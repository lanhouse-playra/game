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
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace OpenRA.Web
{
	/// <summary>
	/// Records the methods a session runs with Mono's AOT profiler, for OpenRA.Web/aot/playra.aotprofile: the AOT
	/// compiler of the other builds compiles the generic instances listed there (OpenRA.Web.csproj). Only part of dev
	/// builds made with PLAYRA_AOT_PROFILER=1 (build-web.sh), which link the profiler; the page starts it at load.
	///
	/// To record the profile: PLAYRA_DEV=1 PLAYRA_AOT=1 PLAYRA_AOT_PROFILER=1 ./build-web.sh, open the page, play what
	/// the profile should cover (the menus, a long replay of a match with many players and unit types, a match against
	/// bots as the lobby admin, so that the bots' AI runs too), then in the browser console call
	/// PlayRA.allExports.OpenRA.Web.AotProfileRecorder.Write() and copy the file out of the page's file system:
	/// PlayRA.runtime.Module.FS.readFile('/support/playra.aotprofile'). Record it again when the engine version changes;
	/// methods a profile names that no longer exist are ignored.
	/// </summary>
	[SupportedOSPlatform("browser")]
	public static partial class AotProfileRecorder
	{
		public const string ProfilePath = "/support/playra.aotprofile";

		/// <summary>
		/// Writes the profile to <see cref="ProfilePath"/>: the profiler writes its data when <see cref="WriteTrigger"/>
		/// is first compiled, and the reflection call makes the runtime look it up (a direct call from compiled code
		/// would not). Returns the size of the profile in bytes, or -1 if the profiler did not write one.
		/// </summary>
		[JSExport]
		public static int Write()
		{
			typeof(AotProfileRecorder).GetMethod(nameof(WriteTrigger))!.Invoke(null, null);
			return File.Exists(ProfilePath) ? (int)new FileInfo(ProfilePath).Length : -1;
		}

		[MethodImpl(MethodImplOptions.NoInlining)]
		public static void WriteTrigger() { }

		/// <summary>Called by the profiler with its data (it requires the signature void (ref byte, int, string)).</summary>
		public static void Save(ref byte data, int length, string extra)
		{
			File.WriteAllBytes(ProfilePath, MemoryMarshal.CreateReadOnlySpan(ref data, length).ToArray());
			Console.WriteLine($"OpenRA.Web: AOT profile written to {ProfilePath} ({length} bytes)");
		}
	}
}
