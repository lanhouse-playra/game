// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Installs the mod's game content in the browser, as the desktop content installer's quick install
// does: the packages named in data/content-packages.json (generated from the mod's own
// installer definitions) are downloaded, checked against the SHA1 the mod defines, and the listed files
// are extracted. They are kept in IndexedDB, so later visits load them from there.
//
// Where a package comes from is the caller's choice (packageUrl): a copy published on the page's own
// site under the package's "file" name (tools/fetch-content.py writes it, verified, from the official
// mirrors), or our relay's content route, which fetches it from the official OpenRA mirrors (they
// send no CORS headers, so a page cannot read them directly).

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

async function download(url, onProgress, cache) {
	let lastError;
	for (let attempt = 1; attempt <= 3; attempt++) {
		try {
			const response = await fetch(url, { cache });
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
			const mirror = response.headers.get('x-playra-mirror') || '';
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
			console.warn(`[playra] content download attempt ${attempt} failed:`, e);
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

// The relay's content route for a package: <relayBase>/<mod>/<package id>, e.g. https://host/content/ra/quickinstall.
export const relayPackageUrl = relayBase => (manifest, pkg) =>
	`${relayBase.replace(/\/$/, '')}/${encodeURIComponent(manifest.mod)}/${encodeURIComponent(pkg.id)}`;

// A copy on our own site: <base>/<file>, e.g. https://host/playra/content/ra-quickinstall.<sha1>.zip. The
// manifest names the file (content-manifest.py), so the page and tools/fetch-content.py agree on it.
export const sitePackageUrl = base => (manifest, pkg) => {
	if (!pkg.file)
		throw new Error(`The content manifest names no file for ${pkg.id}`);
	return `${base.replace(/\/$/, '')}/${encodeURIComponent(pkg.file)}`;
};

// True when the store holds an installation of this manifest's packages (the record ensureContent
// writes). Reads only that record, not the files: the start screen asks before the game starts.
export async function contentStored(manifest, store) {
	if (!store)
		return false;

	try {
		const meta = await store.get('content', `#installed/${manifest.mod}`);
		return !!meta && meta.id === installId(plan(manifest));
	} catch {
		return false;
	}
}

// Returns { files: Map(path -> bytes), source: 'cache' | 'download', ... }.
// packageUrl(manifest, package): where to download a package from (relayPackageUrl, sitePackageUrl).
// cache: the fetch cache mode. The relay's answers are not cacheable ('no-store'); a copy on our site has
// a content-addressed name and is cached as immutable ('default'), which spares a second download when
// IndexedDB is not available. A cached copy that fails the SHA1 check is downloaded once more past the
// HTTP cache.
export async function ensureContent({ manifest, store, packageUrl, cache = 'no-store', onProgress = () => {}, onStatus = () => {} }) {
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
				console.warn('[playra] the stored content is incomplete, downloading it again');
			}
		} catch (e) {
			console.warn('[playra] reading the stored content failed, downloading it again:', e);
		}
	}

	if (typeof DecompressionStream === 'undefined' || !crypto.subtle)
		throw new Error('This browser is too old to install the game content (needs DecompressionStream and Web Crypto).');

	const files = new Map();
	let downloaded = 0;
	const mirrors = [];
	for (const [i, pkg] of packages.entries()) {
		const url = packageUrl(manifest, pkg);
		onStatus(`Downloading ${pkg.title}${packages.length > 1 ? ` (${i + 1}/${packages.length})` : ''}`);
		const progress = (received, total, mirror) => onProgress({ package: pkg.id, received, total, mirror });
		let { data, mirror } = await download(url, progress, cache);
		let hash = await sha1Hex(data);
		if (hash !== pkg.sha1 && cache !== 'no-store' && cache !== 'reload') {
			console.warn(`[playra] ${pkg.title}: SHA1 ${hash} does not match ${pkg.sha1}, downloading it again past the HTTP cache`);
			({ data, mirror } = await download(url, progress, 'reload'));
			hash = await sha1Hex(data);
		}

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
			console.warn('[playra] could not store the game content:', e);
		}
	}

	return { files, source: 'download', bytes, downloaded, mirrors };
}
