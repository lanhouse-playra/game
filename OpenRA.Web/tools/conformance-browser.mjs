// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// Browser side of the numeric conformance corpus: loads a served dist/ in headless Chromium, waits
// for the boot to finish and writes the output of ConformanceRunner.Run() to <out>.
//
// usage: node conformance-browser.mjs <url> <out>
// env:   PLAYWRIGHT_CORE  path of a playwright-core package (default: resolve 'playwright-core')
//        CHROME           Chromium executable; GPU=1 adds the flags for a real GPU (ANGLE/GL)
//        BOOT_TIMEOUT     seconds to wait for the boot (default 600)

import { createRequire } from 'node:module';
import fs from 'node:fs';

const [url, out] = process.argv.slice(2);
if (!url || !out) {
	console.error('usage: node conformance-browser.mjs <url> <out>');
	process.exit(2);
}

const { chromium } = createRequire(import.meta.url)(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const args = process.env.GPU === '1' ? ['--use-angle=gl', '--enable-gpu', '--ignore-gpu-blocklist'] : [];
const browser = await chromium.launch({ headless: true, args, ...(process.env.CHROME ? { executablePath: process.env.CHROME } : {}) });
let status = 0;
try {
	const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
	const log = [];
	page.on('console', m => log.push(`[${m.type()}] ${m.text()}`));
	page.on('pageerror', e => log.push(`[pageerror] ${e.message}`));
	await page.goto(url);
	await page.waitForFunction(() => window.WebRA && ['running', 'error', 'exited'].includes(window.WebRA.phase),
		null, { timeout: (+process.env.BOOT_TIMEOUT || 600) * 1000, polling: 500 });

	const phase = await page.evaluate(() => window.WebRA.phase);
	if (phase !== 'running') {
		console.error(`boot ended in phase '${phase}':\n${log.slice(-40).join('\n')}`);
		status = 1;
	} else {
		const r = await page.evaluate(() => {
			const runner = window.WebRA.allExports.OpenRA.Web.ConformanceRunner;
			const t0 = performance.now();
			const corpus = runner.Run();
			return { corpus, mismatches: runner.RawCastMismatches(), ms: Math.round(performance.now() - t0), build: window.WebRA.build };
		});
		fs.writeFileSync(out, r.corpus);
		console.log(`build ${JSON.stringify(r.build)}`);
		console.log(`corpus: ${r.corpus.split('\n').length - 1} lines in ${r.ms} ms`);
		console.log(`raw casts that differ from CoreCLR in this runtime: ${r.mismatches ? r.mismatches.split('\n').length : 0}`);
		const checks = log.find(l => l.includes('boot checks'));
		if (checks)
			console.log(checks);
	}
} finally {
	await browser.close();
}
process.exit(status);
