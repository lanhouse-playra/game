// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// The page's code is a module, main.js, and the modules it imports. When one of them cannot be downloaded (the
// connection dropped while the page loaded), none of it runs: the start screen stayed up with buttons that did
// nothing, or the loading screen without a word. This classic script runs before the module is even requested and
// shows the error screen in that case. A failed download of any module of the page fires "error" at its script
// element, which does not bubble: it is caught on the way down.
window.addEventListener('error', e => {
	const script = e.target;
	if (!(script instanceof HTMLScriptElement) || script.type !== 'module' || window.PlayRA)
		return;

	document.getElementById('error').textContent = 'The game\'s page could not be downloaded completely. Check the connection and try again.';
	document.getElementById('retry').addEventListener('click', () => location.reload());
	document.body.dataset.phase = 'error';
}, true);
