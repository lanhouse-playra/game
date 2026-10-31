/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

/*
 * setjmp/longjmp runtime support for the .NET wasm-tools toolchain.
 *
 * That toolchain pairs emscripten 3.1.56's system libraries with a newer clang (19). With wasm
 * exception handling, clang 19 lowers setjmp() to calls to __wasm_setjmp() and
 * __wasm_setjmp_test(), but emscripten 3.1.56's compiler-rt only provides the older
 * saveSetjmp()/testSetjmp() pair (plus __wasm_longjmp(), which is unchanged). Lua (which reports
 * every error through longjmp) and emscripten's FreeType port are compiled by clang 19, so they
 * need the newer pair. Without it the linker turns them into stubs that abort when called.
 *
 * Protocol (LLVM WebAssemblyLowerEmscriptenEHSjLj): setjmp stores a per-call-frame invocation id
 * and a label in the jmp_buf; longjmp throws the C_LONGJMP tag with {env, val}; each frame that
 * called setjmp catches it and asks __wasm_setjmp_test() whether the jmp_buf belongs to it, and
 * which setjmp label to resume at (0: not ours, rethrow).
 */

#include <stdint.h>
#include <stdlib.h>

struct playra_jmp_buf
{
	void *func_invocation_id;
	uint32_t label;
};

void __wasm_setjmp(void *env, uint32_t label, void *func_invocation_id)
{
	struct playra_jmp_buf *jb = (struct playra_jmp_buf *)env;
	if (label == 0 || func_invocation_id == NULL)
		abort();

	jb->func_invocation_id = func_invocation_id;
	jb->label = label;
}

uint32_t __wasm_setjmp_test(void *env, void *func_invocation_id)
{
	struct playra_jmp_buf *jb = (struct playra_jmp_buf *)env;
	if (jb->func_invocation_id == func_invocation_id)
		return jb->label;

	return 0;
}
