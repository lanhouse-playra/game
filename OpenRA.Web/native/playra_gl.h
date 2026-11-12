/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

/* The GL calls the browser GL shim (playra_gl.c) replaces, as the engine's DllImports call them. */

#ifndef PLAYRA_GL_H
#define PLAYRA_GL_H

#include <GLES3/gl3.h>

void *playra_gl_get_proc_address(const char *name);

void playra_glPixelStorei(GLenum pname, GLint param);
void playra_glTexImage2D(GLenum target, GLint level, GLint internalformat, GLsizei width, GLsizei height,
	GLint border, GLenum format, GLenum type, const void *pixels);
void playra_glTexSubImage2D(GLenum target, GLint level, GLint xoffset, GLint yoffset, GLsizei width, GLsizei height,
	GLenum format, GLenum type, const void *pixels);
void playra_glCopyTexImage2D(GLenum target, GLint level, GLenum internalformat, GLint x, GLint y,
	GLsizei width, GLsizei height, GLint border);
void playra_glGenTextures(GLsizei n, GLuint *textures);
void playra_glDeleteTextures(GLsizei n, const GLuint *textures);
void playra_glBindTexture(GLenum target, GLuint texture);
GLboolean playra_glIsTexture(GLuint texture);
void playra_glFlush(void);
void playra_glActiveTexture(GLenum texture);
void playra_glFramebufferTexture2D(GLenum target, GLenum attachment, GLenum textarget, GLuint texture, GLint level);

#endif
