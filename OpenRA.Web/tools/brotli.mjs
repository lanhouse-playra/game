// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Makes sure each given file has a correct brotli copy next to it (<file>.br).
//
// usage: node brotli.mjs <file>...
//
// An existing .br (the .NET SDK writes them for _framework and the page's own files) is decompressed
// and compared with the file; one that is missing or does not match is written again at quality 11.
// A publish without content negotiation (one object, one encoding: R2) uploads these copies with
// Content-Encoding: br, so a stale one would serve another file's bytes. Prints one JSON line per file.
// Node's zlib only: no package to install.

import fs from 'node:fs';
import zlib from 'node:zlib';

const { BROTLI_PARAM_QUALITY, BROTLI_PARAM_LGWIN, BROTLI_PARAM_SIZE_HINT, BROTLI_MAX_QUALITY } = zlib.constants;

// The .NET SDK compresses at quality 11 with a 4 MiB window (22). The same here, so every copy is
// made alike and any brotli decoder reads it.
const LGWIN = 22;

const files = process.argv.slice(2);
if (files.length === 0) {
	console.error('usage: node brotli.mjs <file>...');
	process.exit(2);
}

for (const file of files) {
	const data = fs.readFileSync(file);
	const brFile = `${file}.br`;
	let status = 'written';
	if (fs.existsSync(brFile)) {
		try {
			status = zlib.brotliDecompressSync(fs.readFileSync(brFile)).equals(data) ? 'kept' : 'rewritten (stale)';
		} catch {
			status = 'rewritten (unreadable)';
		}
	}

	if (status !== 'kept') {
		const br = zlib.brotliCompressSync(data, {
			params: { [BROTLI_PARAM_QUALITY]: BROTLI_MAX_QUALITY, [BROTLI_PARAM_LGWIN]: LGWIN, [BROTLI_PARAM_SIZE_HINT]: data.length },
		});
		if (!zlib.brotliDecompressSync(br).equals(data))
			throw new Error(`${file}: brotli round trip failed`);
		fs.writeFileSync(brFile, br);
	}

	console.log(JSON.stringify({ file, size: data.length, br: fs.statSync(brFile).size, status }));
}
