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
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;

namespace OpenRA.Web
{
	[SupportedOSPlatform("browser")]
	public static partial class WebHost
	{
		static readonly Stopwatch FrameTimer = new();
		static double frameMillisecondsTotal;
		static double frameMillisecondsWorst;
		static int framesMeasured;
		static double logicFrameMillisecondsTotal;
		static double logicFrameMillisecondsWorst;
		static int logicFramesMeasured;

		// The runtime starts without running Main: the page drives the engine through the exports below.
		public static void Main() { }

		/// <summary>Initializes the engine and loads the mod and its shellmap. Blocks until done.</summary>
		[JSExport]
		public static void Boot(string[] args)
		{
			Console.WriteLine("OpenRA.Web: booting with " + string.Join(" ", args));
			BootChecks.Run();

			try
			{
				Network.NetworkConnection.StreamTransportFactory = WebSocketTransport.Open;
				Game.BrowserInitialize(args);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("OpenRA.Web: boot failed: " + e);
				throw;
			}
		}

		/// <summary>
		/// Runs one frame. Returns false once the game has exited.
		/// The page passes render = false while it is hidden (logic and network only).
		/// </summary>
		[JSExport]
		public static bool Frame(bool render)
		{
			FrameTimer.Restart();
			bool running;
			try
			{
				running = Game.BrowserFrame(render);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("OpenRA.Web: frame failed: " + e);
				throw;
			}

			var elapsed = FrameTimer.Elapsed.TotalMilliseconds;
			frameMillisecondsTotal += elapsed;
			frameMillisecondsWorst = Math.Max(frameMillisecondsWorst, elapsed);
			framesMeasured++;
			return running;
		}

		/// <summary>
		/// Runs the logic ticks that are due and nothing else (no render, input left queued; only a tick
		/// that starts a game shows the mod's load screen, as on desktop). The page calls this from a
		/// steady clock between animation frames while it is visible, so that ticks, and with them
		/// orders, acks and ping replies, are not held back by a slow render.
		/// Returns false once the game has exited.
		/// </summary>
		[JSExport]
		public static bool LogicFrame()
		{
			FrameTimer.Restart();
			bool running;
			try
			{
				running = Game.BrowserFrame(Game.BrowserFrameKind.Logic);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("OpenRA.Web: logic frame failed: " + e);
				throw;
			}

			var elapsed = FrameTimer.Elapsed.TotalMilliseconds;
			logicFrameMillisecondsTotal += elapsed;
			logicFrameMillisecondsWorst = Math.Max(logicFrameMillisecondsWorst, elapsed);
			logicFramesMeasured++;
			return running;
		}

		/// <summary>Debug hook: the managed stack of the main thread (also callable from a paused debugger).</summary>
		[JSExport]
		public static string StackTrace() => Environment.StackTrace;

		/// <summary>Test hook: the screen rectangle "x,y,w,h" of a visible widget, or "" if there is none.</summary>
		[JSExport]
		public static string WidgetBounds(string id)
		{
			var widget = Widgets.Ui.Root.GetOrNull(id);
			if (widget == null || !widget.IsVisible())
				return "";

			var r = widget.RenderBounds;
			return string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.Width},{r.Height}");
		}

		/// <summary>
		/// The type of the world being shown ("Shellmap", "Regular", "Editor", or "none"), for the page.
		/// Cheap, unlike <see cref="Diagnostics"/>, whose managed heap size walks the whole heap.
		/// </summary>
		[JSExport]
		public static string WorldType() => Game.BrowserWorldType;

		/// <summary>Small JSON blob for automated tests and the on-page status line.</summary>
		[JSExport]
		public static string Diagnostics(bool resetFrameStats)
		{
			var sb = new StringBuilder();
			sb.Append('{');
			sb.Append(CultureInfo.InvariantCulture, $"\"logicTicks\":{Game.BrowserLogicTicks},");
			sb.Append(CultureInfo.InvariantCulture, $"\"renderFrames\":{Game.BrowserRenderFrames},");
			sb.Append(CultureInfo.InvariantCulture, $"\"localTick\":{Game.BrowserLocalTick},");
			sb.Append(CultureInfo.InvariantCulture, $"\"runTime\":{Game.RunTime},");
			sb.Append(CultureInfo.InvariantCulture, $"\"worldType\":\"{Game.BrowserWorldType}\",");
			sb.Append(CultureInfo.InvariantCulture, $"\"worldFrame\":{Game.BrowserWorldTick},");
			sb.Append(CultureInfo.InvariantCulture, $"\"managedHeap\":{GC.GetTotalMemory(false)},");
			sb.Append(CultureInfo.InvariantCulture, $"\"framesMeasured\":{framesMeasured},");
			sb.Append(CultureInfo.InvariantCulture, $"\"frameMsAvg\":{(framesMeasured > 0 ? frameMillisecondsTotal / framesMeasured : 0):F3},");
			sb.Append(CultureInfo.InvariantCulture, $"\"frameMsWorst\":{frameMillisecondsWorst:F3},");
			sb.Append(CultureInfo.InvariantCulture, $"\"logicFramesMeasured\":{logicFramesMeasured},");
			sb.Append(CultureInfo.InvariantCulture, $"\"logicFrameMsAvg\":{(logicFramesMeasured > 0 ? logicFrameMillisecondsTotal / logicFramesMeasured : 0):F3},");
			sb.Append(CultureInfo.InvariantCulture, $"\"logicFrameMsWorst\":{logicFrameMillisecondsWorst:F3},");
			sb.Append(CultureInfo.InvariantCulture, $"\"logicFramesOverBudget\":{Game.BrowserLogicFramesOverBudget},");
			sb.Append(CultureInfo.InvariantCulture, $"\"logicBudgetResets\":{Game.BrowserLogicBudgetResets}");
			sb.Append('}');

			if (resetFrameStats)
			{
				frameMillisecondsTotal = frameMillisecondsWorst = 0;
				framesMeasured = 0;
				logicFrameMillisecondsTotal = logicFrameMillisecondsWorst = 0;
				logicFramesMeasured = 0;
			}

			return sb.ToString();
		}
	}
}
