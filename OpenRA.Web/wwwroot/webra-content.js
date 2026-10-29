// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Installs the mod's game content in the browser, as the desktop content installer's quick install
// does: the packages named in data/content-packages.json (generated from the mod's own
// installer definitions) are downloaded from the official OpenRA mirrors, through our relay because
// the mirrors send no CORS headers, checked against the SHA1 the mod defines, and the listed files
// are extracted. They are kept in IndexedDB, so later visits load them from there.

const sha1Hex = async data => [...new Uint8Array(await crypto.subtle.digest('SHA-1', data))]
	.map(b => b.toString(16).padStart(2, '0')).join('');

// Which packages to download: the quick download if it provides every required test file, otherwise
// each required package's own download.
function plan(manifest) {
	const byId = new Map(manifest.packages.map(p => [p.id, p]));
	const needed = manifest.required.flatMap(r => r.testFiles);
	const quick = byId.get(manifest.quickDownload);
	if (quick) {
		const provides = new Set(quick.extract.map(([target]) => target));
		if (needed.every(f => provides.has(f)))
			return [quick];
	}

	return manifest.required.map(r => {
		const p = byId.get(r.download);
		if (!p)
			throw new Error(`No download for the required content package ${r.id}`);
		return p;
	});
}

// Identifies an installation: a different manifest (other packages or hashes) reinstalls.
const installId = packages => packages.map(p => `${p.id}:${p.sha1}`).join(',');

// A refusal from the relay: {"error": code, "message": text for the player, "retryAfter": seconds}.
// Only a fetch still in progress on the relay ("content_pending") is worth retrying at once; the
// others (download limits, the daily budget, mirrors down) are shown to the player as they are.
class RelayRefusal extends Error {
	constructor(status, body) {
		super(body.message || `${status} ${body.error}`);
		this.code = body.error;
		this.retryAfter = Number(body.retryAfter) || 0;
		this.retry = body.error === 'content_pending' || body.error === 'server_full';
	}
}

const inMinutes = seconds => seconds < 90 ? `${Math.max(1, Math.round(seconds))} s` : `${Math.round(seconds / 60)} min`;

async function download(url, onProgress) {
	let lastError;
	for (let attempt = 1; attempt <= 3; attempt++) {
		try {
			const response = await fetch(url, { cache: 'no-store' });
			if (!response.ok) {
				if ((response.headers.get('content-type') || '').includes('application/json')) {
					const body = await response.json().catch(() => ({}));
					if (body && body.error)
						throw new RelayRefusal(response.status, body);
				}

				const text = (await response.text().catch(() => '')).trim().slice(0, 200);
				throw new Error(`${response.status} ${response.statusText}${text ? ': ' + text : ''}`);
			}

			const total = Number(response.headers.get('content-length')) || 0;
			const mirror = response.headers.get('x-webra-mirror') || '';
			const reader = response.body.getReader();
			const chunks = [];
			let received = 0;
			onProgress(0, total, mirror);
			for (;;) {
				const { done, value } = await reader.read();
				if (done)
					break;
				chunks.push(value);
				received += value.length;
				onProgress(received, total, mirror);
			}

			if (total && received !== total)
				throw new Error(`incomplete download (${received} of ${total} bytes)`);

			const data = new Uint8Array(received);
			let offset = 0;
			for (const c of chunks) {
				data.set(c, offset);
				offset += c.length;
			}

			return { data, mirror };
		} catch (e) {
			lastError = e;
			console.warn(`[webra] content download attempt ${attempt} failed:`, e);
			if (e instanceof RelayRefusal && !e.retry)
				break;
			if (attempt < 3)
				await new Promise(r => setTimeout(r, e instanceof RelayRefusal ? Math.min(30, Math.max(2, e.retryAfter)) * 1000 : 2000 * attempt));
		}
	}

	if (lastError instanceof RelayRefusal) {
		// The budget message names the reset time itself.
		const wait = lastError.retryAfter && !lastError.retry && lastError.code !== 'budget_exhausted';
		const error = new Error(lastError.message + (wait ? ` (Try again in about ${inMinutes(lastError.retryAfter)}.)` : ''));
		error.code = lastError.code;
		throw error;
	}

	throw new Error(`Downloading ${url} failed: ${lastError && lastError.message || lastError}`);
}

// Minimal zip reader: stored and deflated entries, no zip64 (the content packages are small).
async function unzip(data, wanted) {
	const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
	let eocd = -1;
	for (let i = data.length - 22; i >= Math.max(0, data.length - 65557); i--) {
		if (view.getUint32(i, true) === 0x06054b50) {
			eocd = i;
			break;
		}
	}

	if (eocd < 0)
		throw new Error('not a zip file');

	const count = view.getUint16(eocd + 10, true);
	let p = view.getUint32(eocd + 16, true);
	const decoder = new TextDecoder();
	const entries = new Map();
	for (let i = 0; i < count; i++) {
		if (view.getUint32(p, true) !== 0x02014b50)
			throw new Error('bad zip central directory');

		const method = view.getUint16(p + 10, true);
		const compressed = view.getUint32(p + 20, true);
		const size = view.getUint32(p + 24, true);
		const nameLength = view.getUint16(p + 28, true);
		const extraLength = view.getUint16(p + 30, true);
		const commentLength = view.getUint16(p + 32, true);
		const local = view.getUint32(p + 42, true);
		const name = decoder.decode(data.subarray(p + 46, p + 46 + nameLength));
		entries.set(name.toLowerCase(), { name, method, compressed, size, local });
		p += 46 + nameLength + extraLength + commentLength;
	}

	const out = new Map();
	for (const [target, source] of wanted) {
		const e = entries.get(source.toLowerCase());
		if (!e)
			throw new Error(`${source} is missing from the package`);

		if (view.getUint32(e.local, true) !== 0x04034b50)
			throw new Error(`bad zip local header for ${e.name}`);

		const start = e.local + 30 + view.getUint16(e.local + 26, true) + view.getUint16(e.local + 28, true);
		const raw = data.subarray(start, start + e.compressed);
		let bytes;
		if (e.method === 0)
			bytes = raw.slice();
		else if (e.method === 8) {
			const stream = new Blob([raw]).stream().pipeThrough(new DecompressionStream('deflate-raw'));
			bytes = new Uint8Array(await new Response(stream).arrayBuffer());
		} else
			throw new Error(`${e.name}: unsupported zip compression method ${e.method}`);

		if (bytes.length !== e.size)
			throw new Error(`${e.name}: extracted ${bytes.length} bytes, expected ${e.size}`);

		out.set(target, bytes);
	}

	return out;
}

// Returns { files: Map(path -> bytes), source: 'cache' | 'download', ... }.
// relayBase: URL of the relay's content route (e.g. https://host/content).
export async function ensureContent({ manifest, store, relayBase, onProgress = () => {}, onStatus = () => {} }) {
	const packages = plan(manifest);
	const id = installId(packages);
	const required = manifest.required.flatMap(r => r.testFiles);
	const metaKey = `#installed/${manifest.mod}`;

	if (store) {
		try {
			const meta = await store.get('content', metaKey);
			if (meta && meta.id === id) {
				const files = new Map(await store.entries('content', '/'));
				if (required.every(f => files.has(f)))
					return { files, source: 'cache', bytes: meta.bytes, installedAt: meta.at };
				console.warn('[webra] the stored content is incomplete, downloading it again');
			}
		} catch (e) {
			console.warn('[webra] reading the stored content failed, downloading it again:', e);
		}
	}

	if (typeof DecompressionStream === 'undefined' || !crypto.subtle)
		throw new Error('This browser is too old to install the game content (needs DecompressionStream and Web Crypto).');

	const files = new Map();
	let downloaded = 0;
	const mirrors = [];
	for (const [i, pkg] of packages.entries()) {
		const url = `${relayBase.replace(/\/$/, '')}/${encodeURIComponent(manifest.mod)}/${encodeURIComponent(pkg.id)}`;
		onStatus(`Downloading ${pkg.title}${packages.length > 1 ? ` (${i + 1}/${packages.length})` : ''}`);
		const { data, mirror } = await download(url, (received, total, mirror) => onProgress({ package: pkg.id, received, total, mirror }));
		const hash = await sha1Hex(data);
		if (hash !== pkg.sha1)
			throw new Error(`${pkg.title}: SHA1 ${hash} does not match ${pkg.sha1}`);

		onStatus(`Installing ${pkg.title}`);
		for (const [path, bytes] of await unzip(data, pkg.extract))
			files.set(path, bytes);

		downloaded += data.length;
		mirrors.push(mirror);
	}

	const missing = required.filter(f => !files.has(f));
	if (missing.length)
		throw new Error(`The downloaded content lacks ${missing.join(', ')}`);

	const bytes = [...files.values()].reduce((n, b) => n + b.length, 0);
	if (store) {
		try {
			await store.write('content', [...files, [metaKey, { id, bytes, downloaded, mirrors, at: Date.now() }]], [], true);
		} catch (e) {
			// Quota or private mode: the game still runs, the next visit downloads again.
			console.warn('[webra] could not store the game content:', e);
		}
	}

	return { files, source: 'download', bytes, downloaded, mirrors };
}
