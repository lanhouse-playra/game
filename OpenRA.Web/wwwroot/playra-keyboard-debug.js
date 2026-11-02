// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// The on-screen keyboard's diagnostics (?debug=keyboard or #debug=keyboard in the page's address): a small panel over
// the game with the last events on the page's text input (playra-keyboard.js), the state of the engine's field and the
// keyboard's counters. It is meant for phones, which have no developer tools to look at: a screenshot of the panel
// shows what the browser delivered and what the engine did with it. Off by default. The panel takes no touches and
// no focus, and the text of a password field is never shown.

export function keyboardDebugRequested(location) {
	const flag = value => value !== null && value.split(',').includes('keyboard');
	const hash = location.hash.startsWith('#') ? location.hash.substring(1) : location.hash;
	return flag(new URLSearchParams(location.search).get('debug')) || flag(new URLSearchParams(hash).get('debug'));
}

export function showKeyboardDiagnostics(keyboard) {
	const panel = document.createElement('pre');
	panel.id = 'keyboard-debug';
	panel.setAttribute('aria-hidden', 'true');
	panel.style.cssText = 'position: fixed; left: 0; top: 0; z-index: 30; margin: 0; padding: 4px 6px; max-width: min(62vw, 520px); ' +
		'box-sizing: border-box; font: 10px/1.25 ui-monospace, Menlo, monospace; color: #dfd; background: rgba(0, 0, 0, 0.72); ' +
		'white-space: pre-wrap; word-break: break-all; pointer-events: none; user-select: none; -webkit-user-select: none; transform-origin: 0 0;';
	document.body.appendChild(panel);

	const standalone = navigator.standalone === true || !!(window.matchMedia && window.matchMedia('(display-mode: standalone)').matches);
	const platform = (/(iPhone|iPad|iPod|Android)[^;)]*/.exec(navigator.userAgent) || [navigator.platform])[0];
	const browser = (/(CriOS|FxiOS|EdgiOS|Version)\/[\d.]+/.exec(navigator.userAgent) || [''])[0];
	const header = `playra keyboard debug | ${platform} ${browser}${standalone ? ' app' : ''}`;

	const rect = r => `${Math.round(r.x)},${Math.round(r.y)} ${Math.round(r.width)}x${Math.round(r.height)}`;
	const render = () => {
		let d;
		try {
			d = keyboard.diagnostics();
		} catch (e) {
			panel.textContent = `${header}\ndiagnostics failed: ${e && e.message || e}`;
			return;
		}

		const s = d.stats;
		const vv = window.visualViewport;
		const lines = [
			header,
			`active=${d.active} open=${d.open}${d.composing ? ' composing' : ''} input ${d.input.type} ${d.input.value} sel=${d.input.selection.join(',')}`,
			`engine: ${d.engine ? `${d.engine.id || d.engine.kind} ${d.engine.text} cursor=${d.engine.cursor} max=${d.engine.maxLength}` : 'no focused field'}`,
			`taps=${s.taps} opened=${s.opened} closed=${s.closed} typed=${s.typed} keys=${s.keys} bksp=${s.backspaces} resyncs=${s.resyncs} errors=${s.errors}`,
			s.lastError ? `last error: ${s.lastError}` : 'last error: none',
			`view ${vv ? `${Math.round(vv.width)}x${Math.round(vv.height)}+${Math.round(vv.offsetLeft)}+${Math.round(vv.offsetTop)}` : 'n/a'} scroll ${Math.round(window.scrollX)},${Math.round(window.scrollY)} input ${rect(d.input.rect)}`,
			'--',
			...keyboard.trace,
		];
		panel.textContent = lines.join('\n');
	};

	// In the visible part of the page: with the keyboard up a phone pans and shrinks the visual viewport, and a fixed
	// element would stay behind in the layout viewport, possibly under the keyboard.
	const follow = () => {
		const vv = window.visualViewport;
		if (!vv)
			return;

		const inset = 4;
		panel.style.transform = `translate(${Math.round(vv.offsetLeft + inset)}px, ${Math.round(vv.offsetTop + inset)}px) scale(${1 / Math.max(0.1, vv.scale)})`;
	};

	keyboard.onTrace(render);
	if (window.visualViewport) {
		window.visualViewport.addEventListener('resize', follow);
		window.visualViewport.addEventListener('scroll', follow);
	}

	// The engine's field also changes without an event on the input (the game edits it, focus moves).
	setInterval(render, 250);
	follow();
	render();
	return panel;
}
