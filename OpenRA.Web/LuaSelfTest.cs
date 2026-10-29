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
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Eluant;

namespace OpenRA.Web
{
	[SupportedOSPlatform("browser")]
	public static partial class LuaSelfTest
	{
		/// <summary>Exercises Lua -> CLR -> Lua -> CLR re-entrancy through Eluant and reports whether managed locals survive.</summary>
		[JSExport]
		public static string Run(int depth)
		{
			var log = new List<string>();
			using var runtime = new MemoryConstrainedLuaRuntime();

			Func<LuaFunction, int, int> outer = null;
			outer = (fn, level) =>
			{
				var sentinel = 0x5EED0000 + level;
				var marker = "level" + level;
				var list = new List<int> { level, level + 1 };
				using (var result = fn.Call(level))
				{
				}

				// Churn the nursery and force collections while outer frames are suspended in native code.
				var garbage = new List<object>();
				for (var i = 0; i < 20000; i++)
					garbage.Add(new int[8]);
				GC.Collect();

				if (sentinel != 0x5EED0000 + level || marker != "level" + level || list.Count != 2 || list[0] != level)
					log.Add($"CORRUPTED at level {level}: sentinel={sentinel:X} marker={marker} count={list.Count}");
				else
					log.Add($"ok level {level}");

				return level;
			};

			using (var f = runtime.CreateFunctionFromDelegate(outer))
				runtime.Globals["outer"] = f;

			runtime.Globals["maxdepth"] = depth;
			using (var r = runtime.DoString("local function inner(l) if l < maxdepth then return outer(inner, l + 1) end return l end return outer(inner, 0)"))
				log.Add("lua returned " + r[0]);

			return string.Join("\n", log);
		}
	}
}
