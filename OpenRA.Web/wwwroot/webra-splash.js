// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// The start screen of a hosting target (build-web.sh WEBRA_SPLASH puts its markup into index.html): two
// buttons, windowed and fullscreen, and nothing is downloaded until one is clicked. It also says how much
// a first visit downloads, or that everything is already in this browser, and replaces the buttons with
// an explanation when the browser has no WebGL 2. Its texts are in the markup (data-* attributes for
// the ones chosen here), so the target's language stays in the target's file.

import { contentStored } from './webra-content.js';
import * as page from './webra-page.js';

// The engine renders with WebGL 2 (OpenGL ES 3.0). A context made on a canvas of its own and released
// at once: the game canvas must get its context from SDL, with SDL's attributes.
export function hasWebGL2() {
	try {
		const gl = document.createElement('canvas').getContext('webgl2');
		if (!gl)
			return false;

		const lose = gl.getExtension('WEBGL_lose_context');
		if (lose)
			lose.loseContext();
		return true;
	} catch {
		return false;
	}
}

// A file the browser has in its HTTP cache, fresh or not, read without going to the network. Browsers
// without the cache mode reject the request; then the file counts as missing.
async function inHttpCache(url) {
	try {
		const response = await fetch(url, { cache: 'only-if-cached', mode: 'same-origin' });
		if (response.body)
			response.body.cancel().catch(() => {});
		return response.ok;
	} catch {
		return false;
	}
}

// True when this browser holds everything a first visit downloads before the main menu (build.json
// firstVisit, tools/publish.py): every content-addressed file in the HTTP cache (of the ICU data shards,
// one) and the game content in IndexedDB. Anything missing (a new release, an evicted cache, storage cleared) counts as a first visit.
export async function alreadyDownloaded(build, at, storePromise, contentManifest) {
	const firstVisit = build.firstVisit;
	if (!firstVisit || !Array.isArray(firstVisit.cached))
		return false;

	const content = build.content ? Promise.resolve(true) : Promise.all([storePromise, contentManifest()])
		.then(([store, manifest]) => contentStored(manifest, store))
		.catch(() => false);

	// cachedOneOf: the runtime's ICU data comes in shards, and a browser loads the one for its language.
	const oneOf = Array.isArray(firstVisit.cachedOneOf) ? firstVisit.cachedOneOf : [];
	const anyOf = oneOf.length ? Promise.all(oneOf.map(key => inHttpCache(at(key)))).then(found => found.some(Boolean)) : true;
	const found = await Promise.all([content, anyOf, ...firstVisit.cached.map(key => inHttpCache(at(key)))]);
	return found.every(Boolean);
}

// The engine's render size is fixed at boot, so in fullscreen it waits for the window to take its new
// size: the browser resizes it a little after the request is granted. Done when the window has the
// screen's size, or once it stopped changing for a moment after a resize, at most maxMs.
function waitForFullscreenSize(maxMs = 1500, settleMs = 250) {
	return new Promise(resolve => {
		const started = performance.now();
		let resized = 0;
		const onResize = () => resized = performance.now();
		const check = () => {
			const t = performance.now();
			const full = window.innerWidth >= screen.width - 1 && window.innerHeight >= screen.height - 1;
			if (full || (resized && t - resized >= settleMs) || t - started >= maxMs || !page.isFullscreen()) {
				window.removeEventListener('resize', onResize);
				resolve({ full, ms: Math.round(t - started) });
				return;
			}

			requestAnimationFrame(check);
		};

		window.addEventListener('resize', onResize);
		check();
	});
}

// Shows the start screen and resolves once the player chose how to play: { fullscreen: true } when the
// page went fullscreen. A refused fullscreen request starts the game in the window. state (for tests)
// gets webgl2, downloaded (null until known) and choice.
export function showSplash(root, { build, at, storePromise, contentManifest, state = {} }) {
	const actions = root.querySelector('#splash-actions');
	const download = root.querySelector('#splash-download');
	state.webgl2 = hasWebGL2();
	state.downloaded = null;
	if (!state.webgl2) {
		actions.hidden = true;
		root.querySelector('#splash-no-webgl').hidden = false;
	}

	if (build.firstVisit) {
		alreadyDownloaded(build, at, storePromise, contentManifest).then(stored => {
			state.downloaded = stored;
			const mb = Math.max(1, Math.round(build.firstVisit.bytes / 1e6));
			download.textContent = stored ? download.dataset.stored : download.dataset.first.replace('{mb}', String(mb));
		});
	}

	return new Promise(resolve => {
		let chosen = false;
		root.querySelector('#splash-play').addEventListener('click', () => {
			if (chosen)
				return;

			chosen = true;
			state.choice = 'window';
			resolve({ fullscreen: false });
		});

		root.querySelector('#splash-play-fullscreen').addEventListener('click', () => {
			if (chosen)
				return;

			chosen = true;
			state.choice = 'fullscreen';
			// Requested inside the click: browsers grant fullscreen only during a user gesture.
			page.enterFullscreen()
				.then(granted => granted ? waitForFullscreenSize() : null)
				.then(wait => {
					state.fullscreenWait = wait;
					resolve({ fullscreen: page.isFullscreen() });
				});
		});
	});
}
