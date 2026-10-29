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
using OpenRA.Network;
using OpenRA.Widgets;

namespace OpenRA
{
	// Entry points for the browser (WebAssembly) host.
	// The browser cannot block inside Loop(): the page has to return to the event loop after every
	// frame so that it can present, deliver input and run timers. The host instead calls BrowserFrame()
	// once per requestAnimationFrame. This runs the same logic ticks as Loop() (including catching up
	// after a slow frame, which matters in lockstep multiplayer) and then renders at most once.
	// Nothing in here touches the simulation: it only decides when LogicTick() and RenderTick() run.
	public static partial class Game
	{
		// Same values as Loop().
		const int BrowserMaxLogicTicksBehind = 250;

		// Stop running logic ticks in a single frame after this long, so that the page stays responsive.
		// Any remaining ticks run on the next frame (Loop() behaves the same way when it falls behind).
		const int BrowserMaxFrameLogicMilliseconds = 100;

		static long browserNextLogic;
		static long browserNextRender;
		static bool browserInitialized;

		public static int BrowserLogicTicks { get; private set; }
		public static int BrowserRenderFrames { get; private set; }
		public static int BrowserLocalTick => OrderManager?.LocalFrameNumber ?? 0;
		public static string BrowserWorldType => OrderManager?.World?.Type.ToString() ?? "none";
		public static int BrowserWorldTick => OrderManager?.World?.WorldTick ?? 0;

		/// <summary>For the host's diagnostics and test hooks.</summary>
		public static OrderManager BrowserOrderManager => OrderManager;

		/// <summary>For the host's diagnostics and test hooks.</summary>
		public static Graphics.WorldRenderer BrowserGetWorldRenderer() => worldRenderer;

		public static void BrowserInitialize(string[] args)
		{
			if (!OperatingSystem.IsBrowser())
				throw new PlatformNotSupportedException("BrowserInitialize is only supported in the browser.");

			Initialize(new Arguments(args));

			// Proactively collect memory during loading to reduce peak memory.
			GC.Collect();

			if (Settings.Graphics.MaxFramerate < 1)
			{
				Settings.Graphics.MaxFramerate = new GraphicSettings().MaxFramerate;
				Settings.Graphics.CapFramerate = false;
			}

			browserNextLogic = browserNextRender = RunTime;
			browserInitialized = true;
		}

		/// <summary>What a browser frame does besides running the logic ticks that are due.</summary>
		public enum BrowserFrameKind
		{
			/// <summary>An animation frame: render (if a render is due), pumping input into the game.</summary>
			Render,

			/// <summary>
			/// A visible page between animation frames, driven by a steady clock: logic ticks only.
			/// Input stays queued for the next render frame.
			/// </summary>
			Logic,

			/// <summary>A hidden page, driven by a timer: logic ticks only, input is discarded.</summary>
			Hidden,
		}

		static bool browserTickedSinceRender;

		/// <summary>
		/// Runs one browser frame. Returns false once the game has exited.
		/// With <paramref name="render"/> false (a hidden page, driven by a timer instead of animation
		/// frames) only the due logic ticks run: the simulation, the network and the lockstep order
		/// exchange keep going, so other players are not stalled, but nothing is drawn.
		/// </summary>
		public static bool BrowserFrame(bool render = true)
		{
			return BrowserFrame(render ? BrowserFrameKind.Render : BrowserFrameKind.Hidden);
		}

		/// <summary>
		/// Runs the logic ticks that are due (the same ticks, in the same order, as Loop() runs them),
		/// then, depending on <paramref name="kind"/>, renders. Returns false once the game has exited.
		/// Logic frames let the page run logic ticks, and with them the network (orders, acks, pings),
		/// on time while a slow GPU makes animation frames rare: a tick is due every 40 ms at normal
		/// speed, but an animation frame only comes after the previous render.
		/// Only the wall-clock moments at which LogicTick() runs change, which they also do on desktop
		/// with its load; what the simulation receives is decided by the order stream alone.
		/// </summary>
		public static bool BrowserFrame(BrowserFrameKind kind)
		{
			if (!browserInitialized || state != RunStatus.Running)
				return false;

			var frameStart = RunTime;

			var logicInterval = Ui.Timestep;
			var logicWorld = worldRenderer?.World;

			// ReplayTimestep = 0 means the replay is paused: we need to keep logicInterval as UI.Timestep to avoid breakage
			if (logicWorld != null && (!logicWorld.IsReplay || logicWorld.ReplayTimestep != 0))
				logicInterval = logicWorld == OrderManager.World ? OrderManager.SuggestedTimestep : logicWorld.Timestep;

			var renderInterval = logicInterval;
			if (!Settings.Graphics.CapFramerateToGameFps)
			{
				var maxFramerate = Settings.Graphics.CapFramerate ? Settings.Graphics.MaxFramerate.Clamp(1, 1000) : 1000;
				renderInterval = 1000 / maxFramerate;
			}

			// Tick as fast as possible while restoring game saves, capping rendering at 5 FPS
			if (OrderManager.World != null && OrderManager.World.IsLoadingGameSave)
			{
				logicInterval = 1;
				renderInterval = 200;
			}

			// If the logic has fallen behind too much, skip it and catch up
			if (frameStart - browserNextLogic > BrowserMaxLogicTicksBehind)
				browserNextLogic = frameStart;

			// Run every logic tick that is due, not just one: a browser frame can take longer than a tick.
			var ticked = false;
			while (frameStart >= browserNextLogic && state == RunStatus.Running)
			{
				browserNextLogic += logicInterval;
				LogicTick();
				BrowserLogicTicks++;
				ticked = true;

				if (RunTime - frameStart > BrowserMaxFrameLogicMilliseconds)
					break;
			}

			if (state != RunStatus.Running)
				return false;

			if (ticked)
				browserTickedSinceRender = true;

			if (kind == BrowserFrameKind.Logic)
				return true;

			// Render once per browser frame at most: anything drawn twice before the page returns
			// to the event loop is never shown. A tick since the last render (in this frame or in a
			// logic frame) makes a render due, as in Loop().
			if (kind == BrowserFrameKind.Hidden || Renderer.WindowIsSuspended)
				Renderer.Window.PumpInput(new NullInputHandler());
			else if (browserTickedSinceRender || frameStart >= browserNextRender)
			{
				browserNextRender = frameStart + renderInterval;
				browserTickedSinceRender = false;
				RenderTick();
				BrowserRenderFrames++;
			}

			return state == RunStatus.Running;
		}
	}
}
