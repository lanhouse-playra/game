/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

/*
 * The .NET WebAssembly build only resolves a DllImport("<name>") statically when a native
 * file called <name>.a is linked. SDL2, FreeType and OpenAL come from emscripten itself
 * (-sUSE_SDL=2, -sUSE_FREETYPE=1, -lopenal), so build-web.sh wraps this stub into SDL2.a,
 * freetype6.a and soft_oal.a purely to register those module names.
 */

int WEBRA_STUB_SYMBOL(void) { return 0; }
