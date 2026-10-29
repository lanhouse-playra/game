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

namespace OpenRA.Web.Conformance
{
	static class Program
	{
		// Prints the corpus. Exits with 1 if this runtime's plain casts differ from LuaValueExts.ToInt32Saturated,
		// which must reproduce desktop CoreCLR exactly.
		static int Main()
		{
			Console.Out.Write(ConformanceCorpus.Run());

			var mismatches = ConformanceCorpus.RawCastMismatches();
			foreach (var m in mismatches)
				Console.Error.WriteLine("raw cast differs from ToInt32Saturated: " + m);

			Console.Error.WriteLine($"identity check: {mismatches.Count} raw cast mismatches");
			return mismatches.Count == 0 ? 0 : 1;
		}
	}
}
