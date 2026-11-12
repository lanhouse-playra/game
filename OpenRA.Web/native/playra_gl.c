/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

/*
 * GL lookup shim for the browser (WebAssembly) build.
 *
 * OpenRA.Platforms.Default binds every GL entry point through a function pointer lookup.
 * In the browser that lookup goes through playra_gl_get_proc_address(), which returns
 * emscripten's WebGL 2 implementation for everything except the calls below:
 *
 * The engine's GLES profile uploads textures as GL_BGRA (GL_EXT_texture_format_BGRA8888),
 * which WebGL 2 does not support. These wrappers convert BGRA to RGBA on upload so that
 * the engine and its shaders stay unchanged. Pixel data is only touched on texture uploads:
 * when sheets are created or updated, and for the textures the engine uploads whole every
 * frame, only the rows that changed (see "Repeated whole-texture uploads" below).
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <GLES3/gl3.h>
#include <emscripten/em_js.h>
#include "playra_gl.h"
#ifdef __wasm_simd128__
#include <wasm_simd128.h>
#endif

#define PLAYRA_GL_BGRA 0x80E1

extern void *SDL_GL_GetProcAddress(const char *proc);

/*
 * Flat varyings take their value from the provoking vertex of each triangle, which in GL (and so in WebGL)
 * is the last one. Direct3D uses the first, so ANGLE's D3D11 backend (Chrome and Edge on Windows) emulates
 * the GL convention with a geometry shader in every draw of a program with flat varyings: OpenRA's sprite
 * shader (glsl/combined.vert) has six. WEBGL_provoking_vertex, where the browser has it, switches the context
 * to the first-vertex convention, and ANGLE then draws without the geometry shader. The image is the same:
 * every vertex of a quad the engine builds carries the same values in the attributes behind the flat
 * varyings (Util.FastCreateQuad, TerrainSpriteLayer, RgbaColorRenderer), so both conventions pick the same.
 * Returns 1 if the convention was switched, 0 if the extension is missing, -1 without a current context.
 */
EM_JS(int, playra_gl_first_vertex_convention, (void), {
	var gl = typeof GLctx !== 'undefined' ? GLctx : null;
	if (!gl)
		return -1;
	var ext = gl.getExtension('WEBGL_provoking_vertex');
	if (!ext) {
		console.log('[playra] WebGL: no WEBGL_provoking_vertex, flat varyings keep the last-vertex convention');
		return 0;
	}
	ext.provokingVertexWEBGL(ext.FIRST_VERTEX_CONVENTION_WEBGL);
	console.log('[playra] WebGL: first-vertex provoking convention (WEBGL_provoking_vertex)');
	return 1;
});

static int context_configured = 0;

static GLint unpack_row_length = 0;
static GLint unpack_skip_pixels = 0;
static GLint unpack_skip_rows = 0;

static uint8_t *scratch = NULL;
static size_t scratch_size = 0;

static uint8_t *get_scratch(size_t size)
{
	if (size > scratch_size)
	{
		free(scratch);
		scratch = (uint8_t *)malloc(size);
		scratch_size = scratch ? size : 0;
	}

	return scratch;
}

/* Copies a width x height BGRA rectangle out of the client buffer (honouring the
 * current unpack row length and skips) into a tightly packed RGBA buffer. */
static const uint8_t *convert_bgra(const void *pixels, GLsizei width, GLsizei height)
{
	if (pixels == NULL || width <= 0 || height <= 0)
		return NULL;

	const size_t row_pixels = unpack_row_length > 0 ? (size_t)unpack_row_length : (size_t)width;
	const uint8_t *src = (const uint8_t *)pixels + ((size_t)unpack_skip_rows * row_pixels + (size_t)unpack_skip_pixels) * 4;
	uint8_t *dst = get_scratch((size_t)width * (size_t)height * 4);
	if (dst == NULL)
		return NULL;

	/* Four pixels at a time with a WebAssembly SIMD byte shuffle (the .NET runtime of this build
	 * requires SIMD already), the rest one 32-bit word per pixel (WebAssembly is little endian:
	 * B G R A in memory is 0xAARRGGBB, R G B A is 0xAABBGGRR). Both handle unaligned buffers. */
	uint8_t *out = dst;
	for (GLsizei y = 0; y < height; y++)
	{
		const uint8_t *in = src + (size_t)y * row_pixels * 4;
		GLsizei x = 0;
#ifdef __wasm_simd128__
		for (; x + 4 <= width; x += 4)
		{
			v128_t v = wasm_v128_load(in);
			v = wasm_i8x16_shuffle(v, v, 2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);
			wasm_v128_store(out, v);
			in += 16;
			out += 16;
		}
#endif
		for (; x < width; x++)
		{
			uint32_t p;
			memcpy(&p, in, 4);
			p = (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);
			memcpy(out, &p, 4);
			in += 4;
			out += 4;
		}
	}

	return dst;
}

/* Per texture name: bit 0 = generated and not deleted, bit 1 = bound at least once.
 *
 * WebGL context loss: WebGL's isTexture answers false for every texture of a lost context, this table
 * does not know about the loss and keeps answering true. Harmless today: the page does not handle
 * webglcontextlost, the context is never restored, and the engine's deferred GL error check reads
 * CONTEXT_LOST_WEBGL and stops the page. Whoever adds context restoration must also clear this
 * table (every texture name of the old context is gone). */
#define TEXTURE_LIVE 1
#define TEXTURE_BOUND 2
static uint8_t *texture_state = NULL;
static size_t texture_state_size = 0;

static void texture_state_update(GLuint id, uint8_t set, uint8_t clear)
{
	/* A name the table has never seen has no bits to clear (a delete of a name that was never
	 * generated): do not grow the table for it. */
	if (id == 0 || (set == 0 && id >= texture_state_size))
		return;

	if (id >= texture_state_size)
	{
		size_t size = texture_state_size ? texture_state_size : 1024;
		while (size <= id)
			size *= 2;

		uint8_t *grown = (uint8_t *)realloc(texture_state, size);
		if (grown == NULL)
			abort();

		memset(grown + texture_state_size, 0, size - texture_state_size);
		texture_state = grown;
		texture_state_size = size;
	}

	texture_state[id] = (uint8_t)((texture_state[id] & ~clear) | set);
}

void playra_glGenTextures(GLsizei n, GLuint *textures)
{
	glGenTextures(n, textures);
	for (GLsizei i = 0; i < n; i++)
		texture_state_update(textures[i], TEXTURE_LIVE, TEXTURE_BOUND);
}

GLboolean playra_glIsTexture(GLuint texture)
{
	return texture != 0 && texture < texture_state_size
		&& texture_state[texture] == (TEXTURE_LIVE | TEXTURE_BOUND) ? GL_TRUE : GL_FALSE;
}

static void set_unpack(GLint row_length, GLint skip_pixels, GLint skip_rows)
{
	glPixelStorei(GL_UNPACK_ROW_LENGTH, row_length);
	glPixelStorei(GL_UNPACK_SKIP_PIXELS, skip_pixels);
	glPixelStorei(GL_UNPACK_SKIP_ROWS, skip_rows);
}

/*
 * Repeated whole-texture uploads. Some textures are uploaded again, whole, every frame: the radar (RadarWidget
 * commits its whole sheet in every Draw, up to 512x512) and the palettes (HardwarePalette, every frame), mostly
 * with the same pixels as the frame before. Each such upload cost a BGRA conversion of the whole image, the copy
 * into the browser's GPU transfer buffer and, in the GPU process, a re-specification of the texture (a new
 * storage on some backends). For a texture that has had two BGRA texImage2D uploads of the same size, the shim
 * keeps a copy of the last pixels it uploaded and compares the next upload with it: unchanged pixels are not
 * uploaded at all, and otherwise only the band of rows that changed goes up, with texSubImage2D (same result:
 * the rest of the texture already holds those pixels). Anything else that writes the texture (a sub-image
 * upload, a copy from the framebuffer, rendering into it, another format or size) drops the copy.
 * Only textures up to UPLOAD_COPY_MAX_BYTES, and at most UPLOAD_COPIES of them, are tracked.
 */
#define UPLOAD_COPIES 8
#define UPLOAD_COPY_MAX_BYTES (4u << 20)
#define TEXTURE_UNITS 32

typedef struct
{
	GLuint texture;
	GLsizei width, height;
	GLint internalformat;
	GLenum type;
	unsigned uploads;
	uint8_t *pixels; /* tightly packed BGRA, as last uploaded; NULL until the second upload */
} upload_copy;

static upload_copy upload_copies[UPLOAD_COPIES];
static GLuint active_unit = 0;
static GLuint bound_texture_2d[TEXTURE_UNITS];

static upload_copy *find_upload_copy(GLuint texture)
{
	for (int i = 0; i < UPLOAD_COPIES; i++)
		if (upload_copies[i].texture == texture && texture != 0)
			return &upload_copies[i];

	return NULL;
}

static void drop_upload_copy(GLuint texture)
{
	upload_copy *c = find_upload_copy(texture);
	if (c == NULL)
		return;

	free(c->pixels);
	memset(c, 0, sizeof(*c));
}

static GLuint current_texture_2d(GLenum target)
{
	return target == GL_TEXTURE_2D && active_unit < TEXTURE_UNITS ? bound_texture_2d[active_unit] : 0;
}

/*
 * Redundant texture state changes. Before every draw the engine selects each texture unit its shader samples and
 * binds the unit's texture again (Shader.PrepareRender), nearly always the unit and texture already current. In
 * WebGL each of those calls goes into the browser (a call out of WebAssembly, WebGL's validation, a command for the
 * GPU process), so the shim, which sees every unit selection and binding, drops the ones that change nothing.
 * GL resets a deleted texture's bindings to 0, which glDeleteTextures below mirrors.
 */
static int active_unit_known = 0;

void playra_glActiveTexture(GLenum texture)
{
	if (active_unit_known && texture - GL_TEXTURE0 == active_unit)
		return;

	active_unit = texture - GL_TEXTURE0;
	active_unit_known = 1;
	glActiveTexture(texture);
}

void playra_glFramebufferTexture2D(GLenum target, GLenum attachment, GLenum textarget, GLuint texture, GLint level)
{
	/* Rendering into the texture would change it behind the copy's back. */
	drop_upload_copy(texture);
	glFramebufferTexture2D(target, attachment, textarget, texture, level);
}

/* Row y of two images with rows of row_bytes bytes, compared 64 bytes at a time with WebAssembly SIMD (libc's memcmp
 * compares byte by byte here, and an unchanged radar image is a megabyte to compare every frame). */
static int same_row(const uint8_t *a, const uint8_t *b, size_t row_bytes, GLsizei y)
{
	a += (size_t)y * row_bytes;
	b += (size_t)y * row_bytes;
	size_t i = 0;
#ifdef __wasm_simd128__
	for (; i + 64 <= row_bytes; i += 64)
	{
		v128_t d = wasm_v128_or(
			wasm_v128_or(wasm_v128_xor(wasm_v128_load(a + i), wasm_v128_load(b + i)),
				wasm_v128_xor(wasm_v128_load(a + i + 16), wasm_v128_load(b + i + 16))),
			wasm_v128_or(wasm_v128_xor(wasm_v128_load(a + i + 32), wasm_v128_load(b + i + 32)),
				wasm_v128_xor(wasm_v128_load(a + i + 48), wasm_v128_load(b + i + 48))));
		if (wasm_v128_any_true(d))
			return 0;
	}
#endif
	return memcmp(a + i, b + i, row_bytes - i) == 0;
}

/* A BGRA texImage2D of a whole texture (level 0, no skips). Returns 1 if it was handled from the copy (nothing or
 * only the changed rows uploaded), 0 if the caller must upload it as usual; then the copy is updated after it. */
static int upload_from_copy(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height,
	GLenum type, const void *pixels, upload_copy **record)
{
	*record = NULL;
	GLuint texture = current_texture_2d(target);
	if (texture == 0 || level != 0 || pixels == NULL || unpack_skip_pixels != 0 || unpack_skip_rows != 0
		|| (unpack_row_length != 0 && unpack_row_length != width) || type != GL_UNSIGNED_BYTE)
	{
		drop_upload_copy(texture);
		return 0;
	}

	const size_t row_bytes = (size_t)width * 4;
	const size_t bytes = row_bytes * (size_t)height;
	upload_copy *c = find_upload_copy(texture);
	if (c != NULL && (c->width != width || c->height != height || c->internalformat != internalformat || c->type != type))
	{
		drop_upload_copy(texture);
		c = NULL;
	}

	if (c != NULL && c->pixels != NULL)
	{
		const uint8_t *src = (const uint8_t *)pixels;
		GLsizei first = 0;
		while (first < height && same_row(src, c->pixels, row_bytes, first))
			first++;

		if (first == height)
			return 1;

		GLsizei last = height - 1;
		while (last > first && same_row(src, c->pixels, row_bytes, last))
			last--;

		const GLsizei rows = last - first + 1;
		const uint8_t *rgba = convert_bgra(src + (size_t)first * row_bytes, width, rows);
		if (rgba == NULL)
			return 0;

		set_unpack(0, 0, 0);
		glTexSubImage2D(target, 0, 0, first, width, rows, GL_RGBA, type, rgba);
		set_unpack(unpack_row_length, unpack_skip_pixels, unpack_skip_rows);
		memcpy(c->pixels + (size_t)first * row_bytes, src + (size_t)first * row_bytes, (size_t)rows * row_bytes);
		return 1;
	}

	if (bytes > UPLOAD_COPY_MAX_BYTES)
		return 0;

	if (c == NULL)
	{
		for (int i = 0; i < UPLOAD_COPIES && c == NULL; i++)
			if (upload_copies[i].texture == 0)
				c = &upload_copies[i];

		/* All slots taken: reuse one that never got its copy (a texture uploaded once, e.g. a finished sheet). */
		for (int i = 0; i < UPLOAD_COPIES && c == NULL; i++)
			if (upload_copies[i].pixels == NULL)
				c = &upload_copies[i];

		if (c == NULL)
			return 0;

		memset(c, 0, sizeof(*c));
		c->texture = texture;
		c->width = width;
		c->height = height;
		c->internalformat = internalformat;
		c->type = type;
	}

	*record = c;
	return 0;
}

/* After a full upload: count it, and from the second one on keep its pixels. */
static void record_upload(upload_copy *c, const void *pixels)
{
	if (c == NULL)
		return;

	const size_t bytes = (size_t)c->width * (size_t)c->height * 4;
	if (++c->uploads >= 2 && c->pixels == NULL)
		c->pixels = (uint8_t *)malloc(bytes);

	if (c->pixels != NULL)
		memcpy(c->pixels, pixels, bytes);
}

void playra_glDeleteTextures(GLsizei n, const GLuint *textures)
{
	for (GLsizei i = 0; i < n; i++)
	{
		texture_state_update(textures[i], 0, TEXTURE_LIVE | TEXTURE_BOUND);
		drop_upload_copy(textures[i]);
		for (int u = 0; u < TEXTURE_UNITS; u++)
			if (bound_texture_2d[u] == textures[i])
				bound_texture_2d[u] = 0;
	}

	glDeleteTextures(n, textures);
}

void playra_glBindTexture(GLenum target, GLuint texture)
{
	if (texture != 0 && texture < texture_state_size && (texture_state[texture] & TEXTURE_LIVE))
		texture_state[texture] |= TEXTURE_BOUND;
	if (target == GL_TEXTURE_2D && active_unit < TEXTURE_UNITS)
	{
		if (bound_texture_2d[active_unit] == texture && texture != 0)
			return;

		bound_texture_2d[active_unit] = texture;
	}

	glBindTexture(target, texture);
}

void playra_glPixelStorei(GLenum pname, GLint param)
{
	switch (pname)
	{
		case GL_UNPACK_ROW_LENGTH: unpack_row_length = param; break;
		case GL_UNPACK_SKIP_PIXELS: unpack_skip_pixels = param; break;
		case GL_UNPACK_SKIP_ROWS: unpack_skip_rows = param; break;
		default: break;
	}

	glPixelStorei(pname, param);
}

void playra_glTexImage2D(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height,
	GLint border, GLenum format, GLenum type, const void *pixels)
{
	if (internalformat == PLAYRA_GL_BGRA)
		internalformat = GL_RGBA;

	if (format != PLAYRA_GL_BGRA)
	{
		drop_upload_copy(current_texture_2d(target));
		glTexImage2D(target, level, internalformat, width, height, border, format, type, pixels);
		return;
	}

	upload_copy *record;
	if (upload_from_copy(target, level, internalformat, width, height, type, pixels, &record))
		return;

	const uint8_t *rgba = convert_bgra(pixels, width, height);
	set_unpack(0, 0, 0);
	glTexImage2D(target, level, internalformat, width, height, border, GL_RGBA, type, rgba);
	set_unpack(unpack_row_length, unpack_skip_pixels, unpack_skip_rows);
	record_upload(record, pixels);
}

void playra_glTexSubImage2D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLsizei width, GLsizei height,
	GLenum format, GLenum type, const void *pixels)
{
	drop_upload_copy(current_texture_2d(target));
	if (format != PLAYRA_GL_BGRA)
	{
		glTexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, pixels);
		return;
	}

	const uint8_t *rgba = convert_bgra(pixels, width, height);
	if (rgba == NULL)
		return;

	set_unpack(0, 0, 0);
	glTexSubImage2D(target, level, xoffset, yoffset, width, height, GL_RGBA, type, rgba);
	set_unpack(unpack_row_length, unpack_skip_pixels, unpack_skip_rows);
}

void playra_glCopyTexImage2D(GLenum target, GLint level, GLenum internalformat, GLint x, GLint y,
	GLsizei width, GLsizei height, GLint border)
{
	if (internalformat == PLAYRA_GL_BGRA)
		internalformat = GL_RGBA;

	drop_upload_copy(current_texture_2d(target));
	glCopyTexImage2D(target, level, internalformat, x, y, width, height, border);
}

/*
 * glFlush: the engine flushes whenever it binds or unbinds a framebuffer (FrameBuffer.Bind/Unbind, four times a
 * frame), which on a desktop GL hands the queued commands to the driver early. In WebGL every flush is a call into
 * the browser that submits the command buffer to its GPU process (a message to that process, and in ANGLE's D3D11
 * backend a flush of the D3D device context there), and it bought nothing: the commands of one context run in
 * order anyway, and the browser submits everything at the end of each frame by itself. It cost 3 % of the main
 * thread in a match (V8 profile), so the browser build does not pass it on.
 */
void playra_glFlush(void)
{
}

/* The calls the shim replaces. OpenRA.Web/tools/gen-gl-signatures.py reads this table: the engine's direct GL
 * bindings (DllImports in the generated GLEntryPoints) call these functions instead of the GL ones of the same name. */
static const struct
{
	const char *name;
	void *function;
} replaced[] = {
	{ "glPixelStorei", (void *)playra_glPixelStorei },
	{ "glTexImage2D", (void *)playra_glTexImage2D },
	{ "glTexSubImage2D", (void *)playra_glTexSubImage2D },
	{ "glCopyTexImage2D", (void *)playra_glCopyTexImage2D },
	{ "glGenTextures", (void *)playra_glGenTextures },
	{ "glDeleteTextures", (void *)playra_glDeleteTextures },
	{ "glBindTexture", (void *)playra_glBindTexture },
	{ "glIsTexture", (void *)playra_glIsTexture },
	{ "glFlush", (void *)playra_glFlush },
	{ "glActiveTexture", (void *)playra_glActiveTexture },
	{ "glFramebufferTexture2D", (void *)playra_glFramebufferTexture2D },
};

void *playra_gl_get_proc_address(const char *name)
{
	/* The engine looks up every GL entry point right after it has created the context and made it current
	 * (OpenGL.Initialize), so the first lookup is the moment to configure the context. The page has one
	 * WebGL context for its whole life. */
	if (!context_configured)
		context_configured = playra_gl_first_vertex_convention() >= 0;

	for (size_t i = 0; i < sizeof(replaced) / sizeof(replaced[0]); i++)
		if (strcmp(name, replaced[i].name) == 0)
			return replaced[i].function;

	return SDL_GL_GetProcAddress(name);
}
