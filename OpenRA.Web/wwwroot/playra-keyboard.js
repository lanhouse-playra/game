// Copyright (c) The OpenRA Developers and Contributors
// This file is part of OpenRA, which is free software. It is made
// available to you under the terms of the GNU General Public License
// as published by the Free Software Foundation, either version 3 of
// the License, or (at your option) any later version. For more
// information, see COPYING.

// The on-screen keyboard of phones and tablets for the game's text fields (player name, chat, filters, passwords,
// Direct IP). The game draws its fields into the canvas, and a mobile browser shows its keyboard only for a text
// input of the page, and only when the page focuses it while handling the player's tap (iOS insists that the focus
// happens synchronously inside the touchend or click handler; the game's own frame comes too late).
//
// So, inside its touchend handler, the page asks the engine whether the tap put a text field in keyboard focus
// (TextInput.Tap delivers the input SDL has queued, the tap itself, before answering), and if it did, focuses a
// transparent input of its own placed over the field. What the player types there is mirrored into the field:
// every change of the input's value (typing, autocorrect, accents and other IME compositions, Backspace and Delete,
// paste) is replayed into the engine as text and Backspace keys, and the keys that do not edit an input (Enter,
// Escape, Tab, the arrows, Home, End, Ctrl/Cmd shortcuts) are passed to the engine as keys. SDL does not see the
// keys typed into the input, so nothing arrives twice. The input closes (the keyboard hides) when the engine's
// field loses focus (Enter in the match chat, the panel closing) or when the player taps outside the field.
//
// Only touch starts any of this: with a mouse and a physical keyboard the input is never focused and the game gets
// its keys from SDL as before.
//
// With `debug` (the page's ?debug=keyboard, see playra-keyboard-debug.js) the keyboard also keeps a short trace of what
// reached the input (keys, beforeinput, input, compositions, focus) and of what the engine did with it, for a phone
// that has no developer tools to look at. Without it no trace is kept and no extra listener is added.

// Keys passed to the engine instead of being left to the input. Backspace and Delete edit the input and reach the
// engine through the value change, unless the page stopped their default action (Ctrl+Backspace, see captureKeys).
const ENGINE_KEYS = new Set(['Enter', 'Escape', 'Tab', 'ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End']);
const EDIT_KEYS = new Set(['Backspace', 'Delete']);

// Left alone, so that SDL keeps an accurate modifier state.
const MODIFIER_KEYS = new Set(['Shift', 'Control', 'Alt', 'AltGraph', 'Meta', 'CapsLock', 'OS']);

// Entries kept in the debug trace.
const TRACE_LENGTH = 15;

// Fields (by engine widget id) whose Enter key does something other than "done".
const ENTER_HINTS = { CHAT_TEXTFIELD: 'send', SEARCH_TEXTFIELD: 'search', FILTER_INPUT: 'search', MAPFILTER_INPUT: 'search', FLT_NAME_INPUT: 'search' };

function parseField(state) {
	if (!state || state.length < 9)
		return null;

	const [kind, id, maxLength, cursor, x, y, width, height, text] = state;
	return { kind, id, maxLength: Number(maxLength), cursor: Number(cursor), x: Number(x), y: Number(y), width: Number(width), height: Number(height), text };
}

function engineModifiers(e) {
	return (e.shiftKey ? 1 : 0) | (e.altKey ? 2 : 0) | (e.ctrlKey ? 4 : 0) | (e.metaKey ? 8 : 0);
}

// The edit that turns oldValue into newValue, as the engine's text field can perform it from its cursor (at
// `cursor`, where the input's caret was): move right to the end of the changed part, delete back to its start,
// type its new text. Bounding the common prefix by the cursor and the common suffix by the text after it puts the
// change at the caret when the input is ambiguous ("aa" to "aaa").
export function editFor(oldValue, newValue, cursor) {
	cursor = Math.max(0, Math.min(cursor, oldValue.length));
	let prefix = 0;
	const maxPrefix = Math.min(oldValue.length, newValue.length, cursor);
	while (prefix < maxPrefix && oldValue[prefix] === newValue[prefix])
		prefix++;

	let suffix = 0;
	const maxSuffix = Math.min(newValue.length - prefix, oldValue.length - cursor);
	while (suffix < maxSuffix && oldValue[oldValue.length - 1 - suffix] === newValue[newValue.length - 1 - suffix])
		suffix++;

	const end = oldValue.length - suffix;
	return { right: end - cursor, backspaces: end - prefix, text: newValue.substring(prefix, newValue.length - suffix), cursorAfter: newValue.length - suffix };
}

// A value as the debug trace shows it: quoted, cut short, and never the text of a password field.
function shown(value, secret) {
	if (value === null || value === undefined)
		return String(value);

	value = String(value);
	if (secret)
		value = '*'.repeat(value.length);
	return JSON.stringify(value.length > 24 ? value.substring(0, 24) + '...' : value);
}

// canvas: the game's canvas; text: the TextInput exports (OpenRA.Web/TextInput.cs); size: the engine's render size;
// refit: fits the canvas to the window again; isActive: whether the game is running; debug: keep the trace.
export function installKeyboard({ canvas, text, size, refit, isActive, debug = false }) {
	const input = document.createElement('input');
	input.id = 'keyboard-input';
	input.type = 'text';
	input.autocomplete = 'off';
	input.setAttribute('autocapitalize', 'off');
	input.setAttribute('autocorrect', 'off');
	input.spellcheck = false;
	input.setAttribute('aria-label', 'Campo de texto do jogo');
	input.tabIndex = -1;
	// Transparent and out of the way of taps (they belong to the canvas, also over the field). 16 px text: iOS zooms
	// the page into any focused input with smaller text.
	input.style.cssText = 'position: absolute; left: 0; top: 0; width: 1px; height: 1px; margin: 0; padding: 0; border: 0; ' +
		'box-sizing: border-box; font-size: 16px; line-height: 1; opacity: 0; color: transparent; background: transparent; ' +
		'caret-color: transparent; outline: none; pointer-events: none; z-index: 0;';
	document.body.appendChild(input);

	const appleMobile = /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
	const stats = { taps: 0, opened: 0, closed: 0, typed: 0, keys: 0, backspaces: 0, resyncs: 0, errors: 0, lastError: '' };
	const trace = debug ? [] : null;
	const traceListeners = new Set();
	const secret = () => input.type === 'password';
	const note = line => {
		if (!trace)
			return;

		trace.push(`${(performance.now() / 1000).toFixed(2)} ${line}`);
		if (trace.length > TRACE_LENGTH)
			trace.shift();
		for (const listener of traceListeners)
			listener();
	};
	let field = null;
	let value = '';
	let caret = 0;
	let composing = false;
	let refocusing = false;
	let touchStart = null;
	let rafPending = false;
	let placed = '';

	const call = (what, f) => {
		try {
			return f();
		} catch (e) {
			stats.errors++;
			stats.lastError = `${what}: ${e && e.message || e}`.substring(0, 200);
			console.warn(`[playra] keyboard: ${what} failed:`, e);
			note(`! ${stats.lastError}`);
			return undefined;
		}
	};

	const focused = () => document.activeElement === input;
	const engineField = () => parseField(call('Focused', () => text.Focused()));

	// Engine window coordinates of a point on the page (SDL scales from the canvas's CSS size the same way).
	const toEngine = (clientX, clientY) => {
		const r = canvas.getBoundingClientRect();
		return {
			x: Math.floor((clientX - r.left) * size.width / Math.max(1, r.width)),
			y: Math.floor((clientY - r.top) * size.height / Math.max(1, r.height)),
		};
	};

	// Over the engine's field, so that the browser scrolls the field into view above the keyboard.
	const place = f => {
		const r = canvas.getBoundingClientRect();
		const sx = r.width / size.width, sy = r.height / size.height;
		const left = Math.round(r.left + window.scrollX + f.x * sx), top = Math.round(r.top + window.scrollY + f.y * sy);
		const width = Math.max(1, Math.round(f.width * sx)), height = Math.max(1, Math.round(f.height * sy));
		const key = `${left},${top},${width},${height}`;
		if (key === placed)
			return;

		placed = key;
		Object.assign(input.style, { left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px` });
	};

	const setValue = (newValue, cursor) => {
		if (input.value !== newValue)
			input.value = newValue;

		cursor = Math.max(0, Math.min(cursor, newValue.length));
		if (input.selectionStart !== cursor || input.selectionEnd !== cursor)
			call('setSelectionRange', () => input.setSelectionRange(cursor, cursor));

		value = newValue;
		caret = cursor;
	};

	// The engine may refuse part of an edit (a character a filename cannot have, a field that is full): its text wins.
	const resync = () => {
		if (composing)
			return;

		const f = engineField();
		if (!f)
			return;

		if (f.text !== input.value || f.cursor !== input.selectionStart || f.cursor !== input.selectionEnd) {
			stats.resyncs++;
			note(`resync: input ${shown(input.value, secret())}@${input.selectionStart} -> engine ${shown(f.text, secret())}@${f.cursor}`);
		}

		setValue(f.text, f.cursor);
	};

	const key = (name, modifiers = 0) => {
		stats.keys++;
		return call('Key', () => text.Key(name, modifiers));
	};

	const configure = f => {
		const type = f.kind === 'password' ? 'password' : 'text';
		const changed = input.type !== type;
		input.type = type;
		input.inputMode = f.kind === 'number' ? 'numeric' : f.id === 'IP' ? 'url' : 'text';
		input.enterKeyHint = ENTER_HINTS[f.id] || 'done';
		if (f.maxLength > 0)
			input.maxLength = f.maxLength;
		else
			input.removeAttribute('maxlength');
		return changed;
	};

	// Inside the tap's touchend handler: the focus must happen before the handler returns.
	const open = f => {
		const typeChanged = configure(f);
		field = f;
		place(f);
		setValue(f.text, f.cursor);

		// A keyboard the player hid (Android's back button) leaves the input focused, and focusing it again does not
		// bring the keyboard back; neither does a new input type on iOS. Leaving the input first does.
		if (focused() && (typeChanged || !appleMobile)) {
			refocusing = true;
			input.blur();
			refocusing = false;
		}

		if (!focused()) {
			input.focus();
			stats.opened++;
			note(`open ${f.id || f.kind}: focus ${focused() ? 'ok' : 'FAILED'}, active ${document.activeElement && (document.activeElement.id || document.activeElement.nodeName)}`);
		}

		watch();
	};

	const close = reason => {
		note(`close: ${reason}`);
		field = null;
		if (focused())
			input.blur();
	};

	const onBlur = () => {
		if (refocusing)
			return;

		field = null;
		composing = false;
		stats.closed++;
		// Keys go to the game again; the page may have been scrolled to show the field above the keyboard.
		canvas.focus({ preventScroll: true });
		if (window.scrollX || window.scrollY)
			window.scrollTo(0, 0);

		// The window's resize when the keyboard has gone fits the canvas again; this covers a browser that changed
		// the window for the keyboard and sends no resize when it hides (not done at once: the keyboard is still up).
		setTimeout(() => {
			if (!focused())
				refit();
		}, 1000);
	};

	// While the input is focused: follow the engine's field (moved, edited by the engine, or no longer focused).
	const watch = () => {
		if (rafPending)
			return;

		rafPending = true;
		requestAnimationFrame(() => {
			rafPending = false;
			if (!focused())
				return;

			const f = engineField();
			if (!f) {
				close('engine field lost focus');
				return;
			}

			field = f;
			place(f);
			if (!composing && (f.text !== value || f.cursor !== caret))
				resync();
			watch();
		});
	};

	const onTouchStart = e => {
		touchStart = e.touches.length === 1 && e.target === canvas ? { x: e.touches[0].clientX, y: e.touches[0].clientY } : null;
	};

	// After SDL's own touchend handler on the canvas, which has queued the tap as mouse input.
	const onTouchEnd = e => {
		if (e.touches.length > 0 || !touchStart || e.target !== canvas)
			return;

		const start = touchStart;
		touchStart = null;
		if (!isActive())
			return;

		stats.taps++;
		const p = toEngine(start.x, start.y);
		const f = parseField(call('Tap', () => text.Tap(p.x, p.y)));
		if (trace && (f || focused()))
			note(`tap ${p.x},${p.y}: ${f ? `field ${f.id || f.kind} ${shown(f.text, f.kind === 'password')}@${f.cursor}` : 'no field'}`);
		if (f)
			open(f);
		else if (focused())
			close('tap outside the field');
	};

	const onKeyDown = e => {
		if (trace)
			note(`keydown key=${shown(e.key, secret() && e.key.length === 1)} code=${shown(e.code, secret())} keyCode=${secret() && e.key.length === 1 ? '*' : e.keyCode}${e.isComposing ? ' composing' : ''}${e.defaultPrevented ? ' PREVENTED' : ''}`);
		if (MODIFIER_KEYS.has(e.key))
			return;

		// SDL listens on the window: it must not see keys typed here, they reach the engine below or as value changes.
		e.stopPropagation();
		if (e.isComposing || e.keyCode === 229)
			return;

		const shortcut = (e.ctrlKey || e.metaKey) && /^[a-z]$/i.test(e.key) && !/^v$/i.test(e.key);
		if (ENGINE_KEYS.has(e.key) || shortcut || (EDIT_KEYS.has(e.key) && e.defaultPrevented)) {
			e.preventDefault();
			key(e.key, engineModifiers(e));
			resync();
		}
	};

	const onInput = () => {
		const oldValue = value, newValue = input.value;
		const caretAfter = input.selectionStart ?? newValue.length;
		if (oldValue === newValue) {
			caret = caretAfter;
			return;
		}

		const edit = editFor(oldValue, newValue, caret);
		for (let i = 0; i < edit.right; i++)
			key('ArrowRight');
		for (let i = 0; i < edit.backspaces; i++) {
			stats.backspaces++;
			key('Backspace');
		}
		if (edit.text) {
			stats.typed += edit.text.length;
			call('Type', () => text.Type(edit.text));
		}
		if (trace) {
			const f = engineField();
			note(`edit: ${edit.right ? `right ${edit.right}, ` : ''}${edit.backspaces ? `backspace ${edit.backspaces}, ` : ''}type ${shown(edit.text, secret())} -> engine ${f ? `${shown(f.text, secret())}@${f.cursor}` : 'no field'}`);
		}
		for (let i = edit.cursorAfter; i > caretAfter; i--)
			key('ArrowLeft');
		for (let i = edit.cursorAfter; i < caretAfter; i++)
			key('ArrowRight');

		value = newValue;
		caret = caretAfter;
		resync();
	};

	canvas.addEventListener('touchstart', onTouchStart, { passive: true });
	window.addEventListener('touchend', onTouchEnd, { passive: true });
	input.addEventListener('keydown', onKeyDown);
	for (const type of ['keyup', 'keypress'])
		input.addEventListener(type, e => {
			if (!MODIFIER_KEYS.has(e.key))
				e.stopPropagation();
		});
	input.addEventListener('input', onInput);
	input.addEventListener('compositionstart', () => {
		composing = true;
	});
	input.addEventListener('compositionend', () => {
		composing = false;
		resync();
	});
	input.addEventListener('blur', onBlur);
	if (trace) {
		input.addEventListener('keypress', e => note(`keypress key=${shown(e.key, secret() && e.key.length === 1)} charCode=${secret() ? '*' : e.charCode}${e.defaultPrevented ? ' PREVENTED' : ''}`), true);
		input.addEventListener('beforeinput', e => note(`beforeinput ${e.inputType} data=${shown(e.data, secret())}${e.isComposing ? ' composing' : ''}`));
		input.addEventListener('input', e => note(`input ${e.inputType} data=${shown(e.data, secret())} value=${shown(input.value, secret())}@${input.selectionStart}`), true);
		for (const type of ['compositionstart', 'compositionupdate', 'compositionend'])
			input.addEventListener(type, e => note(`${type} data=${shown(e.data, secret())}`));
		input.addEventListener('focus', () => note('input focus'));
		input.addEventListener('blur', e => note(`input blur${e.relatedTarget ? ` to ${e.relatedTarget.id || e.relatedTarget.nodeName}` : ''}`), true);
		window.addEventListener('blur', () => note('window blur'));
		window.addEventListener('focus', () => note('window focus'));
		document.addEventListener('visibilitychange', () => note(`page ${document.hidden ? 'hidden' : 'visible'}`));
	}

	// The caret moved without an edit (the iOS keyboard's trackpad, a physical keyboard's selection keys left to the
	// input): the engine's cursor follows.
	document.addEventListener('selectionchange', () => {
		if (!focused() || composing || input.value !== value || input.selectionStart !== input.selectionEnd || input.selectionStart === caret)
			return;

		const to = input.selectionStart;
		note(`caret ${caret} -> ${to}`);
		for (; caret < to; caret++)
			key('ArrowRight');
		for (; caret > to; caret--)
			key('ArrowLeft');
		resync();
	});

	return {
		input,
		stats,
		// While the keyboard is up, the window's height changes only because of it: the canvas keeps its size.
		holdsViewport: () => focused(),
		state: () => ({ open: focused(), composing, field: field && { kind: field.kind, id: field.id }, type: input.type, inputMode: input.inputMode, enterKeyHint: input.enterKeyHint, maxLength: input.maxLength }),
		// The debug trace (null without debug), and a way to hear of new entries.
		trace,
		onTrace: listener => {
			traceListeners.add(listener);
			return () => traceListeners.delete(listener);
		},
		// What the debug overlay shows besides the trace: the page's input and the engine's field as they are now.
		diagnostics: () => {
			const f = engineField();
			const active = document.activeElement;
			return {
				active: active ? active.id || active.nodeName : null,
				open: focused(),
				composing,
				input: { type: input.type, value: shown(input.value, secret()), selection: [input.selectionStart, input.selectionEnd], rect: input.getBoundingClientRect() },
				engine: f && { kind: f.kind, id: f.id, text: shown(f.text, f.kind === 'password'), cursor: f.cursor, maxLength: f.maxLength },
				stats,
			};
		},
	};
}
