/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

/*
 * Compile-time check (never linked): OpenRA.Platforms.Default/FreeTypeFont.cs reads FreeType
 * structs through hardcoded offsets for IntPtr.Size == 4. wasm32 is ILP32, so those offsets
 * must match the FreeType that emscripten links. build-web.sh compiles this with -fsyntax-only.
 */

#include <stddef.h>
#include <ft2build.h>
#include FT_FREETYPE_H

_Static_assert(offsetof(FT_FaceRec, glyph) == 84, "FaceRecGlyphOffset");
_Static_assert(offsetof(FT_GlyphSlotRec, metrics) == 24, "GlyphSlotMetricsOffset");
_Static_assert(offsetof(FT_GlyphSlotRec, bitmap) == 76, "GlyphSlotBitmapOffset");
_Static_assert(offsetof(FT_GlyphSlotRec, bitmap_left) == 100, "GlyphSlotBitmapLeftOffset");
_Static_assert(offsetof(FT_GlyphSlotRec, bitmap_top) == 104, "GlyphSlotBitmapTopOffset");
_Static_assert(offsetof(FT_Glyph_Metrics, width) == 0, "MetricsWidthOffset");
_Static_assert(offsetof(FT_Glyph_Metrics, height) == 4, "MetricsHeightOffset");
_Static_assert(offsetof(FT_Glyph_Metrics, horiAdvance) == 16, "MetricsAdvanceOffset");
_Static_assert(offsetof(FT_Bitmap, pitch) == 8, "BitmapPitchOffset");
_Static_assert(offsetof(FT_Bitmap, buffer) == 12, "BitmapBufferOffset");
