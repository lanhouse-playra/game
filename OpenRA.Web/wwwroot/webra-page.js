// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser integration of the game page: canvas size, fullscreen, keys the browser would otherwise
// take, audio unlock, the mouse at the window edges, and the master server requests that need our
// relay. None of it changes what the engine computes; it only changes what the browser does around it.

// The engine needs at least this effective resolution for its menus (WorldViewportSizes).
const MIN_WIDTH = 1024;
const MIN_HEIGHT = 720;

// The size the engine renders at, chosen once at boot from the window size. OpenRA cannot resize its
// window while running, so later window changes scale the canvas (keeping its aspect ratio) instead.
// A window smaller than the menus need gets a larger render size scaled down to fit.
export function renderSize(width, height) {
	width = Math.max(1, Math.round(width));
	height = Math.max(1, Math.round(height));
	const scale = Math.max(1, MIN_WIDTH / width, MIN_HEIGHT / height);
	return { width: Math.round(width * scale), height: Math.round(height * scale) };
}

// Fits the canvas into the window, letterboxed, centred. SDL maps mouse coordinates from the
// canvas's CSS size to the engine's window size, so input follows the scaling.
export function fitCanvas(canvas, size) {
	const fit = () => {
		const aspect = size.width / size.height;
		let w = window.innerWidth;
		let h = w / aspect;
		if (h > window.innerHeight) {
			h = window.innerHeight;
			w = h * aspect;
		}

		canvas.style.width = `${Math.floor(w)}px`;
		canvas.style.height = `${Math.floor(h)}px`;
	};

	fit();
	window.addEventListener('resize', fit);
	document.addEventListener('fullscreenchange', fit);
	return fit;
}

export function isFullscreen() {
	return !!document.fullscreenElement;
}

export async function toggleFullscreen() {
	try {
		if (isFullscreen()) {
			await document.exitFullscreen();
			return;
		}

		await document.documentElement.requestFullscreen({ navigationUI: 'hide' });

		// Chrome: in fullscreen, keyboard lock gives the game Escape and the browser shortcuts
		// (holding Escape still leaves fullscreen).
		if (navigator.keyboard && navigator.keyboard.lock)
			navigator.keyboard.lock().catch(() => {});
	} catch (e) {
		console.warn('[webra] fullscreen failed:', e);
	}
}

// Keys the game binds that browsers would act on: F1-F12 (all are default hotkeys, with and without
// Ctrl), Tab, Ctrl+letter/digit (control groups, and Ctrl+S/F/P/D/... would open browser dialogs),
// Alt+arrows (history navigation), a lone Alt (Firefox shows its menu bar) and Alt with a letter, a
// digit or a punctuation key (see altGameKey). The events still reach the game; only the browser's
// default action is cancelled. Some shortcuts (Ctrl+W, Ctrl+T, Ctrl+N) cannot be cancelled by a page
// outside fullscreen keyboard lock.
// Alt+Enter toggles fullscreen.
export function captureKeys({ isActive, keepDevTools = false }) {
	// Alt+letter, Alt+digit (top row) and Alt+punctuation: the default hotkeys use Alt+A/S/D/F (unit
	// stances), Alt+Q/W/E/R (restore a map bookmark), Alt+1-0 (jump to a control group) and Alt+Shift+=
	// (hide the interface), and any of these keys can be bound with Alt. Browsers act on some of them:
	// Alt+F and Alt+E open Chrome's menu, Alt+D focuses the address bar, Alt+1-8 switch tabs in Chrome on
	// Linux, and Firefox's menu bar takes Alt+letters. Left to the browser: AltGr (it types characters on
	// many keyboard layouts), the numeric keypad (Windows Alt codes) and macOS, where Option+key types
	// characters (accents in chat) and browsers bind nothing to it; the game gets those keys there anyway.
	const mac = /Mac|iPhone|iPad/.test(navigator.platform || '');
	const altKeys = /^(Key[A-Z]|Digit[0-9]|Minus|Equal|BracketLeft|BracketRight|Backslash|Semicolon|Quote|Backquote|Comma|Period|Slash|IntlBackslash)$/;
	const altGameKey = e => !mac && e.altKey && !e.ctrlKey && !e.metaKey &&
		!(e.getModifierState && e.getModifierState('AltGraph')) && altKeys.test(e.code);

	const onKeyDown = e => {
		if (!isActive())
			return;

		if (e.key === 'Enter' && e.altKey) {
			e.preventDefault();
			toggleFullscreen();
			return;
		}

		const fkey = /^F([1-9]|1[0-2])$/.test(e.key);
		if (keepDevTools && (e.key === 'F12' || (e.ctrlKey && e.shiftKey && /^[ijc]$/i.test(e.key))))
			return;

		if (fkey || e.key === 'Tab' || (e.altKey && e.key.startsWith('Arrow')) || e.key === 'Alt' || altGameKey(e))
			e.preventDefault();
		else if ((e.ctrlKey || e.metaKey) && !/^[cvx]$/i.test(e.key))
			e.preventDefault();
	};

	const onKeyUp = e => {
		if (isActive() && (e.key === 'Alt' || /^F([1-9]|1[0-2])$/.test(e.key)))
			e.preventDefault();
	};

	// Ctrl+wheel and pinch would zoom the page.
	const onWheel = e => {
		if (isActive() && e.ctrlKey)
			e.preventDefault();
	};

	window.addEventListener('keydown', onKeyDown, true);
	window.addEventListener('keyup', onKeyUp, true);
	window.addEventListener('wheel', onWheel, { capture: true, passive: false });
}

// Emscripten's OpenAL resumes its AudioContext on the first keydown/mousedown/touchstart. This also
// covers pointer and touch-end gestures (Safari unlocks audio on touchend) and contexts that
// were suspended later, e.g. by the browser.
export function unlockAudio() {
	const contexts = new Set();
	const Original = window.AudioContext || window.webkitAudioContext;
	if (!Original)
		return { contexts };

	class TrackedAudioContext extends Original {
		constructor(...args) {
			super(...args);
			contexts.add(this);
		}
	}

	window.AudioContext = TrackedAudioContext;
	if (window.webkitAudioContext)
		window.webkitAudioContext = TrackedAudioContext;

	const resume = () => {
		for (const ctx of contexts)
			if (ctx.state === 'suspended')
				ctx.resume().catch(() => {});
	};

	for (const type of ['pointerdown', 'pointerup', 'keydown', 'touchend', 'click'])
		window.addEventListener(type, resume, true);

	return { contexts };
}

// Edge panning in a window: when the mouse leaves the canvas, the engine would keep the last
// position it saw, which may be a few pixels short of the edge if the mouse moved fast. This moves
// the engine's cursor onto the edge where the mouse left, so pushing the mouse out of the window
// pans the map as it does in a desktop window. In fullscreen the mouse cannot leave the canvas.
// The engine only pans while the window has input focus, so clicking outside the page stops it.
export function clampMouseAtEdges(canvas, isActive) {
	canvas.addEventListener('mouseleave', e => {
		if (!isActive() || e.buttons)
			return;

		const r = canvas.getBoundingClientRect();
		const x = Math.min(Math.max(e.clientX, r.left), r.right - 1);
		const y = Math.min(Math.max(e.clientY, r.top), r.bottom - 1);
		canvas.dispatchEvent(new MouseEvent('mousemove', {
			bubbles: true, cancelable: true, clientX: x, clientY: y, screenX: e.screenX, screenY: e.screenY,
			ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey,
		}));
	});
}

// The engine asks master.openra.net for the version check and the news, which do not allow
// cross-origin requests from pages. The runtime's HttpClient calls globalThis.fetch for each request,
// so those two URLs are sent to our relay's master route instead (same query string).
export function routeMasterRequests(relayBase) {
	if (!relayBase)
		return;

	const base = relayBase.replace(/\/$/, '');
	const routes = [
		['https://master.openra.net/versioncheck?', `${base}/versioncheck?`],
		['https://master.openra.net/gamenews?', `${base}/gamenews?`],
	];

	const original = globalThis.fetch.bind(globalThis);
	globalThis.fetch = (input, init) => {
		const url = typeof input === 'string' ? input : input instanceof URL ? input.href : null;
		if (url) {
			for (const [from, to] of routes)
				if (url.startsWith(from))
					return original(to + url.substring(from.length), init);
		}

		return original(input, init);
	};
}

// Prevents the browser's own menus and gestures over the game.
export function suppressBrowserUi(canvas) {
	for (const type of ['contextmenu', 'dragstart', 'selectstart'])
		document.addEventListener(type, e => e.preventDefault());

	canvas.style.touchAction = 'none';
}

// Asks before leaving the page (closing the tab, reload, back) while connected to a game server.
export function guardLeaving(isConnected) {
	window.addEventListener('beforeunload', e => {
		if (isConnected()) {
			e.preventDefault();
			e.returnValue = '';
		}
	});
}
