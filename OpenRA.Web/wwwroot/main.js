// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser loader for OpenRA.Web: starts the .NET runtime, stages the engine data (and, for local
// testing, the game content) into the emscripten filesystem, boots the engine and drives its frame
// loop from requestAnimationFrame. State is exposed on window.WebRA and body[data-phase] for tests.

import { dotnet } from './_framework/dotnet.js';

const params = new URLSearchParams(location.search);

// Debug URL parameters (?arg=, ?mod=, ?rt=, ?env=, ?interp=, ?luatest=) work only in dev builds
// (build-web.sh WEBRA_DEV=1, data/build.json "dev": true). On a hosted page a crafted link could
// otherwise join the visitor to any server, change settings or disable the interpreter workaround.
let devBuild = false;
const debugParams = name => devBuild ? params.getAll(name) : [];
const debugParam = name => devBuild ? params.get(name) : null;
const canvas = document.getElementById('canvas');
const statusLine = document.getElementById('status');

const WebRA = window.WebRA = {
	phase: 'loading',
	timings: {},
	errors: [],
	host: null,
	diagnostics: (reset = false) => WebRA.host ? JSON.parse(WebRA.host.Diagnostics(reset)) : null,
};

const t0 = performance.now();
function setPhase(phase, detail) {
	WebRA.phase = phase;
	WebRA.timings[phase] = Math.round(performance.now() - t0);
	document.body.dataset.phase = phase;
	statusLine.textContent = detail ? `${phase}: ${detail}` : phase;
	console.log(`[webra] ${phase} at ${WebRA.timings[phase]} ms${detail ? ': ' + detail : ''}`);
}

function fail(error) {
	console.error('[webra] fatal', error);
	WebRA.errors.push(String(error && error.stack || error));
	setPhase('error', String(error && error.message || error));
}

// Lets the page repaint (status text) before a long synchronous call.
const nextPaint = () => new Promise(resolve => requestAnimationFrame(() => setTimeout(resolve, 0)));

async function fetchBundle(name) {
	const index = await fetch(`data/${name}.json`);
	if (!index.ok)
		return null;

	const [files, blob] = await Promise.all([index.json(), fetch(`data/${name}.bin`).then(r => {
		if (!r.ok)
			throw new Error(`Failed to download data/${name}.bin: ${r.status}`);
		return r.arrayBuffer();
	})]);

	return { files: files.files, data: new Uint8Array(blob) };
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

function bootArguments() {
	const width = Number(params.get('w')) || window.innerWidth;
	const height = Number(params.get('h')) || window.innerHeight;
	const args = [
		`Game.Mod=${debugParam('mod') || 'ra'}`,
		'Engine.EngineDir=/openra',
		'Engine.SupportDir=/support',
		'Graphics.Mode=Windowed',
		`Graphics.WindowedSize=${width},${height}`,
		'Graphics.VSync=False',
		// Discord rich presence talks to a local IPC pipe, which a browser cannot do.
		'Game.EnableDiscordService=False',
		// Registers this mod/version as an installed external mod, as the desktop launchers do.
		// The server browser only lets you join servers whose mod and version are registered.
		'Engine.LaunchPath=/openra/openra-web',
	];

	// WebSocket-to-TCP relay for game connections, from the deployment's data/build.json
	// (build-web.sh WEBRA_PROXY_URL). Dev builds also accept ?proxy=ws://... A link must not be able
	// to send a player's game traffic through somebody else's relay.
	const proxy = debugParam('proxy') || (WebRA.build && WebRA.build.proxy);
	if (proxy)
		args.push(`Game.WebSocketProxy=${proxy}`);

	// Extra engine arguments for testing, e.g. ?arg=Graphics.CapFramerate=True&arg=...
	for (const extra of debugParams('arg'))
		args.push(extra);

	return args;
}

function startFrameLoop(host) {
	let stopped = false;

	const frame = () => {
		if (stopped)
			return;

		try {
			if (!host.Frame()) {
				stopped = true;
				setPhase('exited');
				return;
			}
		} catch (e) {
			stopped = true;
			fail(e);
			return;
		}

		schedule();
	};

	// requestAnimationFrame stops in hidden tabs, which would stall the simulation (and, in a
	// lockstep multiplayer game, every other player). Fall back to timers while hidden.
	const schedule = () => {
		if (document.hidden)
			setTimeout(frame, 40);
		else
			requestAnimationFrame(frame);
	};

	schedule();
}

// Game connections: the engine opens a WebSocket to the WebSocket-to-TCP relay through these
// functions (OpenRA.Web/WebSocketTransport.cs). Events go straight back into managed code.
function createNetModule(getTransport) {
	const sockets = new Map();
	let nextId = 1;
	return {
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

async function main() {
	setPhase('runtime');
	const build = WebRA.build = await fetch('data/build.json').then(r => r.ok ? r.json() : {}).catch(() => ({}));
	devBuild = build.dev === true;
	if (!devBuild && [...params.keys()].some(k => ['arg', 'mod', 'rt', 'env', 'interp', 'luatest'].includes(k)))
		console.warn('[webra] debug URL parameters are ignored: this is not a dev build');

	// Extra Mono runtime options for experiments, e.g. ?rt=--no-jiterpreter-traces-enabled
	// and environment variables, e.g. ?env=MONO_GC_PARAMS=nursery-size=64m
	const runtimeOptions = debugParams('rt');
	let builder = dotnet
		.withModuleConfig({ canvas })
		.withRuntimeOptions(runtimeOptions)
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

	const runtime = await builder.create();

	const { Module, getAssemblyExports, setModuleImports } = runtime;
	WebRA.runtime = runtime;
	let transport = null;
	setModuleImports('webra-net', createNetModule(() => transport));

	setPhase('downloading');
	const [engine, content] = await Promise.all([
		fetchBundle('engine'),
		fetchBundle('content'),
	]);
	if (!engine)
		throw new Error('Engine data (data/engine.json) is missing.');

	setPhase('staging');
	Module.FS.mkdirTree('/support');
	let staged = stageBundle(Module.FS, engine);
	Module.FS.writeFile('/openra/openra-web', '');
	if (content)
		staged += stageBundle(Module.FS, content);
	WebRA.stagedFiles = staged;

	const exports = await getAssemblyExports('OpenRA.Web');
	const host = exports.OpenRA && exports.OpenRA.Web && exports.OpenRA.Web.WebHost;
	if (!host)
		throw new Error(`OpenRA.Web exports not found (have: ${Object.keys(exports).join(', ')})`);

	WebRA.exports = host;
	WebRA.allExports = exports;
	transport = exports.OpenRA.Web.WebSocketTransport;
	if (debugParam('luatest') !== null) {
		const r = exports.OpenRA.Web.LuaSelfTest.Run(Number(debugParam('luatest')) || 3);
		console.log('[webra] lua self-test:\n' + r);
	}
	setPhase('booting', 'loading mod and shellmap');
	await nextPaint();
	host.Boot(bootArguments());
	WebRA.host = host;

	setPhase('running');
	canvas.focus();
	startFrameLoop(host);
}

window.addEventListener('error', e => WebRA.errors.push(String(e.error && e.error.stack || e.message)));
window.addEventListener('unhandledrejection', e => WebRA.errors.push(String(e.reason && e.reason.stack || e.reason)));

main().catch(fail);
