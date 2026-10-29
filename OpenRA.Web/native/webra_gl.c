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
 * In the browser that lookup goes through webra_gl_get_proc_address(), which returns
 * emscripten's WebGL 2 implementation for everything except the calls below:
 *
 * The engine's GLES profile uploads textures as GL_BGRA (GL_EXT_texture_format_BGRA8888),
 * which WebGL 2 does not support. These wrappers convert BGRA to RGBA on upload so that
 * the engine and its shaders stay unchanged. Pixel data is only touched on texture uploads,
 * which happen when sheets are created or updated, not every frame.
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <GLES3/gl3.h>
#ifdef __wasm_simd128__
#include <wasm_simd128.h>
#endif

#define WEBRA_GL_BGRA 0x80E1

extern void *SDL_GL_GetProcAddress(const char *proc);

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

/* Per texture name: bit 0 = generated and not deleted, bit 1 = bound at least once. */
#define TEXTURE_LIVE 1
#define TEXTURE_BOUND 2
static uint8_t *texture_state = NULL;
static size_t texture_state_size = 0;

static void texture_state_update(GLuint id, uint8_t set, uint8_t clear)
{
	if (id == 0)
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

static void webra_glGenTextures(GLsizei n, GLuint *textures)
{
	glGenTextures(n, textures);
	for (GLsizei i = 0; i < n; i++)
		texture_state_update(textures[i], TEXTURE_LIVE, TEXTURE_BOUND);
}

static void webra_glDeleteTextures(GLsizei n, const GLuint *textures)
{
	for (GLsizei i = 0; i < n; i++)
		texture_state_update(textures[i], 0, TEXTURE_LIVE | TEXTURE_BOUND);
	glDeleteTextures(n, textures);
}

static void webra_glBindTexture(GLenum target, GLuint texture)
{
	if (texture != 0 && texture < texture_state_size && (texture_state[texture] & TEXTURE_LIVE))
		texture_state[texture] |= TEXTURE_BOUND;
	glBindTexture(target, texture);
}

static GLboolean webra_glIsTexture(GLuint texture)
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

static void webra_glPixelStorei(GLenum pname, GLint param)
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

static void webra_glTexImage2D(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height,
	GLint border, GLenum format, GLenum type, const void *pixels)
{
	if (internalformat == WEBRA_GL_BGRA)
		internalformat = GL_RGBA;

	if (format != WEBRA_GL_BGRA)
	{
		glTexImage2D(target, level, internalformat, width, height, border, format, type, pixels);
		return;
	}

	const uint8_t *rgba = convert_bgra(pixels, width, height);
	set_unpack(0, 0, 0);
	glTexImage2D(target, level, internalformat, width, height, border, GL_RGBA, type, rgba);
	set_unpack(unpack_row_length, unpack_skip_pixels, unpack_skip_rows);
}

static void webra_glTexSubImage2D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLsizei width, GLsizei height,
	GLenum format, GLenum type, const void *pixels)
{
	if (format != WEBRA_GL_BGRA)
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

static void webra_glCopyTexImage2D(GLenum target, GLint level, GLenum internalformat, GLint x, GLint y,
	GLsizei width, GLsizei height, GLint border)
{
	if (internalformat == WEBRA_GL_BGRA)
		internalformat = GL_RGBA;

	glCopyTexImage2D(target, level, internalformat, x, y, width, height, border);
}

void *webra_gl_get_proc_address(const char *name)
{
	if (strcmp(name, "glPixelStorei") == 0)
		return (void *)webra_glPixelStorei;

	if (strcmp(name, "glTexImage2D") == 0)
		return (void *)webra_glTexImage2D;

	if (strcmp(name, "glTexSubImage2D") == 0)
		return (void *)webra_glTexSubImage2D;

	if (strcmp(name, "glCopyTexImage2D") == 0)
		return (void *)webra_glCopyTexImage2D;

	if (strcmp(name, "glGenTextures") == 0)
		return (void *)webra_glGenTextures;

	if (strcmp(name, "glDeleteTextures") == 0)
		return (void *)webra_glDeleteTextures;

	if (strcmp(name, "glBindTexture") == 0)
		return (void *)webra_glBindTexture;

	if (strcmp(name, "glIsTexture") == 0)
		return (void *)webra_glIsTexture;

	return SDL_GL_GetProcAddress(name);
}
