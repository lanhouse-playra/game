// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser storage for OpenRA.Web (an IndexedDB database, "webra" when the page is served at the root of
// its site; main.js chooses the name):
//   content  the game content installed on the first visit (path -> bytes), plus one
//            "#installed/<mod>" record that describes the installation;
//   support  the engine's support directory, except Content/ and Logs/: settings.yaml, the player
//            profile key, replays, downloaded maps, the news cache (path -> { data, mtime }).
// Without IndexedDB (blocked storage, some private modes) everything still works, in memory only.

const DEFAULT_DB_NAME = 'webra';
const DB_VERSION = 1;

const request = req => new Promise((resolve, reject) => {
	req.onsuccess = () => resolve(req.result);
	req.onerror = () => reject(req.error);
});

const done = tx => new Promise((resolve, reject) => {
	tx.oncomplete = () => resolve();
	tx.onerror = () => reject(tx.error);
	tx.onabort = () => reject(tx.error || new Error('IndexedDB transaction aborted'));
});

export class Store {
	constructor(db) {
		this.db = db;
	}

	static async open(name = DEFAULT_DB_NAME) {
		if (!globalThis.indexedDB)
			return null;

		try {
			const req = indexedDB.open(name, DB_VERSION);
			req.onupgradeneeded = () => {
				const db = req.result;
				for (const store of ['content', 'support'])
					if (!db.objectStoreNames.contains(store))
						db.createObjectStore(store);
			};

			return new Store(await request(req));
		} catch (e) {
			console.warn('[webra] IndexedDB is not available, nothing will be saved:', e);
			return null;
		}
	}

	async get(store, key) {
		return request(this.db.transaction(store).objectStore(store).get(key));
	}

	// All [key, value] pairs of a store whose key starts with prefix.
	async entries(store, prefix = '') {
		const s = this.db.transaction(store).objectStore(store);
		const range = prefix ? IDBKeyRange.bound(prefix, prefix + '￿') : undefined;
		const [keys, values] = await Promise.all([request(s.getAllKeys(range)), request(s.getAll(range))]);
		return keys.map((k, i) => [k, values[i]]);
	}

	// Empties one store (Reset saved data).
	async clear(store) {
		const tx = this.db.transaction(store, 'readwrite');
		tx.objectStore(store).clear();
		return done(tx);
	}

	// One transaction: puts [key, value] pairs and deletes keys. clearFirst empties the store.
	async write(store, puts, deletes = [], clearFirst = false) {
		const tx = this.db.transaction(store, 'readwrite');
		const s = tx.objectStore(store);
		if (clearFirst)
			s.clear();
		for (const key of deletes)
			s.delete(key);
		for (const [key, value] of puts)
			s.put(value, key);
		return done(tx);
	}
}

// Deletes every saved support file (settings, profile, replays, maps); the game content stays.
// Works without a Store too (IndexedDB that cannot be opened normally): then the whole database goes.
export async function resetSavedData(name = DEFAULT_DB_NAME) {
	const store = await Store.open(name);
	if (store) {
		await store.clear('support');
		store.db.close();
		return;
	}

	if (globalThis.indexedDB)
		await request(indexedDB.deleteDatabase(name));
}

const isQuotaError = e => !!e && (e.name === 'QuotaExceededError' || /quota/i.test(String(e.message || e)));

// Keeps the support directory of the emscripten filesystem in IndexedDB: restore() before the engine
// boots, then sync() whenever files may have changed (timer, page hidden, page closed). Files are
// compared by modification time, so a sync without changes only walks the directory tree.
//
// Failures never stop the game: a saved file that cannot be restored is skipped (and dropped from
// the store at the next sync); writes go in bounded batches, a batch that fails is retried file by
// file, and a file that still fails is skipped until it changes again. onError(kind, error) tells
// the page, which shows quota errors to the player.
export class SupportSync {
	constructor(FS, store, {
		root = '/support', exclude = ['/support/Content', '/support/Logs'], maxFileBytes = 64 << 20,
		batchBytes = 4 << 20, batchFiles = 64, onError = () => {},
	} = {}) {
		this.FS = FS;
		this.store = store;
		this.root = root;
		this.exclude = exclude;
		this.maxFileBytes = maxFileBytes;
		this.batchBytes = batchBytes;
		this.batchFiles = batchFiles;
		this.onError = onError;
		this.known = new Map();   // path -> mtime saved in the store
		this.failed = new Map();  // path -> mtime whose write failed (retried once it changes)
		this.dropped = new Set(); // stored paths that could not be restored
		this.syncing = null;
		this.stopped = false;
		this.timer = 0;
		this.stats = { restoredFiles: 0, restoredBytes: 0, skipped: [], syncs: 0, written: 0, deleted: 0, failedFiles: 0, lastError: null };
	}

	async restore() {
		if (!this.store)
			return;

		let entries;
		try {
			entries = await this.store.entries('support', this.root + '/');
		} catch (e) {
			// The store cannot even be read: boot with defaults rather than not at all.
			this.stats.lastError = String(e);
			console.warn('[webra] reading the saved support directory failed; starting without it:', e);
			this.onError('restore', e);
			return;
		}

		for (const [path, entry] of entries) {
			if (this.excluded(path))
				continue;

			try {
				if (!entry || !(entry.data instanceof Uint8Array) || typeof entry.mtime !== 'number' || !path.startsWith(this.root + '/') || path.includes('/../'))
					throw new Error('malformed entry');

				this.FS.mkdirTree(path.substring(0, path.lastIndexOf('/')));
				this.FS.writeFile(path, entry.data);
				this.FS.utime(path, entry.mtime, entry.mtime);
				this.known.set(path, entry.mtime);
				this.stats.restoredFiles++;
				this.stats.restoredBytes += entry.data.length;
			} catch (e) {
				// One bad entry (corrupt value, a path whose parent is now a file, ...) must not keep
				// the game from starting.
				this.dropped.add(path);
				// emscripten's FS errors carry a code (ENOTDIR, ...) and no message.
				this.stats.skipped.push(`${path}: ${e && (e.code || e.message) || e}`);
				console.warn(`[webra] skipped the saved file ${path}:`, e);
			}
		}
	}

	excluded(path) {
		return this.exclude.some(e => path === e || path.startsWith(e + '/'));
	}

	scan() {
		const FS = this.FS;
		const found = new Map();
		const walk = dir => {
			let names;
			try {
				names = FS.readdir(dir);
			} catch {
				return;
			}

			for (const name of names) {
				if (name === '.' || name === '..')
					continue;

				const path = dir + '/' + name;
				if (this.excluded(path))
					continue;

				const stat = FS.stat(path);
				if (FS.isDir(stat.mode))
					walk(path);
				else if (FS.isFile(stat.mode) && stat.size <= this.maxFileBytes)
					found.set(path, stat.mtime.getTime());
			}
		};

		walk(this.root);
		return found;
	}

	sync() {
		if (!this.store || this.stopped)
			return Promise.resolve();

		// One sync at a time; a request during a sync runs once more afterwards.
		if (this.syncing) {
			this.again = true;
			return this.syncing;
		}

		this.syncing = this.syncOnce().catch(e => {
			this.stats.lastError = String(e);
			console.warn('[webra] saving the support directory failed:', e);
			this.onError(isQuotaError(e) ? 'quota' : 'write', e);
		}).finally(() => {
			this.syncing = null;
			if (this.again) {
				this.again = false;
				this.sync();
			}
		});

		return this.syncing;
	}

	async syncOnce() {
		const found = this.scan();
		const changed = [];
		for (const [path, mtime] of found)
			if (this.known.get(path) !== mtime && this.failed.get(path) !== mtime)
				changed.push([path, mtime]);

		const deletes = [...this.known.keys(), ...this.dropped].filter(path => !found.has(path));
		this.stats.syncs++;
		if (deletes.length) {
			await this.store.write('support', [], deletes);
			for (const path of deletes) {
				this.known.delete(path);
				this.dropped.delete(path);
			}

			this.stats.deleted += deletes.length;
		}

		// Bounded batches: one huge transaction would fail as a whole at the quota, and keep
		// failing at every sync. Files are read only when their batch is written.
		let firstError = null;
		for (let i = 0; i < changed.length && !this.stopped;) {
			const batch = [];
			let bytes = 0;
			for (; i < changed.length && batch.length < this.batchFiles && (batch.length === 0 || bytes < this.batchBytes); i++) {
				const [path, mtime] = changed[i];
				let data;
				try {
					data = this.FS.readFile(path);
				} catch {
					continue; // removed since the scan
				}

				batch.push([path, { data, mtime }]);
				bytes += data.length;
			}

			const error = await this.writeBatch(batch);
			firstError ||= error;
		}

		if (firstError)
			throw firstError;
	}

	// Writes one batch; on failure, file by file, so one file over the quota does not block the rest.
	async writeBatch(batch) {
		const saved = entries => {
			for (const [path, entry] of entries) {
				this.known.set(path, entry.mtime);
				this.failed.delete(path);
				this.dropped.delete(path);
			}

			this.stats.written += entries.length;
		};

		if (batch.length === 0)
			return null;

		try {
			await this.store.write('support', batch);
			saved(batch);
			return null;
		} catch (e) {
			if (batch.length === 1) {
				this.failed.set(batch[0][0], batch[0][1].mtime);
				this.stats.failedFiles++;
				console.warn(`[webra] could not save ${batch[0][0]}:`, e);
				return e;
			}
		}

		let error = null;
		for (const entry of batch)
			error = (await this.writeBatch([entry])) || error;
		return error;
	}

	// Stops saving (Reset saved data must not be undone by a sync that is still due).
	stop() {
		this.stopped = true;
		clearInterval(this.timer);
	}

	// Saves every few seconds and whenever the page is hidden or closed.
	start(intervalMs = 3000) {
		this.timer = setInterval(() => this.sync(), intervalMs);
		document.addEventListener('visibilitychange', () => {
			if (document.hidden)
				this.sync();
		});
		window.addEventListener('pagehide', () => this.sync());
	}
}
