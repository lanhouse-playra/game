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
using System.Reflection;
using System.Runtime.Versioning;
using Eluant;
using Eluant.ObjectBinding;
using OpenRA.Scripting;

namespace OpenRA.Web
{
	/// <summary>
	/// Checks run before the engine boots. They refuse to start a build whose runtime is not the one
	/// it was validated with, that runs the Mono interpreter without the workaround for its memory
	/// corruption bug, or that fails the interpreter self-test. A silent corruption would desync a
	/// multiplayer game, which is worse than a page that does not start.
	/// </summary>
	[SupportedOSPlatform("browser")]
	static class BootChecks
	{
		// The Mono interpreter of .NET 10.0.14 corrupts memory when a method tiers up while frames of the
		// same method are suspended in native code (Lua -> CLR -> Lua re-entrancy, as in the shellmap's
		// WorldLoaded). "-tiering" compiles every method optimized on first call, so nothing tiers up.
		// main.js sets it; only dev builds can override it (?interp=).
		const string InterpreterWorkaround = "-tiering";

		public static void Run()
		{
			var runtime = Environment.Version.ToString();
			if (runtime != PlayraBuildInfo.RuntimeVersion)
				Fail($"the .NET runtime is {runtime}, but this build was validated with {PlayraBuildInfo.RuntimeVersion}");

			var options = Environment.GetEnvironmentVariable("MONO_INTERPRETER_OPTIONS") ?? "";
			if (!options.Contains(InterpreterWorkaround, StringComparison.Ordinal))
			{
				var message = $"MONO_INTERPRETER_OPTIONS is '{options}', without '{InterpreterWorkaround}' (the interpreter memory corruption workaround)";
				if (!PlayraBuildInfo.Dev)
					Fail(message);

				Console.Error.WriteLine("OpenRA.Web: WARNING: " + message + "; allowed in a dev build only");
			}

			var error = InterpreterSelfTest() ?? BindingSelfTest();
			if (error != null)
				Fail("the interpreter self-test failed: " + error);

			Console.WriteLine($"OpenRA.Web: boot checks passed (runtime {runtime}, interpreter options '{options}', {PlayraBuildInfo.Version} {PlayraBuildInfo.Commit})");
		}

		static void Fail(string message)
		{
			throw new InvalidOperationException("OpenRA.Web refuses to start: " + message);
		}

		/// <summary>
		/// Stresses the pattern behind the interpreter bug: a CLR callback, called from Lua, that calls
		/// back into Lua, which calls the same callback again, enough times for its methods (and Eluant's
		/// callback wrapper) to reach the interpreter's tier-up threshold while outer frames of them are
		/// suspended in native code. Locals that live across the native calls and the results are checked
		/// against a plain C# computation. Returns null if everything matches.
		/// </summary>
		static string InterpreterSelfTest()
		{
			const int Depth = 6;
			const int Rounds = 400;

			string error = null;
			using var runtime = new MemoryConstrainedLuaRuntime();
			LuaFunction reenter = null;

			Func<int, int, double> callback = (depth, seed) =>
			{
				var a = seed * 7919L;
				var b = seed + 0.5;
				var s = "s" + seed;
				var pair = (Seed: seed, Depth: depth);

				var acc = 0.0;
				if (depth > 0)
					using (var r = reenter.Call(depth - 1, seed + 1))
						acc += r[0].ToNumber().Value;

				for (var i = 0; i < 40; i++)
					acc += (i ^ seed) & 7;

				if (a != seed * 7919L || b != seed + 0.5 || s != "s" + seed || pair.Seed != seed || pair.Depth != depth)
					error ??= $"locals changed across a Lua call at depth {depth}, seed {seed}";

				return acc + a % 13;
			};

			using (var f = runtime.CreateFunctionFromDelegate(callback))
				runtime.Globals["cb"] = f;

			using (var r = runtime.DoString("return function(d, s) return cb(d, s) end"))
				reenter = (LuaFunction)r[0].CopyReference();

			try
			{
				for (var round = 0; round < Rounds && error == null; round++)
				{
					using var r = reenter.Call(Depth, round);
					var expected = Expected(Depth, round);
					var actual = r[0].ToNumber().Value;
					if (actual != expected)
						error = $"round {round}: got {actual}, expected {expected}";
				}
			}
			finally
			{
				reenter.Dispose();
			}

			return error;
		}

		/// <summary>
		/// The same re-entrancy through the path OpenRA's script bindings take (ScriptObjectWrapper and
		/// ScriptMemberWrapper): a CLR object indexed from Lua returns a fresh vararg function, which converts
		/// its arguments with TryGetClrValue and calls the method through reflection; the method calls back
		/// into Lua, which indexes the object again.
		/// </summary>
		static string BindingSelfTest()
		{
			const int Depth = 6;
			const int Rounds = 300;

			using var runtime = new MemoryConstrainedLuaRuntime();
			var target = new SelfTestBinding();
			using (var obj = new LuaCustomClrObject(target))
				runtime.Globals["obj"] = obj;

			using (var r = runtime.DoString(@"
				local function cb(d, s) return obj.Step(cb, d, s) end
				return function(round) return obj.Step(cb, " + Depth + @", round) end"))
			using (var run = (LuaFunction)r[0].CopyReference())
			{
				for (var round = 0; round < Rounds && target.Error == null; round++)
				{
					using var result = run.Call(round);
					var actual = result[0].ToNumber().Value;
					var expected = Expected(Depth, round);
					if (actual != expected)
						return $"binding round {round}: got {actual}, expected {expected}";
				}
			}

			return target.Error;
		}

		sealed class SelfTestBinding : ILuaTableBinding
		{
			static readonly MethodInfo StepMethod = typeof(SelfTestBinding).GetMethod(nameof(Step));
			public string Error;

			public LuaValue this[LuaRuntime runtime, LuaValue key]
			{
				get => runtime.CreateFunctionFromDelegate((Func<LuaVararg, LuaValue>)Invoke);
				set => throw new InvalidOperationException("read only");
			}

			LuaValue Invoke(LuaVararg args)
			{
				var parameters = StepMethod.GetParameters();
				var clrArgs = new object[parameters.Length];
				try
				{
					for (var i = 0; i < parameters.Length; i++)
						if (!args[i].TryGetClrValue(parameters[i].ParameterType, out clrArgs[i]))
							throw new InvalidOperationException($"argument {i} is not a {parameters[i].ParameterType.Name}");

					return (double)StepMethod.Invoke(this, clrArgs);
				}
				finally
				{
					foreach (var arg in args)
						arg.Dispose();
					args.Dispose();
				}
			}

			public double Step(LuaFunction callback, int depth, int seed)
			{
				var a = seed * 7919L;
				var s = "s" + seed;
				var acc = 0.0;
				if (depth > 0)
					using (var r = callback.Call(depth - 1, seed + 1))
						acc += r[0].ToNumber().Value;

				for (var i = 0; i < 40; i++)
					acc += (i ^ seed) & 7;

				if (a != seed * 7919L || s != "s" + seed || callback == null)
					Error ??= $"binding locals changed across a Lua call at depth {depth}, seed {seed}";

				return acc + a % 13;
			}
		}

		static double Expected(int depth, int seed)
		{
			var acc = depth > 0 ? Expected(depth - 1, seed + 1) : 0.0;
			for (var i = 0; i < 40; i++)
				acc += (i ^ seed) & 7;

			return acc + seed * 7919L % 13;
		}
	}
}
