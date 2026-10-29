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

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using OpenRA.Web.Conformance;

namespace OpenRA.Web
{
	/// <summary>Runs the numeric conformance corpus in the browser (see tools/conformance.sh).</summary>
	[SupportedOSPlatform("browser")]
	public static partial class ConformanceRunner
	{
		[JSExport]
		public static string Run() => ConformanceCorpus.Run();

		/// <summary>Inputs where this runtime's plain casts differ from CoreCLR's (expected in the browser).</summary>
		[JSExport]
		public static string RawCastMismatches() => string.Join("\n", ConformanceCorpus.RawCastMismatches());
	}
}
