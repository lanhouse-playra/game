// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser storage for OpenRA.Web (IndexedDB database "webra"):
//   content  the game content installed on the first visit (path -> bytes), plus one
//            "#installed/<mod>" record that describes the installation;
//   support  the engine's support directory, except Content/ and Logs/: settings.yaml, the player
//            profile key, replays, downloaded maps, the news cache (path -> { data, mtime }).
// Without IndexedDB (blocked storage, some private modes) everything still works, in memory only.

const DB_NAME = 'webra';
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

	static async open() {
		if (!globalThis.indexedDB)
			return null;

		try {
			const req = indexedDB.open(DB_NAME, DB_VERSION);
			req.onupgradeneeded = () => {
				const db = req.result;
				for (const name of ['content', 'support'])
					if (!db.objectStoreNames.contains(name))
						db.createObjectStore(name);
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

// Keeps the support directory of the emscripten filesystem in IndexedDB: restore() before the engine
// boots, then sync() whenever files may have changed (timer, page hidden, page closed). Files are
// compared by modification time, so a sync without changes only walks the directory tree.
export class SupportSync {
	constructor(FS, store, { root = '/support', exclude = ['/support/Content', '/support/Logs'], maxFileBytes = 64 << 20 } = {}) {
		this.FS = FS;
		this.store = store;
		this.root = root;
		this.exclude = exclude;
		this.maxFileBytes = maxFileBytes;
		this.known = new Map();
		this.syncing = null;
		this.stats = { restoredFiles: 0, restoredBytes: 0, syncs: 0, written: 0, deleted: 0, lastError: null };
	}

	async restore() {
		if (!this.store)
			return;

		for (const [path, entry] of await this.store.entries('support', this.root + '/')) {
			if (this.excluded(path))
				continue;

			this.FS.mkdirTree(path.substring(0, path.lastIndexOf('/')));
			this.FS.writeFile(path, entry.data);
			this.FS.utime(path, entry.mtime, entry.mtime);
			this.known.set(path, entry.mtime);
			this.stats.restoredFiles++;
			this.stats.restoredBytes += entry.data.length;
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
		if (!this.store)
			return Promise.resolve();

		// One sync at a time; a request during a sync runs once more afterwards.
		if (this.syncing) {
			this.again = true;
			return this.syncing;
		}

		this.syncing = this.syncOnce().catch(e => {
			this.stats.lastError = String(e);
			console.warn('[webra] saving the support directory failed:', e);
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
		const puts = [];
		for (const [path, mtime] of found)
			if (this.known.get(path) !== mtime)
				puts.push([path, { data: this.FS.readFile(path), mtime }]);

		const deletes = [...this.known.keys()].filter(path => !found.has(path));
		this.stats.syncs++;
		if (puts.length === 0 && deletes.length === 0)
			return;

		await this.store.write('support', puts, deletes);
		for (const [path, entry] of puts)
			this.known.set(path, entry.mtime);
		for (const path of deletes)
			this.known.delete(path);

		this.stats.written += puts.length;
		this.stats.deleted += deletes.length;
	}

	// Saves every few seconds and whenever the page is hidden or closed.
	start(intervalMs = 3000) {
		setInterval(() => this.sync(), intervalMs);
		document.addEventListener('visibilitychange', () => {
			if (document.hidden)
				this.sync();
		});
		window.addEventListener('pagehide', () => this.sync());
	}
}
