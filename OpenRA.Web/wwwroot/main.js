// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser loader for OpenRA.Web. A hosting target's start screen comes first, if the build has one
// (webra-splash.js); then, in parallel: starts the .NET runtime, downloads the engine data,
// and installs the game content (from IndexedDB, or on the first visit from the official OpenRA
// mirrors through our relay). Then it restores the saved support directory (settings, profile,
// replays, maps), boots the engine and drives its frame loop. State is exposed on window.WebRA and
// body[data-phase] for tests.

import { dotnet } from './_framework/dotnet.js';
import { Store, SupportSync, resetSavedData } from './webra-store.js';
import { ensureContent, relayPackageUrl, sitePackageUrl } from './webra-content.js';
import * as page from './webra-page.js';
import { showSplash } from './webra-splash.js';

const params = new URLSearchParams(location.search);

// Every file of the build is addressed relative to the directory this module was loaded from, not to
// the page's address or the site's root: the same build works at the root of a site and under a path
// (https://example.org/webra/), and a page address without the trailing slash or with index.html
// at the end does not change where the files are. Nothing is configured per path; the publisher only
// has to serve the build's files under one directory.
const BASE = new URL('./', import.meta.url);
const at = path => new URL(path, BASE).href;

// IndexedDB is shared by the whole origin, and a game storefront serves several games, possibly several
// builds of this one, from one origin. A build at the root keeps the name earlier builds used ("webra");
// a build under a path gets a database of its own ("webra:/webra/"). data/build.json can name one
// ("storage"), e.g. to keep the players' saved data when the path changes.
const storageNameFor = build => build.storage || (BASE.pathname === '/' ? 'webra' : `webra:${BASE.pathname}`);

// Debug URL parameters (?arg=, ?mod=, ?rt=, ?env=, ?interp=, ?luatest=, ?proxy=) work only in dev
// builds (build-web.sh WEBRA_DEV=1, data/build.json "dev": true). On a hosted page a crafted link
// could otherwise join the visitor to any server, change settings or disable the interpreter workaround.
const DEBUG_PARAMS = ['arg', 'mod', 'rt', 'env', 'interp', 'luatest', 'proxy'];
let devBuild = false;
const debugParams = name => devBuild ? params.getAll(name) : [];
const debugParam = name => devBuild ? params.get(name) : null;
const canvas = document.getElementById('canvas');
// The start screen, in builds with one (build-web.sh WEBRA_SPLASH).
const splash = document.getElementById('splash');
const ui = {
	step: document.getElementById('step'),
	bar: document.getElementById('bar'),
	detail: document.getElementById('detail'),
	note: document.getElementById('note'),
	error: document.getElementById('error'),
	retry: document.getElementById('retry'),
	reset: document.getElementById('reset'),
	fullscreen: document.getElementById('fullscreen'),
	mapsStatus: document.getElementById('maps-status'),
	notice: document.getElementById('notice'),
	noticeText: document.getElementById('notice-text'),
	noticeClose: document.getElementById('notice-close'),
};

const WebRA = window.WebRA = {
	phase: 'loading',
	timings: {},
	load: {},
	errors: [],
	notices: [],
	downloads: [],
	host: null,
	diagnostics: (reset = false) => WebRA.host ? JSON.parse(WebRA.host.Diagnostics(reset)) : null,
};

const t0 = performance.now();
const now = () => Math.round(performance.now() - t0);
function setPhase(phase, detail) {
	WebRA.phase = phase;
	WebRA.timings[phase] = now();
	document.body.dataset.phase = phase;
	if (detail)
		ui.step.textContent = detail;
	console.log(`[webra] ${phase} at ${WebRA.timings[phase]} ms${detail ? ': ' + detail : ''}`);
}

function fail(error) {
	console.error('[webra] fatal', error);
	WebRA.errors.push(String(error && error.stack || error));
	setPhase('error');
	ui.error.textContent = String(error && error.message || error);
}

// A message for the player that stays visible over the game until closed (storage full, background
// clock blocked). Each text is shown once per page load, unless `always` (an error the engine recovered
// from, which the player caused again).
function notify(text, always = false) {
	if (!always && WebRA.notices.includes(text))
		return;

	console.warn(`[webra] notice: ${text}`);
	WebRA.notices.push(text);
	ui.noticeText.textContent = text;
	ui.notice.hidden = false;
}

// Closes the notice if it still shows this text (the problem went away).
function dismiss(text) {
	if (!ui.notice.hidden && ui.noticeText.textContent === text)
		ui.notice.hidden = true;
}

const NOTICES = {
	quota: 'The browser storage for this site is full, so settings, replays and maps are not being saved any more. Free some space (for example, delete old replays in the game) or use "Reset saved data" on the loading screen.',
	write: 'Saving settings, replays or maps in this browser failed; changes may be lost when the page closes.',
	restore: 'The saved settings could not be read, so the game started with default settings. "Reset saved data" on the loading screen clears them.',
	skipped: 'Some saved files could not be restored and were skipped.',
	maps: 'The game\'s built-in maps could not be downloaded yet; the page keeps trying once a minute. Until they arrive, the map list holds only one built-in map and the maps downloaded from the OpenRA resource center, and a lobby on another built-in map shows that map as missing.',
	mapsBroken: 'The game\'s built-in maps could not be added. Reload the page to try again.',
	clock: 'This browser blocked the background clock the game uses in hidden tabs. If you switch to another tab or window during a multiplayer match, the game slows down for every player: keep this tab visible while you play.',
};

// Reset saved data: deletes the saved support directory (settings, profile, replays, maps), keeps
// the game content, reloads. The way out when saved data keeps the page from starting.
async function resetSaved() {
	if (!confirm('Delete the settings, player profile, replays and downloaded maps this site saved in this browser? The game content stays.'))
		return;

	if (WebRA.support)
		WebRA.support.stop();

	try {
		await resetSavedData(WebRA.storageName || storageNameFor(WebRA.build || {}));
		WebRA.resetDone = true;
	} catch (e) {
		WebRA.errors.push(`reset: ${e}`);
		alert(`Resetting the saved data failed: ${e && e.message || e}. Clear this site's data in the browser settings instead.`);
		return;
	}

	location.reload();
}

// "Restart Now" in the game's settings: the browser's restart is a page reload. The engine has just written
// settings.yaml into /support, and the reload restores /support from IndexedDB, so it is saved there first.
// If that fails, the storage notice is up and the page stays as it is: a reload would lose the change.
async function restart() {
	try {
		if (!WebRA.support || await WebRA.support.flush()) {
			location.reload();
			return;
		}
	} catch (e) {
		WebRA.errors.push(`restart: ${e}`);
	}

	console.warn('[webra] restart: the settings could not be saved, so the page was not reloaded');
}

// Quit in the game's menu. With a start screen, the page goes back to it: the engine cannot start again
// in the same page, so it reloads, once what the game saved last is in IndexedDB. If saving fails, the
// storage notice is up and the page stays: a reload would lose it.
async function exited() {
	if (!splash) {
		setPhase('exited', 'The game was closed. Reload the page to play again.');
		return;
	}

	setPhase('exited', 'Returning to the start screen');
	try {
		if (!WebRA.support || await WebRA.support.flush()) {
			location.reload();
			return;
		}
	} catch (e) {
		WebRA.errors.push(`quit: ${e}`);
	}

	ui.step.textContent = 'The game was closed. Reload the page to play again.';
}

// Lets the page repaint (status text) before a long synchronous call. A hidden page gets no animation
// frames at all, so it does not wait for one: a tab opened or restored in the background (session
// restore after a browser crash) used to stop at "Starting the game" until the player looked at it,
// and a test browser attached to such a tab waited for minutes. A visible page can also go without
// animation frames for a while (GPU process restart, offscreen iframe), so the wait is capped.
const NEXT_PAINT_MAX_WAIT_MS = 250;
const nextPaint = () => new Promise(resolve => {
	const started = performance.now();
	let done = false;
	const finish = how => {
		if (done)
			return;
		done = true;
		WebRA.load.bootPaint = { how, ms: Math.round(performance.now() - started) };
		resolve();
	};

	if (document.hidden) {
		setTimeout(() => finish('hidden'), 0);
		return;
	}

	requestAnimationFrame(() => setTimeout(() => finish('frame'), 0));
	setTimeout(() => finish('timeout'), NEXT_PAINT_MAX_WAIT_MS);
});

const MB = n => (n / 1048576).toFixed(1);

// Download progress of the three parallel parts, shown as one bar plus a line per part.
const progress = {
	parts: {},
	set(name, label, received, total) {
		this.parts[name] = { label, received, total };
		this.render();
	},
	render() {
		const parts = Object.values(this.parts);
		const total = parts.reduce((n, p) => n + p.total, 0);
		const received = parts.reduce((n, p) => n + Math.min(p.received, p.total), 0);
		ui.bar.value = total ? received / total : 1;
		ui.detail.textContent = parts.map(p => p.total
			? `${p.label}: ${MB(p.received)} / ${MB(p.total)} MB`
			: p.label).join('\n');
	},
};

// A URL from build.json: absolute, or relative to the build's directory (BASE). ws: true gives a
// WebSocket URL (ws: for an http page, wss: for https).
function resolveUrl(value, ws = false) {
	if (/^wss?:\/\//.test(value))
		return value;

	const url = new URL(value, BASE);
	if (ws)
		url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
	return url.href;
}

const isLoopback = host => ['localhost', '127.0.0.1', '[::1]'].includes(host);

// The relay for game connections. Official builds only use an encrypted (wss:) relay, except on
// loopback for local tests: game traffic must not go in clear text, and https pages block ws: anyway.
function relayUrl(build) {
	const value = debugParam('proxy') || build.proxy;
	if (!value)
		return '';

	const url = resolveUrl(value, true);
	if (!devBuild && !url.startsWith('wss:') && !isLoopback(new URL(url).hostname)) {
		console.error(`[webra] refusing the unencrypted game relay ${url}; multiplayer is disabled`);
		return '';
	}

	return url;
}

// Reads a packed bundle (<prefix>.json index + <prefix>.bin blob), reporting download progress
// to onProgress(received, total). signal (optional) cancels the download.
async function fetchBundle(prefix, onProgress, signal) {
	const index = await fetch(`${prefix}.json`, { signal });
	if (!index.ok)
		return null;

	const { files, size } = await index.json();
	const response = await fetch(`${prefix}.bin`, { signal });
	if (!response.ok)
		throw new Error(`Failed to download ${prefix}.bin: ${response.status}`);

	const data = new Uint8Array(size);
	const reader = response.body.getReader();
	let received = 0;
	onProgress(0, size);
	for (;;) {
		const { done, value } = await reader.read();
		if (done)
			break;
		if (received + value.length > size)
			throw new Error(`${prefix}.bin is larger than its index says`);
		data.set(value, received);
		received += value.length;
		onProgress(received, size);
	}

	if (received !== size)
		throw new Error(`${prefix}.bin is incomplete (${received} of ${size} bytes)`);

	return { files, data };
}

function stageBundle(FS, bundle) {
	const dirs = new Set();
	for (const file of bundle.files) {
		const dir = file.path.substring(0, file.path.lastIndexOf('/'));
		if (!dirs.has(dir)) {
			FS.mkdirTree(dir);
			dirs.add(dir);
		}

		FS.writeFile(file.path, bundle.data.subarray(file.offset, file.offset + file.size));
	}

	return bundle.files.length;
}

function stageFiles(FS, files) {
	let n = 0;
	for (const [path, data] of files) {
		if (path.startsWith('#'))
			continue;
		FS.mkdirTree(path.substring(0, path.lastIndexOf('/')));
		FS.writeFile(path, data);
		n++;
	}

	return n;
}

// The content manifest, downloaded once (the start screen reads it too).
let contentManifestRequest = null;
const contentManifest = () => contentManifestRequest ||= fetch(at(WebRA.build.contentManifest), { cache: 'no-cache' }).then(r => {
	if (!r.ok)
		throw new Error(`Failed to download ${WebRA.build.contentManifest}: ${r.status}`);
	return r.json();
});

// The game content: from IndexedDB, or downloaded from our own site (build.contentUrl) or through the
// relay (build.contentRelay), or, in local test builds, from data/content.*.
async function loadContent(build, store) {
	const started = now();
	if (build.content) {
		const bundle = await fetchBundle(at('data/content'), (received, total) =>
			progress.set('content', 'Game content (local test build)', received, total));
		WebRA.load.content = { source: 'bundle', ms: now() - started };
		return { bundle };
	}

	const manifest = await contentManifest();

	const fromSite = Boolean(build.contentUrl);
	const result = await ensureContent({
		manifest,
		store,
		packageUrl: fromSite ? sitePackageUrl(resolveUrl(build.contentUrl)) : relayPackageUrl(resolveUrl(build.contentRelay || 'content')),
		cache: fromSite ? 'default' : 'no-store',
		onStatus: text => {
			ui.note.hidden = false;
			ui.step.textContent = text;
		},
		onProgress: ({ received, total, mirror }) =>
			progress.set('content', `Game content${mirror ? ` (mirror ${mirror})` : ''}`, received, total || received),
	});

	if (result.source === 'cache')
		progress.set('content', 'Game content: saved in this browser', 0, 0);

	WebRA.load.content = { source: result.source, bytes: result.bytes, downloaded: result.downloaded, mirrors: result.mirrors, ms: now() - started };

	// Chromium grants persistent storage silently to engaged sites; Firefox would ask the player,
	// so it is not requested there.
	if (result.source === 'download' && navigator.storage && navigator.storage.persist && !/Firefox\//.test(navigator.userAgent))
		navigator.storage.persist().then(granted => WebRA.load.persisted = granted).catch(() => {});

	return { files: result.files };
}

function bootArguments(size, proxy) {
	const args = [
		`Game.Mod=${debugParam('mod') || 'ra'}`,
		'Engine.EngineDir=/openra',
		'Engine.SupportDir=/support',
		'Graphics.Mode=Windowed',
		`Graphics.WindowedSize=${size.width},${size.height}`,
		'Graphics.VSync=False',
		// Discord rich presence talks to a local IPC pipe, which a browser cannot do.
		'Game.EnableDiscordService=False',
		// UPnP/NAT-PMP discovery needs sockets: with "true" saved by an older build (the checkbox is disabled
		// now), the engine stopped at every boot with "System.Net.Sockets is not supported on this platform".
		'Server.DiscoverNatDevices=False',
		// emscripten's SDL cannot confine the mouse to the window; the setting is disabled in the browser.
		'Game.LockMouseWindow=False',
		// Registers this mod/version as an installed external mod, as the desktop launchers do.
		// The server browser only lets you join servers whose mod and version are registered.
		'Engine.LaunchPath=/openra/openra-web',
		// Always passed, empty included: settings.yaml is saved now, and a value stored by an
		// earlier build must not override this build's relay.
		`Game.WebSocketProxy=${proxy}`,
	];

	// Extra engine arguments for testing, e.g. ?arg=Graphics.CapFramerate=True&arg=...
	for (const extra of debugParams('arg'))
		args.push(extra);

	return args;
}

// Frame scheduling.
//
// Visible page: one engine frame (logic ticks that are due, then one render) per animation frame,
// plus logic-only frames from the clock below in between.
//
// Hidden page: requestAnimationFrame stops, and main-thread timers are throttled to about one wake-up
// per second (and to one per minute after five minutes in Chrome). In a lockstep multiplayer game a
// client that stops ticking stalls every other player, so while hidden the frames are driven by a
// clock in a dedicated worker, whose timers are not throttled, and run without rendering. The
// worker's messages are ordinary tasks on the main thread, which hidden pages still run promptly.
// The worker is created from a blob: URL, so a Content-Security-Policy must allow worker-src blob:.
const HIDDEN_FRAME_INTERVAL_MS = 10;

// Visible page: the same clock also runs logic-only frames (host.LogicFrame) between animation
// frames. An animation frame comes only after the previous render, so with a slow GPU the logic ticks,
// and with them the network (orders, acks, ping replies), would wait for the render: every other
// player would feel this browser's frame time as latency. The engine runs a tick only when it is due
// (every 40 ms at normal speed), so most of these calls return at once.
const VISIBLE_LOGIC_INTERVAL_MS = 5;

// The worker keeps posting while the main thread is blocked (a map load takes seconds), so its
// messages queue up and then arrive back to back. A logic frame that comes less than this long after
// the previous one ended is skipped: the backlog drains without a managed call per message.
const MIN_LOGIC_FRAME_GAP_MS = 2;

// If the worker cannot run (CSP, browser policy), a main-thread timer chain takes over. Browsers
// throttle it in hidden tabs: measured in Chrome, about one wake-up and so one logic tick per second
// instead of 25. The engine does not catch up on missed ticks (Game.MaxLogicTicksBehind, 250 ms), so
// a hidden tab slows a multiplayer match down for everyone: the player is told when this happens.
function createTimerClock(onTick) {
	let timer = 0;
	let interval = 0;
	const loop = () => {
		timer = setTimeout(loop, interval);
		onTick();
	};

	return {
		start: ms => {
			clearTimeout(timer);
			interval = ms;
			timer = setTimeout(loop, ms);
		},
		stop: () => clearTimeout(timer),
	};
}

function createWorkerClock(onTick) {
	const source = `let timer = 0;
		onmessage = e => {
			clearInterval(timer);
			timer = e.data > 0 ? setInterval(() => postMessage(0), e.data) : 0;
		};`;

	let url = null;
	let worker = null;
	let fallback = null;
	let running = 0;
	const useFallback = reason => {
		if (fallback)
			return;
		console.warn(`[webra] clock worker unavailable (${reason}); hidden tabs and logic frames use a main-thread timer, which browsers throttle in hidden tabs`);
		WebRA.clockFallback = String(reason);
		notify(NOTICES.clock);
		fallback = createTimerClock(onTick);
		if (running)
			fallback.start(running);
	};

	try {
		url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
		worker = new Worker(url);
		worker.onmessage = () => {
			// Revoked only once the worker runs: older Safari fails to load revoked blob URLs.
			if (url) {
				URL.revokeObjectURL(url);
				url = null;
			}

			if (!fallback)
				onTick();
		};
		worker.onerror = e => {
			e.preventDefault();
			useFallback(e.message || 'worker error');
		};
	} catch (e) {
		useFallback(e && e.message || e);
	}

	return {
		get kind() {
			return fallback ? 'timer' : 'worker';
		},
		start: interval => {
			running = interval;
			if (fallback)
				fallback.start(interval);
			else
				worker.postMessage(interval);
		},
		stop: () => {
			running = 0;
			if (fallback)
				fallback.stop();
			else if (worker)
				worker.postMessage(0);
		},
	};
}

// Returns sleep(ms), a timeout for other page work (the maps loader) that also ends at the first clock
// tick after its time: browsers throttle main-thread timers in hidden tabs (one wake-up per second, one
// per minute after five minutes in Chrome), the clock's worker timer is not. onExit runs once the game
// has quit.
function startFrameLoop(host, onExit) {
	let stopped = false;
	let rafPending = false;
	const stats = WebRA.frameStats = { visibleFrames: 0, logicFrames: 0, skippedLogicFrames: 0, hiddenFrames: 0, visibilityChanges: 0 };
	let lastLogicFrameEnd = -Infinity;

	// render: true (animation frame), false (hidden page), 'logic' (visible page, between animation frames)
	const runFrame = render => {
		if (stopped)
			return;

		try {
			if (!(render === 'logic' ? host.LogicFrame() : host.Frame(render))) {
				stopped = true;
				clock.stop();
				onExit();
			}
		} catch (e) {
			stopped = true;
			clock.stop();
			fail(e);
		}
	};

	const onAnimationFrame = () => {
		rafPending = false;
		if (stopped || document.hidden)
			return;

		stats.visibleFrames++;
		runFrame(true);
		requestFrame();
	};

	const clock = WebRA.clock = createWorkerClock(() => {
		// Messages still queued from before a visibility change run as the new state's frame kind.
		if (document.hidden) {
			stats.hiddenFrames++;
			runFrame(false);
		} else if (performance.now() - lastLogicFrameEnd < MIN_LOGIC_FRAME_GAP_MS) {
			stats.skippedLogicFrames++;
		} else {
			stats.logicFrames++;
			runFrame('logic');
			lastLogicFrameEnd = performance.now();
		}

		wakeSleepers();
	});

	const sleepers = new Set();
	const wakeSleepers = () => {
		if (sleepers.size === 0)
			return;

		const t = performance.now();
		for (const sleeper of sleepers)
			if (t >= sleeper.at)
				sleeper.wake();
	};

	const sleep = ms => new Promise(resolve => {
		const sleeper = {
			at: performance.now() + ms,
			wake: () => {
				sleepers.delete(sleeper);
				clearTimeout(sleeper.timer);
				resolve();
			},
		};

		// Also ends the wait when the clock has stopped (the game exited).
		sleeper.timer = setTimeout(sleeper.wake, ms);
		sleepers.add(sleeper);
	});

	const requestFrame = () => {
		if (!rafPending && !stopped) {
			rafPending = true;
			requestAnimationFrame(onAnimationFrame);
		}
	};

	const onVisibility = () => {
		stats.visibilityChanges++;
		if (stopped)
			return;

		if (document.hidden)
			clock.start(HIDDEN_FRAME_INTERVAL_MS);
		else {
			clock.start(VISIBLE_LOGIC_INTERVAL_MS);
			requestFrame();
		}
	};

	document.addEventListener('visibilitychange', onVisibility);
	if (document.hidden)
		clock.start(HIDDEN_FRAME_INTERVAL_MS);
	else {
		clock.start(VISIBLE_LOGIC_INTERVAL_MS);
		requestFrame();
	}

	return { sleep };
}

// Game connections: the engine opens a WebSocket to the WebSocket-to-TCP relay through these
// functions (OpenRA.Web/WebSocketTransport.cs). Events go straight back into managed code.
function createNetModule(getTransport) {
	const sockets = new Map();
	let nextId = 1;
	return {
		openCount: () => sockets.size,
		open(url) {
			const id = nextId++;
			const ws = new WebSocket(url);
			ws.binaryType = 'arraybuffer';
			ws.onopen = () => getTransport().OnOpen(id);
			ws.onmessage = e => getTransport().OnMessage(id, new Uint8Array(e.data));
			ws.onclose = e => {
				sockets.delete(id);
				getTransport().OnClose(id, e.code, e.reason || '');
			};
			sockets.set(id, ws);
			return id;
		},
		send(id, view) {
			const ws = sockets.get(id);
			// slice() copies the bytes out of the managed heap before the call returns.
			if (ws && ws.readyState === WebSocket.OPEN)
				ws.send(view.slice());
		},
		close(id) {
			const ws = sockets.get(id);
			if (!ws)
				return;

			sockets.delete(id);
			ws.onopen = ws.onmessage = ws.onclose = null;
			ws.close();
		},
	};
}

// The bundled maps, all but the shellmap and one small lobby map (10.8 MB, 2.4 MB compressed), come in a
// second package that is downloaded once the main menu is up, so that the first visit does not wait
// for them (build-web.sh, tools/split-maps.py). Until they are in, the map chooser lists only that one
// map and the maps this browser downloaded before, the mission browser is empty (a list opened
// meanwhile fills in when it is opened again), and a lobby or replay on a bundled map shows it as
// missing: the engine searches
// the OpenRA resource center for it, which does not carry the bundled maps of playtest releases. The
// lobby and the replay browser pick the map up by themselves once it is loaded. A status line shows
// the progress outside matches.
// The maps are added to the engine's map cache a few at a time between frames, never during a match
// or a replay; in a hidden tab the frame clock paces them (the player may wait in another tab).
const MAP_LOAD_BUDGET_MS = 8;
// Download attempts: three over about 15 s, then the player is told and the page keeps trying once a
// minute, and at once when the browser reports that the network is back (a phone switching from Wi-Fi
// to mobile data), for as long as it is open.
const MAP_DOWNLOAD_ATTEMPTS = 3;
const MAP_RETRY_INTERVAL_MS = 60000;

async function loadDeferredMaps(prefix, FS, host, sleep) {
	const state = WebRA.maps = { state: 'downloading', received: 0, total: 0, maps: 0, loaded: 0, startedMs: now() };
	const inMatch = () => {
		try {
			return host.WorldType() === 'Regular';
		} catch {
			return false;
		}
	};

	const show = () => {
		const busy = state.state === 'downloading' || state.state === 'retrying' || state.state === 'loading';
		ui.mapsStatus.hidden = !busy || inMatch();
		if (state.state === 'loading')
			ui.mapsStatus.textContent = `Adding maps: ${state.loaded} / ${state.maps}`;
		else if (state.state === 'retrying')
			ui.mapsStatus.textContent = `Maps download failed, trying again in ${Math.max(0, Math.ceil((state.retryAt - now()) / 1000))} s`;
		else
			ui.mapsStatus.textContent = state.total ? `Downloading maps: ${MB(state.received)} / ${MB(state.total)} MB` : 'Downloading maps';
	};

	const statusTimer = setInterval(show, 1000);
	show();
	try {
		// Neither downloaded nor staged during a match: the transfer would compete with the game's
		// connection, and staging blocks the page for tens of milliseconds. A download still running
		// when a match starts is cancelled and a finished one dropped; both start again once the match
		// is over (the second time from the HTTP cache). The 10.8 MB bundle is released once staged:
		// the file system holds its own copy.
		let files = null;
		for (let attempt = 1; !files;) {
			if (WebRA.phase !== 'running')
				return;
			while (inMatch()) {
				if (WebRA.phase !== 'running')
					return;
				await sleep(1000);
			}

			const download = new AbortController();
			let downloading = true;
			(async () => {
				while (downloading) {
					await sleep(1000);
					if (downloading && inMatch())
						download.abort(new Error('a match started'));
				}
			})();

			let bundle = null;
			try {
				bundle = await fetchBundle(prefix, (received, total) => {
					state.received = received;
					state.total = total;
					show();
				}, download.signal);
				if (!bundle)
					throw new Error(`${prefix}.json is missing`);
			} catch (e) {
				state.received = 0;
				if (download.signal.aborted) {
					state.cancelledInMatch = (state.cancelledInMatch || 0) + 1;
					continue;
				}

				WebRA.errors.push(`maps (attempt ${attempt}): ${e && e.message || e}`);
				console.warn(`[webra] maps download attempt ${attempt} failed:`, e);
				if (attempt === MAP_DOWNLOAD_ATTEMPTS)
					notify(NOTICES.maps);
				const delay = attempt < MAP_DOWNLOAD_ATTEMPTS ? 5000 * attempt : MAP_RETRY_INTERVAL_MS;
				state.state = 'retrying';
				state.retryAt = now() + delay;
				show();
				let online = null;
				await Promise.race([sleep(delay), new Promise(resolve => {
					online = resolve;
					window.addEventListener('online', online);
				})]);
				window.removeEventListener('online', online);
				state.state = 'downloading';
				show();
				attempt++;
				continue;
			} finally {
				downloading = false;
			}

			// This check and the staging run in one task, so a match cannot start in between.
			if (inMatch()) {
				state.droppedInMatch = (state.droppedInMatch || 0) + 1;
				continue;
			}

			state.downloadedMs = now();
			stageBundle(FS, bundle);
			files = bundle.files;
			bundle = null;
		}

		// Every top-level entry of a mod's map folder is a map (an .oramap file or a directory).
		const folders = new Map();
		for (const file of files) {
			const m = /^(\/openra\/mods\/[^/]+\/maps)\/([^/]+)/.exec(file.path);
			if (!m)
				continue;
			if (!folders.has(m[1]))
				folders.set(m[1], new Set());
			folders.get(m[1]).add(m[2]);
		}

		for (const [folder, maps] of folders)
			state.maps = host.AddMaps(folder, [...maps]);

		state.state = 'loading';
		let pending = state.maps;
		while (pending > 0) {
			if (WebRA.phase !== 'running')
				return;

			if (inMatch()) {
				await sleep(1000);
				continue;
			}

			pending = host.LoadPendingMaps(MAP_LOAD_BUDGET_MS);
			state.loaded = state.maps - pending;
			show();
			await sleep(16);
		}

		state.state = 'ready';
		state.readyMs = now();
		dismiss(NOTICES.maps);
		console.log(`[webra] ${state.maps} maps added: downloaded at ${state.downloadedMs} ms, ready at ${state.readyMs} ms`);
	} catch (e) {
		state.state = 'failed';
		state.error = String(e && e.message || e);
		console.error('[webra] maps', e);
		notify(NOTICES.mapsBroken);
	} finally {
		clearInterval(statusTimer);
		show();
	}
}

// The fullscreen button shows outside matches (menus, lobby) while not in fullscreen. In a match:
// Alt+Enter, or the browser's own fullscreen.
function watchFullscreenButton() {
	ui.fullscreen.addEventListener('click', () => {
		page.toggleFullscreen();
		canvas.focus();
	});

	const update = () => {
		let inMatch = false;
		try {
			inMatch = WebRA.host.WorldType() === 'Regular';
		} catch {
			// Not booted yet.
		}

		ui.fullscreen.hidden = page.isFullscreen() || inMatch;
	};

	setInterval(update, 1000);
	document.addEventListener('fullscreenchange', update);
	update();
}

// Files the game writes for the player that also go to the browser's Downloads, as soon as each is complete:
// screenshots and replays. Maps the game downloads to play on a server stay inside the browser.
const SAVED_FOR_PLAYER = ['/support/Screenshots/', '/support/Replays/'];

async function main() {
	setPhase(splash ? 'splash' : 'loading', 'Starting');
	ui.retry.addEventListener('click', () => location.reload());
	ui.reset.addEventListener('click', () => resetSaved());
	ui.noticeClose.addEventListener('click', () => {
		ui.notice.hidden = true;
	});

	const build = WebRA.build = await fetch(at('data/build.json'), { cache: 'no-cache' }).then(r => r.ok ? r.json() : {}).catch(() => ({}));
	devBuild = build.dev === true;
	const storageName = WebRA.storageName = storageNameFor(build);
	if (build.contentUrl)
		ui.note.textContent = 'First visit: the original Red Alert game files (freeware), about 13 MB, are downloaded from this site and kept in this browser, so later visits start without downloading them again.';
	if (!devBuild && [...params.keys()].some(k => DEBUG_PARAMS.includes(k)))
		console.warn('[webra] debug URL parameters are ignored: this is not a dev build');

	// Browser integration that must be in place before the runtime starts (the audio context is
	// created during boot, the version check and news requests soon after).
	page.unlockAudio();
	page.keepPageTitle();
	page.routeMasterRequests(build.masterRelay ? resolveUrl(build.masterRelay) : '');
	page.suppressBrowserUi(canvas);
	page.captureKeys({ isActive: () => WebRA.phase === 'running', keepDevTools: devBuild });
	page.clampMouseAtEdges(canvas, () => WebRA.phase === 'running');

	const proxy = relayUrl(build);
	const storePromise = Store.open(storageName);
	if (splash) {
		WebRA.splash = {};
		const choice = await showSplash(splash, { build, at, storePromise, contentManifest, state: WebRA.splash });
		WebRA.splash.fullscreen = choice.fullscreen;
		setPhase('loading', 'Starting');
	}

	// Extra Mono runtime options for experiments, e.g. ?rt=--no-jiterpreter-traces-enabled
	// and environment variables, e.g. ?env=MONO_GC_PARAMS=nursery-size=64m
	const frameworkBytes = build.frameworkBytes || 12e6;
	progress.set('framework', 'Game engine', 0, frameworkBytes);
	let builder = dotnet
		.withModuleConfig({
			canvas,
			onDownloadResourceProgress: (loaded, total) =>
				progress.set('framework', 'Game engine', Math.round((total ? loaded / total : 0) * frameworkBytes), frameworkBytes),
		})
		// The loader would fetch every resource with cache: 'no-cache' (a revalidation per file per
		// visit). The _framework files are fingerprinted and served as immutable (deploy/webra.caddy),
		// and the unfingerprinted dotnet.js is revalidated by its own Cache-Control header.
		.withConfig({ disableNoCacheFetch: true })
		.withRuntimeOptions(debugParams('rt'))
		// Mono's interpreter (.NET 10.0.14) corrupts memory during OpenRA's Lua (Eluant) callbacks when
		// methods tier up while re-entered from native code: "memory access out of bounds" in Chrome,
		// a renderer crash in headless Chromium. Compiling every method optimized from the start
		// (no tiering) avoids it. Dev builds can override it with ?interp=<options>; see phase0-report.md.
		// WebHost.Boot refuses to start a non-dev build without it (BootChecks.cs).
		.withEnvironmentVariable('MONO_INTERPRETER_OPTIONS', debugParam('interp') ?? '-tiering');
	for (const env of debugParams('env')) {
		const eq = env.indexOf('=');
		builder = builder.withEnvironmentVariable(env.substring(0, eq), env.substring(eq + 1));
	}

	setPhase('downloading', 'Downloading');
	// A replay launched at boot (dev builds: ?arg=Launch.Replay=...) needs its map before the engine
	// starts: then the maps package is part of the loading screen, as before it was split off.
	const mapsAtBoot = Boolean(build.mapData) && debugParams('arg').some(a => a.startsWith('Launch.Replay='));
	const [runtime, engine, content, store, mapsBundle] = await Promise.all([
		builder.create().then(r => {
			WebRA.load.frameworkMs = now();
			progress.set('framework', 'Game engine', frameworkBytes, frameworkBytes);
			return r;
		}),
		fetchBundle(at(build.engineData || 'data/engine'), (received, total) =>
			progress.set('engine', 'Engine data', received, total)).then(b => {
			WebRA.load.engineMs = now();
			return b;
		}),
		storePromise.then(s => loadContent(build, s)),
		storePromise,
		mapsAtBoot ? fetchBundle(at(build.mapData), (received, total) => progress.set('maps', 'Maps', received, total)) : null,
	]);

	if (!engine)
		throw new Error('Engine data is missing.');

	const { Module, getAssemblyExports, setModuleImports } = runtime;
	WebRA.runtime = runtime;
	let transport = null;
	const net = createNetModule(() => transport);
	setModuleImports('webra-net', net);
	// The engine shows errors it recovered from (a UI action that failed) as a notice, asks for a
	// restart ("Restart Now" in its settings) through restart() and copies the forum key with copyText().
	setModuleImports('webra-page', {
		notify: text => notify(text, true),
		restart: () => restart(),
		copyText: text => page.copyText(text, canvas),
	});
	page.guardLeaving(() => net.openCount() > 0 && WebRA.phase !== 'exited');

	setPhase('staging', 'Installing');
	const FS = Module.FS;
	FS.mkdirTree('/support');
	let staged = stageBundle(FS, engine);
	FS.writeFile('/openra/openra-web', '');
	staged += content.bundle ? stageBundle(FS, content.bundle) : stageFiles(FS, content.files);
	if (mapsBundle)
		staged += stageBundle(FS, mapsBundle);

	const support = WebRA.support = new SupportSync(FS, store, { onError: kind => notify(NOTICES[kind] || NOTICES.write) });
	await support.restore();
	if (support.stats.skipped.length)
		notify(NOTICES.skipped);
	WebRA.stagedFiles = staged;
	WebRA.load.restoredSupportFiles = support.stats.restoredFiles;
	// After the restore: the files restored from IndexedDB are not new.
	page.downloadSavedFiles(FS, SAVED_FOR_PLAYER, WebRA.downloads);

	const exports = await getAssemblyExports('OpenRA.Web');
	const host = exports.OpenRA && exports.OpenRA.Web && exports.OpenRA.Web.WebHost;
	if (!host)
		throw new Error(`OpenRA.Web exports not found (have: ${Object.keys(exports).join(', ')})`);

	WebRA.exports = host;
	WebRA.allExports = exports;
	transport = exports.OpenRA.Web.WebSocketTransport;
	WebRA.net = exports.OpenRA.Web.NetDiagnostics;
	WebRA.netStatus = () => JSON.parse(WebRA.net.Status());
	if (debugParam('luatest') !== null) {
		const r = exports.OpenRA.Web.LuaSelfTest.Run(Number(debugParam('luatest')) || 3);
		console.log('[webra] lua self-test:\n' + r);
	}

	setPhase('booting', 'Starting the game (the page does not respond for a few seconds)');
	await nextPaint();
	// The render size is fixed at boot, so it is taken now, from the window as it is (fullscreen when
	// the player chose it). ?w= and ?h= (render size for tests) are clamped: a crafted link must not
	// make the tab allocate a huge canvas.
	const sizeParam = name => Math.min(4096, Number(params.get(name)) || 0);
	const size = WebRA.renderSize = page.renderSize(sizeParam('w') || window.innerWidth, sizeParam('h') || window.innerHeight);
	page.fitCanvas(canvas, size);
	host.Boot(bootArguments(size, proxy));
	WebRA.host = host;

	// Settings, profile, replays and maps are saved from now on.
	support.start();
	support.sync();

	setPhase('running');
	canvas.focus();
	watchFullscreenButton();
	const { sleep } = startFrameLoop(host, exited);
	if (build.mapData && !mapsBundle)
		loadDeferredMaps(at(build.mapData), FS, host, sleep);
}

// Content-Security-Policy violations (deploy/webra.caddy) are recorded for tests and bug reports.
document.addEventListener('securitypolicyviolation', e => WebRA.errors.push(`CSP: ${e.violatedDirective} blocked ${e.blockedURI}`));
window.addEventListener('error', e => WebRA.errors.push(String(e.error && e.error.stack || e.message)));
window.addEventListener('unhandledrejection', e => WebRA.errors.push(String(e.reason && e.reason.stack || e.reason)));

main().catch(fail);
