#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Eluant;
using OpenRA.Scripting;
using OpenRA.Support;

namespace OpenRA.Web.Conformance
{
	/// <summary>
	/// Numeric conformance corpus: the same conversions, math and Lua snippets run on desktop (CoreCLR,
	/// the packaged Lua 5.1 library) and in the browser (Mono WebAssembly, interpreter and AOT, our Lua
	/// build), and the outputs are diffed. Lockstep multiplayer needs them to be identical.
	///
	/// Output: one "section key = value" line per check. Sections starting with "info." describe the
	/// runtime itself (raw casts, culture) and are expected to differ; every other line must be equal.
	/// Doubles are printed with their bit pattern, so a last-bit difference is visible.
	/// </summary>
	public static class ConformanceCorpus
	{
		public const string Version = "2";

		// Every branch of a double-to-integer conversion: in range, fractions, both signs, the edges of
		// byte/short/int/long, beyond them, infinities, NaNs (quiet, negative, signaling), denormals.
		static readonly double[] ConversionInputs =
		[
			0.0, -0.0, 0.5, -0.5, 0.9999999999999999, 1.0, -1.0, 1.5, -1.5, 2.5, -2.5, 0.1, 1.0 / 3,
			127.5, 128, -128.5, -129, 255, 255.5, 256, 256.5, -255.5, -256,
			32767, 32767.5, 32768, -32768.5, -32769, 65535, 65536, 65536.7, -65536.7, -70000.7,
			2147483646.9, 2147483647, 2147483647.5, 2147483648, 2147483648.5, -2147483647.5, -2147483648,
			-2147483648.5, -2147483649, 3e9, -3e9, 4294967295, 4294967295.5, 4294967296, 4294967297.5, -4294967297.5,
			9007199254740992, 9007199254740993, -9007199254740993, 9.2233720368547748E18, 9.2233720368547758E18,
			-9.2233720368547758E18, -9.2233720368547778E18, 1.8446744073709552E19, 1e19, 1e20, -1e20,
			1e300, -1e300, double.MaxValue, double.MinValue, double.Epsilon, -double.Epsilon, 1e-310, -1e-310,
			double.PositiveInfinity, double.NegativeInfinity, double.NaN, -double.NaN,
			BitConverter.Int64BitsToDouble(0x7FF0000000000001), BitConverter.Int64BitsToDouble(unchecked((long)0xFFF4000000000000)),
		];

		// Values computed by Lua and handed to the CLR through OpenRA's script bindings.
		static readonly string[] ScriptNumberExpressions =
		[
			"0", "-0", "0.5", "-0.5", "-1.5", "1.5", "2.5", "-2.5", "100.99", "-100.99", "127.5", "128", "-129",
			"255", "255.5", "256", "-255.5", "-70000.7", "65536", "65536.7", "32767.5", "-32769",
			"2^31", "2^31 - 1", "2^31 - 0.5", "-2^31", "-2^31 - 1", "-2^31 - 0.5", "3e9", "-3e9", "2^32", "2^32 + 5",
			"2^53 + 1", "2^63", "-2^63", "2^64", "1e300", "-1e300", "math.huge", "-math.huge", "0/0", "-(0/0)",
			"math.huge - math.huge", "2^-1074", "-2^-1074", "1/3", "math.pi * 1e6",
		];

		// Lua snippets: the integer conversions inside the Lua library (lua_Integer, luaL_checkint), string
		// positions, number formatting and parsing, arithmetic and the math library. Must not hang or
		// allocate much on any implementation.
		static readonly string[] LuaSnippets =
		[
			// string.sub: positions go through lua_Integer (64-bit on desktop x64)
			"('abc'):sub(1, 3e9)", "('abc'):sub(1, 2^31)", "('abc'):sub(1, 2^31 - 1)", "('abc'):sub(-2^31 - 1)",
			"('abc'):sub(2^32 + 1)", "('abc'):sub(1, 2^32 + 2)", "('abc'):sub(-2^32 + 2)", "('abc'):sub(1, 0/0)",
			"('abc'):sub(1, 1/0)", "('abc'):sub(-1/0)", "('abc'):sub(1/0)", "('abc'):sub(2^63)", "('abc'):sub(-2^63)",
			"('abc'):sub(1.9)", "('abc'):sub(-1.5)", "('abcdef'):sub(2.5, 4.5)", "('abcdef'):sub(-3)", "('abc'):sub(0)",
			"('abc'):sub(2^53)", "('abc'):sub(1, -2^32 - 1)", "('abc'):sub(1e300)", "('abc'):sub(-1e300)",

			// string.byte, string.find, string.match, string.gmatch with odd positions
			"string.byte('abc', 2^32 + 1)", "string.byte('abc', -2^32)", "string.byte('abc', 1, 2^32 + 2)",
			"string.byte('abc', 0/0)", "string.byte('abc', 1/0)", "string.byte('abc', -1/0, 2)", "string.byte('abc', 2.7)",
			"string.find('abcabc', 'c', 2^32 + 2)", "string.find('abc', 'b', 3e9)", "string.find('abc', 'b', -3e9)",
			"string.find('abc', 'b', 0/0)", "string.find('abc', 'b', 1/0)", "string.find('abc', 'b', -1/0)",
			"string.find('abcabc', 'b', 2^32 + 3, true)", "string.match('key=val', '(%w+)=(%w+)', 2^32 + 1)",
			"string.find('a.b', '.', 1, true)", "string.find('hello world', 'o w')",

			// luaL_checkint: lua_Integer then truncated to int
			"string.rep('ab', 3e9)", "string.rep('ab', 2^32 + 3)", "string.rep('ab', 2.9)", "string.rep('ab', -1)",
			"string.rep('ab', 0/0)", "string.rep('ab', 1/0)", "string.rep('ab', -2^32 + 2)", "string.rep('ab', 2^31)",
			"string.char(2^32 + 65)", "string.char(65.9)", "string.char(-2^32 + 66)", "string.char(2^63)",
			"math.ldexp(1, 2^32 + 1)", "math.ldexp(1, 0/0)", "math.ldexp(1.5, -1074)", "math.ldexp(1, 1024)",
			"select(2^32 + 2, 'a', 'b', 'c')", "select(-1, 'a', 'b', 'c')", "select('#', nil, nil)",
			"unpack({1, 2, 3}, 2^32 + 1, 2^32 + 3)", "unpack({1, 2, 3}, -1, 1)", "unpack({1, 2, 3}, 2)",
			"table.concat({1, 2, 3}, ',', 2^32 + 1, 2^32 + 2)", "table.concat({1, 2, 3}, ',', 2.9)",
			"(function() local t = {1, 2, 3}; table.insert(t, 2^32 + 1, 'x'); return table.concat(t, ',') end)()",
			"(function() local t = {1, 2, 3}; table.remove(t, 2^32 + 2); return table.concat(t, ',') end)()",
			"tonumber('10', 2^32 + 16)", "tonumber('ff', 16)", "tonumber('zz', 36)", "tonumber('8', 2.9)",
			"tonumber('777', 8.5)", "#{1, 2, nil, 4}", "#'abc'",

			// string.format integer conversions (LUA_INTFRM_T, long on desktop Linux x64)
			"string.format('%d', 3e9)", "string.format('%d', -3e9)", "string.format('%d', 2^53 + 1)",
			"string.format('%d', 2^63)", "string.format('%d', -2^63)", "string.format('%d', 1e300)",
			"string.format('%d', 0/0)", "string.format('%d', 1/0)", "string.format('%d', -1/0)",
			"string.format('%d', -1.5)", "string.format('%d', 2.99)", "string.format('%i', 2^31)",
			"string.format('%x', -1)", "string.format('%X', 2^40 + 255)", "string.format('%o', 8)",
			"string.format('%u', -1)", "string.format('%u', 3e9)", "string.format('%5d|%-5d|%05d', 42, 42, 42)",
			"string.format('%c', 65)", "string.format('%c', 2^32 + 66)", "string.format('%c', 65.7)",
			"string.format('%u', 2^63)", "string.format('%x', 2^64)", "string.format('%x', 1e19)", "string.format('%x', 0/0)",
			"string.format('%x', 1/0)", "string.format('%x', -1/0)", "string.format('%u', -2^63 - 2^12)", "string.format('%o', -8)",
			"string.format('%X', -2^31)", "string.format('%u', 2^64 - 2^11)",

			// string.format floating point and strings
			"string.format('%5.2f', 1/3)", "string.format('%.3f', 2.0005)", "string.format('%.0f', 0.5)",
			"string.format('%.0f', 1.5)", "string.format('%.0f', 2.5)", "string.format('%e', 12345.6789)",
			"string.format('%.10e', 1/3)", "string.format('%g', 1e300 * 10)", "string.format('%g', -1e300 * 10)",
			"string.format('%g', 0/0)", "string.format('%f', 0/0)", "string.format('%.14g', 0.1)",
			"string.format('%.17g', 0.1)", "string.format('%g', 100000)", "string.format('%g', 1000000)",
			"string.format('%G', 1e-5)", "string.format('%.20f', 1e-7)", "string.format('%.3s|%5s', 'abcdef', 'x')",
			"string.format('%q', 'a\\0b\\n\"c\\\\')", "string.format('%s', 1e15)", "string.format('%s', 1e16)",
			"string.format('%.99f', 1/3):len()", "string.format('%10.4f|%-10.2e|', math.pi, -math.pi)",

			// tostring and tonumber
			"tostring(0.1)", "tostring(1/3)", "tostring(-1/3)", "tostring(1e15)", "tostring(1e16)", "tostring(123456789012345678)",
			"tostring(2^53 + 1)", "tostring(2^63)", "tostring(-0)", "tostring(0/0)", "tostring(-(0/0))",
			"tostring(1/0)", "tostring(-1/0)", "tostring(1e-310)", "tostring(5e-324)", "tostring(3.14159265358979)",
			"tostring(100)", "tostring(-1.5)", "tostring(1e100)", "tostring(2^-1074)", "tostring(1.7976931348623157e308)",
			"tonumber('0x10')", "tonumber('0X1f')", "tonumber('1e400')", "tonumber('-1e400')", "tonumber(' 12 ')",
			"tonumber('12a')", "tonumber('0x')", "tonumber('1e')", "tonumber('inf')", "tonumber('nan')",
			"tonumber('.5')", "tonumber('5.')", "tonumber('1e-400')", "tonumber('0x7fffffffffffffff')",
			"tonumber('0xffffffffffffffffff')", "tonumber('123456789012345678901234567890')", "tonumber('-0')",
			"tonumber('ffffffffff', 16)", "tonumber('-1', 16)", "tonumber('zzzzzzzzzzzzzz', 36)", "tonumber('  -ff', 16)",
			"tonumber('1' .. string.rep('0', 30), 16)", "tonumber('7fffffff', 16)", "tonumber('80000000', 16)",
			"tonumber('0x', 16)", "tonumber('0xg', 16)", "tonumber(' 0x', 16)", "tonumber('0x1', 16)", "tonumber('-0x', 16)",
			"tonumber('0X')", "tonumber(' 0x ')", "tonumber('0x.')", "tonumber('0x.8')",
			"tonumber('infinity')", "tonumber('0x1p4')", "tonumber('0x1p-1074')", "tonumber('1e-5')",
			"tonumber(' \\t\\n42\\r ')", "tonumber('4.9e-324')", "tonumber('2.4703282292062328e-324')",
			"'10' + 5", "'0x10' * 2", "'3' .. 4", "1e15 .. ''", "2^63 .. ''", "-0 .. ''", "10 / 2 .. ''",

			// arithmetic and comparisons
			"7 % 3", "-7 % 3", "7 % -3", "-7 % -3", "5.5 % 2", "-5.5 % 2", "5 % 0", "-5 % 0", "0 % 0", "5 % 1/0",
			"5 % math.huge", "-5 % math.huge", "1e300 % 7", "2^53 % 3", "0.1 + 0.2", "0.1 + 0.2 == 0.3",
			"1e308 * 10", "-1e308 * 10", "2^1024", "2^-1075", "2^-1074", "2^0.5", "(-8)^(1/3)", "0^0", "0^-1",
			"(-2)^0.5", "(-0)^-1", "10 / 3", "-10 / 3", "1 / -0", "0/0 == 0/0", "1 < 0/0", "1 > 0/0",
			"'a' < 'B'", "'abc' < 'abd'", "'' < 'a'", "'Z' < 'a'", "'a\\0b' < 'a\\0c'", "1e15 + 0.3",
			"(2^53 + 1) - 2^53", "1e16 + 1 - 1e16", "-(-0)", "math.huge == 1/0", "3 == 3.0000000000000001",

			// math library: exact operations
			"math.floor(-0.5)", "math.floor(-0)", "math.ceil(-0.5)", "math.floor(2^53 + 1)", "math.floor(3e9)",
			"math.floor(-3e9 - 0.5)", "math.floor(0/0)", "math.floor(1/0)", "math.ceil(1e-310)", "math.floor(-1e-310)",
			"math.fmod(5.5, 2)", "math.fmod(-5.5, 2)", "math.fmod(5, 0)", "math.fmod(1/0, 1)", "math.fmod(5, 1/0)",
			"math.fmod(1e300, 3)", "math.fmod(-0, 1)", "math.modf(-3.7)", "math.modf(3.7)", "math.modf(1/0)",
			"math.modf(-1/0)", "math.modf(0/0)", "math.modf(-0)", "math.frexp(0)", "math.frexp(-0)", "math.frexp(1e-310)",
			"math.frexp(1/0)", "math.frexp(0/0)", "math.frexp(-1234.5)", "math.abs(-0)", "math.abs(-1/0)",
			"math.abs(-2^63)", "math.max(0/0, 1)", "math.max(1, 0/0)", "math.min(0/0, 1)", "math.min(1, 0/0)",
			"math.max(-0, 0)", "math.max(0, -0)", "math.min(-0, 0)", "math.max(1, 2, 3e9, -1)", "math.huge",
			"-math.huge", "math.pi", "math.sqrt(-1)", "math.sqrt(2)", "math.sqrt(1e-310)", "math.sqrt(-0)",
			"math.deg(1)", "math.rad(180)", "math.deg(math.pi)", "math.rad(1/0)",

			// NaN results: the sign of a NaN shows in tostring ("nan" or "-nan")
			"math.asin(-2)", "math.acos(2)", "math.acos(1/0)", "math.asin(-1/0)", "math.log(-1/0)", "math.log10(-0.5)",
			"math.log(0/0)", "math.log(-(0/0))", "math.sin(1/0)", "math.cos(-1/0)", "math.tan(1/0)", "math.atan2(0/0, 1)",
			"math.exp(0/0)", "math.sinh(0/0)", "math.pow(-8, 1/3)", "math.sqrt(-1)", "math.fmod(5, 0)", "math.fmod(1/0, 2)",
			"math.cosh(0/0)", "math.tanh(0/0)", "math.atan(0/0)", "math.sin(0/0)", "math.exp(-(0/0))", "math.cosh(-(0/0))",
			"math.sinh(-(0/0))", "math.tan(0/0)", "math.log10(0/0)", "math.acos(0/0)", "math.atan2(1, 0/0)", "math.pow(0/0, 1)",
			"math.fmod(0/0, 1)", "math.floor(-(0/0))", "math.abs(0/0)",
			"(1/0) - (1/0)", "(1/0) * 0", "-(1/0 - 1/0)", "math.huge % 2", "0/0 + 1",

			// table iteration order (depends on Lua's hashing of strings and numbers, not on the CLR)
			"(function() local t = {alpha = 1, beta = 2, gamma = 3, delta = 4, [1.5] = 5, [2^40] = 6, [-1] = 7, [0] = 8, [3] = 9, [true] = 10, eps = 11}; local o = {}; for k, v in pairs(t) do o[#o + 1] = tostring(k) .. '=' .. v end; return table.concat(o, ',') end)()",
			"(function() local t = {}; for i = 1, 40 do t['k' .. i * 7] = i end; for i = 1, 40, 3 do t['k' .. i * 7] = nil end; local o = {}; for k in pairs(t) do o[#o + 1] = k end; return table.concat(o, ',') end)()",
			"(function() local t = {}; for i = 1, 30 do t[i * 0.5] = i end; local o = {}; for k in pairs(t) do o[#o + 1] = k end; return table.concat(o, ',') end)()",
			"(function() local t = {5, 2, 8, 1, 9, 3, 7, 4, 6, 0}; table.sort(t); return table.concat(t, ',') end)()",
			"(function() local t = {5, 2, 8, 1, 9, 3, 7, 4, 6, 0}; table.sort(t, function(a, b) return a % 3 < b % 3 end); return table.concat(t, ',') end)()",
			"(function() local c = 0; for i = 1, 2, 0.1 do c = c + 1 end; return c end)()",
			"(function() local s = 0; for i = 1, 1000 do s = s + 1 / i end; return s end)()",
			"(function() local s = 1; for i = 1, 200 do s = s * 1.1 end; return s end)()",
			"(function() local o = {}; for w in string.gmatch('one two  three', '%a+') do o[#o + 1] = w end; return table.concat(o, '|') end)()",
			"string.gsub('hello world', 'o', '0', 2^32 + 1)", "string.gsub('abc', '', '-')", "string.upper('aBc\\200')",
			"string.len('\\0\\0')", "string.reverse('abc')",
		];

		// libm transcendental functions at a few points (the sweep covers more). Their results depend on the
		// C library (glibc, the Windows CRT, Apple's libm and musl all differ in the last bit for some
		// arguments), so they are reported in their own section, "lua.libm".
		static readonly string[] LuaLibmSnippets =
		[
			"math.pow(2, 0.5)", "math.pow(-8, 1/3)", "math.pow(10, -5)", "math.pow(2, 1023.5)", "math.pow(0, 0)",
			"math.exp(1)", "math.exp(709.8)", "math.exp(-745.2)", "math.log(10)", "math.log(0)", "math.log(-1)",
			"math.log10(1000)", "math.log10(2)", "math.log10(0)", "math.sin(1)", "math.sin(math.pi)", "math.sin(1e22)",
			"math.cos(1)", "math.cos(math.pi / 2)", "math.tan(math.pi / 4)", "math.tan(1.5707963267948966)",
			"math.asin(1)", "math.asin(2)", "math.acos(-1)", "math.atan(1)", "math.atan2(3, 7)", "math.atan2(0, -0)",
			"math.atan2(-0, -1)", "math.atan2(1/0, 1/0)", "math.sinh(1)", "math.cosh(1)", "math.tanh(0.5)",
			"math.sinh(710)", "math.exp(0.5)", "math.log(2)", "math.log(1e-310)",
		];

		// Arguments for the libm sweep (Lua math.* and C# Math.*): small, large, negative, near multiples of pi.
		static double[] SweepInputs()
		{
			var values = new List<double>();
			for (var i = -60; i <= 60; i++)
				values.Add(i * 0.37 + 0.011);
			for (var i = 1; i <= 40; i++)
				values.Add(i * 3.14159265358979 / 8);
			for (var i = 1; i <= 20; i++)
				values.Add(Math.Pow(10, i) + 0.5);
			for (var i = 1; i <= 20; i++)
				values.Add(1.0 / (i * 7.3));
			values.AddRange([0.0, -0.0, 1e-300, 1e-310, 700, 709.7, -700, 1e22, 1e300, 0.7853981633974483, 1.5707963267948966]);
			return values.ToArray();
		}

		// A fixed key, used only by these vectors, and its RSASSA-PKCS1-v1_5 SHA-1 signatures as OpenSSL makes them: the
		// browser build signs with ManagedRsa (System.Security.Cryptography.RSA is not supported on browser-wasm), and its
		// signatures must be the same bytes. OpenRA.Test checks the same vectors on desktop (ManagedRsaTest).
		const string RsaTestKey =
			"<RSAKeyValue>" +
			"<Modulus>" +
				"2MHYk5VXtbB9qaV+sduSrvmx2UJfvAeoMWYDQoS8rX0UvR4bPY2lotdSB35qs2X80weDMR8oLUgZYW0Vxoq3php8P/3HA8bk" +
				"60VEdUNChhKFXY/qtGVnhZlmvWV3rRHoBk7pNKb2nNA0GU5kFxFKyFwsZJaxdApRAqgFJmcvlAQa0o6GtL9IwMvfvJOGr8gm" +
				"waEwZPSo+MeZU3bJbLvwW+DI51nVx77IPezzGbVQhYn6sL8zo4tpxi3emsOHyQegZ5fgkxn8Sc356JKwej+rl4YxuVMD7fg2" +
				"BBF1QtMzh6yS2COVgheQLzVxPTRD+H/l1Y57NNr2uncndStREJBoMQ==" +
			"</Modulus>" +
			"<Exponent>" +
				"AQAB" +
			"</Exponent>" +
			"<P>" +
				"//zxqWRBOxiVBnfkRNt41YBKHs/ZyYXAjjMILGJ9xjHDGdY808ssixld6Jy91htJVVOlKyDBOJ1ODDGNhT1BjoMIGRL1nvps" +
				"e0xPmvn//XjmirczsWhHDAO+a3jaY1YCZEDKqxcYIBzFy2qurggScUPxuqxM2ymwRXraG7qkQOU=" +
			"</P>" +
			"<Q>" +
				"2MRvBPZWoZSgAdCvYv0dBsZAMQgEEfcKH/2KwQJEvbA0oybrtbecWAaY6fOZKGvSv45zL6sm8Nzou4c4gJEm2Rq2s67fLKUO" +
				"q2HzTu67xVBjP03XCEuPyxNurfRr5Z7mbCxYt4zrsGWpG3A6W+OwuIE1I0DK/T9kg6WnbnYFMV0=" +
			"</Q>" +
			"<DP>" +
				"qmNM33kxygoUt5d5wzNqfIHF1Dh5OB/lCUGCgTiViyR4PmxRVI/5+CRMU5Zw7WCAMWFZoRlOQ0bBOnCJSFR7nEYvT7FE+Cqr" +
				"pm4m1nrtH2Qn39ENgVptX9vmFVb91fzb3qsZu0PF9SnxpfI+juwkimu1LJGfHonjIHwjTS2wQtk=" +
			"</DP>" +
			"<DQ>" +
				"1bYNEqJf1Nw4SLHbSMftpzEsNmsFwESEKfgJpbK73RxtOPF1cGn+7VZLylRNiumLFSq3a6bdNJlHpyNw1GjHGULaIBrnkxeE" +
				"d/mRLR/35lgUEWSH/Xip4vhcpYqWGgPof3vVV9nKwNXpvNCP6Op5394bao9F0hp+2cAna6NWCU0=" +
			"</DQ>" +
			"<InverseQ>" +
				"XfCoDWyDGO+K7oktDmK/EfDmgxDQ5zy8ZnohqiIbPzRNMsGWplyyMZNF6b8vT05CrRNVZ0Ol6eCYnTS6/0GCpZxnI3sgkdFl" +
				"paNxFryIu8428iEe95gO48LuM+WXp8LCwDnilVNLlxk5p/Qvc1nE0x7kk2sG3oKfQA161cRMKzQ=" +
			"</InverseQ>" +
			"<D>" +
				"BN8uf/quUv4ZhPOcSmEWBerEzY5JWo+Nfza8wgT7vKaADdQcZqDFZUiTpml3+npDWXbkwvjhTUs+jssF30WE5S3ZNxL8IyCY" +
				"SpVu+Xe+igbminjOnKlez0joXz9lljU90s55PV63UkT3ts3lQAN6e9BeixA8AtCsi3O9UeSl1BzM4Vf3cedK7ZHJW+kXWBzg" +
				"Qa389A7cJXpEyq72ZhN8Fa1gQC5FeZNRSAAdMRk2nJJZxPBJFWes7Ugrcn7hKgHkI91GLcDxXBKqfrgGTYT6eZAwAIRTbsUL" +
				"PiXPzpfxOcU+nGsPuaAWAcsglx77ugeP7op1pYRELS4Z0qtSNfwZ8Q==" +
			"</D>" +
			"</RSAKeyValue>";

		static readonly (string Name, string Data, string Signature)[] RsaVectors =
		[
			("empty", "",
				"OAm0RIZdgjYMNNXSRmm1bPaD34OoJ0LAQJVNN2jQn/ozE64YLfFTeNvWqpLs8TwrTt+c6YAMTX+m8LjYrFnWEFRRVU5KBF7z" +
				"M91XQxvr3SqEo+DksBLJP8wJuNrTpm2YD9nSiFEsKnTMsRvkZEj4dQxJqV6PKyy466xdKXpXRyZHpf/8w3uJn7Gz96gw9+Gh" +
				"cQVGiSKHJOAlRrJt8IfzLcD9DC61ax7sUI/jlE/twp0B6mxY1sKdvkaSFGxBxjGuz6Az70czMwGUu9U7QlG+Z4FT1qhRselw" +
				"mYg3/g7cnLwqQY96DtWXofiChMeghWJ95V+mSH8abyFQBGLT1t77aA=="),
			("text", "OpenRA known-answer test é✓",
				"0i2pQTF2ljUzAFYj8yOxhY0C9z5BIZHkcuku24e5WU0hgSEWSeVqC4dTZUUAvUGHfR0AcIzCgt7EjM7NLeteF8G01DJjRtPz" +
				"3ziSvvOXSOui2RTC+BfVRDCW2gDp0ClMPBtD2SGFJ/RZ3PWgL2Vkv3w/wveP03tSjsaZzhS25Qjo3JnjtdRrAFng0ZtrUeBz" +
				"+UAvfprLNB2UZjDzQXYy9rHQ72kV97H8eDQksUESIfhMLpHcY65lQhEA7UB77LCD8/ICH92akTSIDdZ5G1z/LTcxgOZxdxJv" +
				"O4rlmC0sJOGBeXedKvogrmNLtwYKFFCRT8mqaORO+ZRuqUNnF8X90g=="),
		];

		static readonly string[] SweepFunctions = ["sin", "cos", "tan", "asin", "acos", "atan", "exp", "log", "log10", "sqrt", "sinh", "cosh", "tanh"];

		public static string Run()
		{
			var output = new StringBuilder();
			void Line(string section, string key, string value) =>
				output.Append(section).Append(' ').Append(key).Append(" = ").Append(value.Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');

			Line("info.runtime", "corpus", Version);
			Line("info.runtime", "framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
			Line("info.runtime", "os", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
			Line("info.runtime", "arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
			Line("info.runtime", "culture", CultureInfo.CurrentCulture.Name);

			ClrConversions(Line);
			ClrMath(Line);
			EngineMath(Line);
			Lua(Line);
			Crypto(Line);

			return output.ToString();
		}

		/// <summary>Inputs where the raw (int)/(short)/(byte) cast of this runtime differs from ToInt32Saturated.</summary>
		public static List<string> RawCastMismatches()
		{
			var mismatches = new List<string>();
			foreach (var d in ConversionInputs)
			{
				var s = LuaValueExts.ToInt32Saturated(d);
				if (RawInt(d) != s || RawShort(d) != (short)s || RawByte(d) != (byte)s)
					mismatches.Add($"{F(d)}: raw {RawInt(d)}/{RawShort(d)}/{RawByte(d)}, helper {s}/{(short)s}/{(byte)s}");
			}

			return mismatches;
		}

		static void ClrConversions(Action<string, string, string> line)
		{
			foreach (var d in ConversionInputs)
			{
				var s = LuaValueExts.ToInt32Saturated(d);
				line("clr.convert", F(d), Invariant($"int {s} short {(short)s} byte {(byte)s}"));

				// What this runtime does with plain casts: CoreCLR is expected to equal the helper, Mono is not.
				line("info.rawcast", F(d), Invariant($"int {RawInt(d)} short {RawShort(d)} byte {RawByte(d)} long {RawLong(d)} uint {RawUInt(d)} float->int {RawIntFromFloat(d)}"));

				line("clr.checked", F(d), Try(() => checked((int)d).ToString(CultureInfo.InvariantCulture)) + " " + Try(() => checked((byte)d).ToString(CultureInfo.InvariantCulture)));
				line("clr.convertclass", F(d), Try(() => Convert.ToInt32(d).ToString(CultureInfo.InvariantCulture)) + " " + Try(() => Convert.ToByte(d).ToString(CultureInfo.InvariantCulture)));
				line("clr.round", F(d), F(Math.Round(d)) + " " + F(Math.Round(d, MidpointRounding.AwayFromZero)) + " " + F(Math.Floor(d)) + " " + F(Math.Ceiling(d)) + " " + F(Math.Truncate(d)));
				line("clr.tofloat", F(d), F((float)d));
				line("clr.format", F(d), d.ToString(CultureInfo.InvariantCulture) + " " + d.ToString("R", CultureInfo.InvariantCulture) + " " + d.ToString("F2", CultureInfo.InvariantCulture) + " " + ((float)d).ToString(CultureInfo.InvariantCulture));
			}
		}

		static void ClrMath(Action<string, string, string> line)
		{
			line("clr.int", "MinValue / -1", Try(() => Div(int.MinValue, -1).ToString(CultureInfo.InvariantCulture)));
			line("clr.int", "MinValue % -1", Try(() => Rem(int.MinValue, -1).ToString(CultureInfo.InvariantCulture)));
			line("clr.int", "long MinValue / -1", Try(() => DivL(long.MinValue, -1).ToString(CultureInfo.InvariantCulture)));
			line("clr.int", "1 / 0", Try(() => Div(1, 0).ToString(CultureInfo.InvariantCulture)));
			line("clr.int", "MaxValue + 1", unchecked(Add(int.MaxValue, 1)).ToString(CultureInfo.InvariantCulture));
			line("clr.int", "checked MaxValue + 1", Try(() => checked(Add(int.MaxValue, 0) + Add(1, 0)).ToString(CultureInfo.InvariantCulture)));
			line("clr.int", "-7 / 2, -7 % 2, -7 >> 1", Invariant($"{Div(-7, 2)} {Rem(-7, 2)} {Shr(-7, 1)}"));
			line("clr.int", "1 << 33, 1L << 65", Invariant($"{Shl(1, 33)} {ShlL(1, 65)}"));
			line("clr.int", "Math.BigMul", Math.BigMul(int.MaxValue, int.MaxValue).ToString(CultureInfo.InvariantCulture));

			line("clr.float", "max/min NaN", F(Math.Max(Value(double.NaN), 1)) + " " + F(Math.Min(1, Value(double.NaN))));
			line("clr.float", "max/min -0", F(Math.Max(Value(-0.0), 0.0)) + " " + F(Math.Min(Value(0.0), -0.0)));
			line("clr.float", "IEEERemainder", F(Math.IEEERemainder(Value(5.5), 2)) + " " + F(Math.IEEERemainder(Value(-7), 3)));
			line("clr.float", "% doubles", F(Value(5.5) % 2) + " " + F(Value(-5.5) % 2) + " " + F(Value(5) % 0) + " " + F(Value(1e300) % 7));
			line("clr.float", "FusedMultiplyAdd", F(Math.FusedMultiplyAdd(Value(0.1), 10, -1)));
			line("clr.float", "ScaleB/ILogB", F(Math.ScaleB(Value(1.5), -1074)) + " " + Math.ILogB(Value(1e-310)).ToString(CultureInfo.InvariantCulture));
			line("clr.float", "Round digits", F(Math.Round(Value(1.005), 2)) + " " + F(Math.Round(Value(2.675), 2)) + " " + F(Math.Round(Value(-0.5))));

			// float32 arithmetic: a runtime that keeps float intermediates in double precision differs here.
			var a = ValueF(16777216f);
			var b = ValueF(1.0000001f);
			var c = ValueF(0.1f);
			line("clr.float32", "a + 1 - a", F(a + 1f - a));
			line("clr.float32", "b * b * b * b", F(b * b * b * b));
			line("clr.float32", "c * 3 - 0.3", F(c * 3f - 0.3f));
			line("clr.float32", "(double)(c * c)", F((double)(c * c)));
			line("clr.float32", "sum 0.1 x 1000", F(SumF(c, 1000)));
			line("clr.float32", "1/3", F(ValueF(1f) / ValueF(3f)));
			line("clr.float32", "float->int 16777217.0", RawIntFromFloat(16777217.0).ToString(CultureInfo.InvariantCulture));

			line("clr.parse", "double.Parse", F(double.Parse("0.1", CultureInfo.InvariantCulture)) + " " + F(double.Parse("1e400", CultureInfo.InvariantCulture)) + " " + F(double.Parse("4.9e-324", CultureInfo.InvariantCulture)) + " " + F(double.Parse("2.4703282292062328e-324", CultureInfo.InvariantCulture)));
			line("clr.parse", "float.Parse", F(float.Parse("0.1", CultureInfo.InvariantCulture)) + " " + F(float.Parse("16777217", CultureInfo.InvariantCulture)) + " " + F(float.Parse("3.4028236e38", CultureInfo.InvariantCulture)));

			// Collections: enumeration order after adds and removes, unstable sort of equal keys.
			var dict = new Dictionary<int, int>();
			for (var i = 0; i < 50; i++)
				dict[i * 37 % 101] = i;
			for (var i = 0; i < 50; i += 3)
				dict.Remove(i * 37 % 101);
			for (var i = 200; i < 210; i++)
				dict[i] = i;
			line("clr.collections", "Dictionary<int> order", string.Join(",", dict.Keys));

			var set = new HashSet<string>();
			for (var i = 0; i < 40; i++)
				set.Add("s" + i * 13);
			set.RemoveWhere(s => s.Length == 3);
			for (var i = 0; i < 5; i++)
				set.Add("t" + i);
			line("clr.collections", "HashSet<string> order", string.Join(",", set));

			var items = Enumerable.Range(0, 40).Select(i => (Key: i % 4, Id: i)).ToArray();
			Array.Sort(items, (x, y) => x.Key.CompareTo(y.Key));
			line("clr.collections", "Array.Sort unstable", string.Join(",", items.Select(x => x.Id)));

			var random = new Random(12345);
			line("clr.random", "Random(12345)", string.Join(",", Enumerable.Range(0, 8).Select(_ => random.Next(1000))) + " " + F(random.NextDouble()));
		}

		static void Crypto(Action<string, string, string> line)
		{
			var key = ManagedRsa.FromXmlString(RsaTestKey);
			line("crypto.rsa", "fingerprint", CryptoUtil.PublicKeyFingerprint(key));
			foreach (var (name, text, expected) in RsaVectors)
			{
				var data = Encoding.UTF8.GetBytes(text);
				var signature = Convert.ToBase64String(ManagedRsa.SignSha1(key, data));
				line("crypto.rsa", name + " signature", signature);
				line("crypto.rsa", name + " known answer", signature == expected ? "same" : "DIFFERENT");
				line("crypto.rsa", name + " verifies", ManagedRsa.VerifySha1(key, data, Convert.FromBase64String(expected)) ? "yes" : "no");
			}

			// How long a new player key takes on this runtime (random, so info only).
			const int Keys = 4;
			var stopwatch = Stopwatch.StartNew();
			for (var i = 0; i < Keys; i++)
				ManagedRsa.GenerateKey(2048);

			line("info.crypto", "rsa key generation ms", Invariant($"{stopwatch.ElapsedMilliseconds / Keys}"));
		}

		static void EngineMath(Action<string, string, string> line)
		{
			var twister = new MersenneTwister(4242);
			line("engine.random", "MersenneTwister(4242)", string.Join(",", Enumerable.Range(0, 8).Select(_ => twister.Next(1000))) + " " + F(twister.NextFloat()));

			var sqrt = new StringBuilder();
			foreach (var n in new[] { 0, 1, 2, 3, 99, 100, 101, 65535, 1 << 30, int.MaxValue })
				sqrt.Append(Exts.ISqrt(n)).Append(',').Append(Exts.ISqrt(n, Exts.ISqrtRoundMode.Nearest)).Append(',').Append(Exts.ISqrt(n, Exts.ISqrtRoundMode.Ceiling)).Append(' ');
			line("engine.math", "ISqrt", sqrt.ToString().TrimEnd());

			var angles = new StringBuilder();
			for (var i = -1100; i <= 1100; i += 73)
				angles.Append(Invariant($"{new WAngle(i).Sin()}/{new WAngle(i).Cos()}/{new WAngle(i).Tan()} "));
			line("engine.math", "WAngle sin/cos/tan", angles.ToString().TrimEnd());

			var arcTan = new StringBuilder();
			for (var y = -3; y <= 3; y++)
				for (var x = -3; x <= 3; x++)
					arcTan.Append(WAngle.ArcTan(y * 1000 + 7, x * 1000 - 3).Angle).Append(' ');
			line("engine.math", "WAngle.ArcTan", arcTan.ToString().TrimEnd());
			line("engine.math", "WVec.Length", Invariant($"{new WVec(3000, 4000, 12000).Length} {new WVec(46340, 46340, 0).Length} {new WVec(-1, -1, -1).Length}"));
		}

		static void Lua(Action<string, string, string> line)
		{
			using var runtime = new MemoryConstrainedLuaRuntime();
			runtime.DoString(LuaHarness).Dispose();
			using var evaluate = (LuaFunction)runtime.Globals["__conformance"];

			foreach (var snippet in LuaSnippets)
				using (var results = evaluate.Call(snippet))
					line("lua", snippet, results[0].ToString());

			foreach (var snippet in LuaLibmSnippets)
				using (var results = evaluate.Call(snippet))
					line("lua.libm", snippet, results[0].ToString());

			// Lua numbers converted to CLR parameters by OpenRA's script bindings.
			foreach (var expression in ScriptNumberExpressions)
			{
				using var results = runtime.DoString("return " + expression);
				var value = results[0];
				var sb = new StringBuilder();
				foreach (var type in new[] { typeof(int), typeof(short), typeof(byte), typeof(double), typeof(int?), typeof(object) })
				{
					var ok = value.TryGetClrValue(type, out var converted);
					sb.Append(type.Name).Append(':').Append(ok ? (converted is double d ? F(d) : Convert.ToString(converted, CultureInfo.InvariantCulture)) : "fail").Append(' ');
				}

				line("script.convert", expression, sb.ToString().TrimEnd());
			}

			// libm sweep through Lua (desktop: the C library of the platform; browser: musl).
			var inputs = SweepInputs();
			using (var sweep = (LuaFunction)runtime.Globals["__sweep"])
			{
				foreach (var fn in SweepFunctions)
				{
					var values = new StringBuilder();
					foreach (var x in inputs)
						using (var r = sweep.Call(fn, x))
							values.Append(r[0].ToString()).Append(' ');

					line("lua.libm", fn, values.ToString().TrimEnd());
				}

				foreach (var (name, y) in new[] { ("atan2", 0.75), ("pow", 1.7), ("fmod", 2.3) })
				{
					var values = new StringBuilder();
					foreach (var x in inputs)
						using (var r = sweep.Call(name, x, y))
							values.Append(r[0].ToString()).Append(' ');

					line("lua.libm", name, values.ToString().TrimEnd());
				}
			}

			// The same sweep through the CLR's Math class (CoreCLR: the platform C library; Mono: musl).
			var clr = new (string Name, Func<double, double> Fn)[]
			{
				("sin", Math.Sin), ("cos", Math.Cos), ("tan", Math.Tan), ("asin", Math.Asin), ("acos", Math.Acos),
				("atan", Math.Atan), ("exp", Math.Exp), ("log", Math.Log), ("log10", Math.Log10), ("sqrt", Math.Sqrt),
				("sinh", Math.Sinh), ("cosh", Math.Cosh), ("tanh", Math.Tanh), ("atan2", x => Math.Atan2(x, 0.75)),
				("pow", x => Math.Pow(x, 1.7)), ("fmod", x => Math.IEEERemainder(x, 2.3)),
				("sinf", x => MathF.Sin((float)x)), ("expf", x => MathF.Exp((float)x)), ("powf", x => MathF.Pow((float)x, 1.7f)),
			};
			foreach (var (name, fn) in clr)
				line("clr.libm", name, string.Join(" ", inputs.Select(x => Hex(fn(Value(x))))));
		}

		// __conformance(src) evaluates "return <src>" and serializes every result with its exact bits.
		// __sweep(fn, x, y) returns the %.17g text of math[fn](x, y), which identifies the double exactly.
		const string LuaHarness = @"
			local function ser(v)
				local t = type(v)
				if t == 'number' then
					return tostring(v) .. ' [' .. string.format('%.17g', v) .. ']'
				elseif t == 'string' then
					return string.format('%q', v)
				elseif t == 'table' then
					return 'table'
				end
				return tostring(v)
			end

			local function pack(...)
				return select('#', ...), { ... }
			end

			function __conformance(src)
				local f, err = loadstring('return ' .. src, 'snippet')
				if not f then
					return 'compile error: ' .. tostring(err)
				end
				local n, r = pack(pcall(f))
				if not r[1] then
					return 'error: ' .. tostring(r[2])
				end
				local out = {}
				for i = 2, n do
					out[#out + 1] = ser(r[i])
				end
				return '(' .. (n - 1) .. ') ' .. table.concat(out, ' | ')
			end

			function __sweep(fn, x, y)
				return string.format('%.17g', math[fn](x, y))
			end
		";

		static string F(double d) => double.IsNaN(d) ? "NaN" : d.ToString("R", CultureInfo.InvariantCulture) + "/" + BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
		static string F(float f) => float.IsNaN(f) ? "NaN" : f.ToString("R", CultureInfo.InvariantCulture) + "/" + BitConverter.SingleToInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);
		static string Hex(double d) => double.IsNaN(d) ? "NaN" : BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);
		static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

		static string Try(Func<string> f)
		{
			try
			{
				return f();
			}
			catch (Exception e)
			{
				return e.GetType().Name;
			}
		}

		// Out-of-line helpers so that no conversion or operation is folded at compile time.
		[MethodImpl(MethodImplOptions.NoInlining)]
		static double Value(double d) => d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static float ValueF(float f) => f;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int RawInt(double d) => (int)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static short RawShort(double d) => (short)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static byte RawByte(double d) => (byte)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static long RawLong(double d) => (long)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static uint RawUInt(double d) => (uint)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int RawIntFromFloat(double d) => (int)(float)d;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Div(int a, int b) => a / b;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Rem(int a, int b) => a % b;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static long DivL(long a, long b) => a / b;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Add(int a, int b) => unchecked(a + b);
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Shr(int a, int b) => a >> b;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static int Shl(int a, int b) => a << b;
		[MethodImpl(MethodImplOptions.NoInlining)]
		static long ShlL(long a, int b) => a << b;

		[MethodImpl(MethodImplOptions.NoInlining)]
		static float SumF(float step, int count)
		{
			var sum = 0f;
			for (var i = 0; i < count; i++)
				sum += step;
			return sum;
		}
	}
}
