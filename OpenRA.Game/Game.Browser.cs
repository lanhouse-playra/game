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
using System.Threading.Tasks;
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

		// A logic frame runs at most one tick, and logic frames stop once they have kept the page busy for
		// this long since the last Render (or Hidden) frame: when the logic falls behind (a fast-forwarded
		// replay, a slow machine catching up), ticks in back-to-back logic frames would otherwise occupy
		// the page and the animation frame would never run. The animation frames then catch up as before
		// (BrowserMaxFrameLogicMilliseconds, then render). A slow GPU alone (animation frames far apart,
		// the page idle in between) does not stop them: this counts busy time, not elapsed time.
		const double BrowserLogicFrameBudget = 50;

		// A page the browser reports visible can still get no animation frames at all (an offscreen
		// cross-origin iframe, a GPU process restart, a compositor that stopped vsync). Nothing would then
		// reset the budget above, the page would stop ticking and every other player in a match would
		// stall. Once no Render or Hidden frame has run for this long, logic frames get a fresh budget at
		// most once per BrowserLogicFrameBudgetWindow milliseconds: they keep the full tick rate but can
		// still occupy at most about half of the page's time, leaving room for animation frames that return.
		const int BrowserNoAnimationFrameMilliseconds = 500;
		const int BrowserLogicFrameBudgetWindow = 100;

		static long browserNextLogic;
		static long browserNextRender;
		static long browserLastBudgetReset;
		static long browserLastAnimationFrame;
		static bool browserInitialized;

		public static int BrowserLogicTicks { get; private set; }
		public static int BrowserRenderFrames { get; private set; }
		public static int BrowserLocalTick => OrderManager?.LocalFrameNumber ?? 0;
		public static string BrowserWorldType => OrderManager?.World?.Type.ToString() ?? "none";
		public static int BrowserWorldTick => OrderManager?.World?.WorldTick ?? 0;

		/// <summary>How many logic frames returned without ticking because their budget was spent (diagnostics).</summary>
		public static int BrowserLogicFramesOverBudget { get; private set; }

		/// <summary>How often logic frames got a fresh budget because no animation frame came (diagnostics).</summary>
		public static int BrowserLogicBudgetResets { get; private set; }

		/// <summary>For the host's diagnostics and test hooks.</summary>
		public static OrderManager BrowserOrderManager => OrderManager;

		/// <summary>For the host's diagnostics and test hooks.</summary>
		public static Graphics.WorldRenderer BrowserGetWorldRenderer() => worldRenderer;

		/// <summary>
		/// False in the browser build, which plays online only: it cannot listen on a socket (nor run a
		/// server thread), so it cannot run the local server behind skirmish, missions, saved games, the
		/// map editor's "Play Map" and Create Server. Menus disable those entries when this is false.
		/// </summary>
		public static bool CanHostLocalServer => !OperatingSystem.IsBrowser();

		// What the browser build tells the player about entries it cannot offer: the ones above, and settings
		// that need a desktop (a window, a disc drive, a router, Discord). The mod's fluent files must equal the
		// upstream release (servers admit a client by its mod data), so these cannot be fluent messages.
		public const string BrowserNotAvailable = "Not available in the browser version";
		public const string BrowserNoLocalServer = BrowserNotAvailable;
		public const string BrowserNoLocalServerHint = "Join a server from Multiplayer.";
		public const string BrowserNoHostingHint = "Join a server from the list, or with Direct IP.";

		static void CheckCanHostLocalServer()
		{
			if (!CanHostLocalServer)
				throw new PlatformNotSupportedException($"{BrowserNoLocalServer}: a local server needs to listen on a socket. {BrowserNoLocalServerHint}");
		}

		/// <summary>Set by the browser host: shows a message to the player over the game.</summary>
		public static Action<string> BrowserNotify;

		/// <summary>
		/// Set by the browser host: restarts the game by reloading the page, once the support directory
		/// (settings.yaml included) is saved in the browser's storage. "Restart Now" in the settings calls it.
		/// </summary>
		public static Action BrowserRestart;

		/// <summary>
		/// Set by the browser host: puts text on the system clipboard (the forum key, in the settings). True once
		/// it is there. Must be called from a click: browsers only allow it during a user gesture.
		/// </summary>
		public static Func<string, Task<bool>> BrowserCopyText;

		/// <summary>How many UI actions failed and sent the player back to the main menu (diagnostics).</summary>
		public static int BrowserUiActionsFailed { get; private set; }

		static bool browserUiRecoveryPending;

		// Browser only: a UI action (a click, a key) that throws would otherwise escape the frame and stop
		// the page. Input handlers run unsynced (Sync.RunUnsynced: they may not change the simulation), so
		// catching here cannot desync a match. The UI may be half way through a change, though, so the
		// player leaves whatever it was in (a match too: for the other players that is the same as the
		// page stopping) and returns to a freshly loaded main menu, with a notice that says what failed.
		sealed class BrowserInputHandler : IInputHandler
		{
			readonly DefaultInputHandler inner;

			public BrowserInputHandler(World world)
			{
				inner = new DefaultInputHandler(world);
			}

			public void ModifierKeys(Modifiers mods) => Run(() => inner.ModifierKeys(mods));
			public void OnKeyInput(KeyInput input) => Run(() => inner.OnKeyInput(input));
			public void OnMouseInput(MouseInput input) => Run(() => inner.OnMouseInput(input));
			public void OnTextInput(string text) => Run(() => inner.OnTextInput(text));

			static void Run(Action action)
			{
				// Input that arrives before the recovery has run goes nowhere: the UI may be broken.
				if (browserUiRecoveryPending)
					return;

				try
				{
					action();
				}
				catch (Exception e)
				{
					BrowserUiActionFailed(e);
				}
			}
		}

		// The page's on-screen keyboard support (OpenRA.Web/TextInput.cs). A phone shows its keyboard only when the
		// page focuses a text input of its own while it handles the tap, before the frame that would deliver the tap
		// to the game; and what the player types on it arrives as browser input events, not as SDL text or keys.

		/// <summary>
		/// Delivers the input SDL has queued now, exactly as the next render frame would, instead of leaving it
		/// to that frame. The page calls this while it handles a tap, to learn whether the tap focused a text field.
		/// Returns false (and leaves the input queued) when the game is not running a world.
		/// </summary>
		public static bool BrowserPumpInput()
		{
			if (!browserInitialized || state != RunStatus.Running || OrderManager?.World == null || Renderer.WindowIsSuspended)
				return false;

			Renderer.Window.PumpInput(new BrowserInputHandler(OrderManager.World));
			return true;
		}

		/// <summary>Delivers text typed into the page's keyboard input, after the input SDL has queued before it.</summary>
		public static void BrowserTextInput(string text)
		{
			if (BrowserPumpInput())
				new BrowserInputHandler(OrderManager.World).OnTextInput(text);
		}

		/// <summary>Delivers a key pressed in the page's keyboard input (down, then up), after the input SDL has queued before it.</summary>
		public static void BrowserKeyInput(Keycode key, Modifiers modifiers)
		{
			if (!BrowserPumpInput())
				return;

			var handler = new BrowserInputHandler(OrderManager.World);
			var input = new KeyInput { Event = KeyInputEvent.Down, Key = key, Modifiers = modifiers, MultiTapCount = 1, UnicodeChar = (char)key };
			handler.OnKeyInput(input);
			input.Event = KeyInputEvent.Up;
			handler.OnKeyInput(input);
		}

		static void BrowserUiActionFailed(Exception e)
		{
			BrowserUiActionsFailed++;
			Log.Write("debug", $"A UI action failed; returning to the main menu. {e}");
			Console.Error.WriteLine("OpenRA.Web: a UI action failed: " + e);

			browserUiRecoveryPending = true;
			var message = $"Something went wrong in the game ({e.GetBaseException().Message}), so it returned to the main menu.";

			// Not from inside the input pump (this runs in the middle of Renderer.EndFrame): on the next tick,
			// as RestartGame does.
			RunAfterTick(() =>
			{
				browserUiRecoveryPending = false;
				Disconnect();
				Ui.ResetAll();
				LoadShellMap();
				BrowserNotify?.Invoke(message);
			});
		}

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

			browserNextLogic = browserNextRender = browserLastBudgetReset = browserLastAnimationFrame = RunTime;
			browserInitialized = true;
		}

		/// <summary>What a browser frame does besides running the logic ticks that are due.</summary>
		public enum BrowserFrameKind
		{
			/// <summary>An animation frame: render (if a render is due), pumping input into the game.</summary>
			Render,

			/// <summary>
			/// A visible page between animation frames, driven by a steady clock: logic ticks only.
			/// Input stays queued for the next render frame. The exception is a tick that starts a game:
			/// the mod's load screen renders and pumps input (into a NullInputHandler) while the map loads,
			/// as it does on desktop.
			/// </summary>
			Logic,

			/// <summary>A hidden page, driven by a timer: logic ticks only, input is discarded.</summary>
			Hidden,
		}

		static bool browserTickedSinceRender;
		static double browserLogicFrameMilliseconds;

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

			if (kind == BrowserFrameKind.Logic && browserLogicFrameMilliseconds >= BrowserLogicFrameBudget)
			{
				var now = RunTime;
				if (now - browserLastAnimationFrame < BrowserNoAnimationFrameMilliseconds || now - browserLastBudgetReset < BrowserLogicFrameBudgetWindow)
				{
					BrowserLogicFramesOverBudget++;
					return true;
				}

				browserLogicFrameMilliseconds = 0;
				browserLastBudgetReset = now;
				BrowserLogicBudgetResets++;
			}

			var frameStart = RunTime;
			var frameStartTimestamp = Stopwatch.GetTimestamp();

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

			// Replays: the busy time of the logic frames since the last render counts against this frame's
			// tick time, so that a fast-forwarded replay keeps rendering about as often as Loop()'s
			// MinReplayFps (10). Matches are unaffected.
			var maxFrameLogicMilliseconds = (double)BrowserMaxFrameLogicMilliseconds;
			if (kind == BrowserFrameKind.Render && logicWorld != null && logicWorld.IsReplay)
				maxFrameLogicMilliseconds -= browserLogicFrameMilliseconds;

			// Run every logic tick that is due, not just one: a browser frame can take longer than a tick.
			var ticked = false;
			while (frameStart >= browserNextLogic && state == RunStatus.Running)
			{
				browserNextLogic += logicInterval;
				LogicTick();
				BrowserLogicTicks++;
				ticked = true;

				if (kind == BrowserFrameKind.Logic || RunTime - frameStart > maxFrameLogicMilliseconds)
					break;
			}

			if (state != RunStatus.Running)
				return false;

			if (ticked)
				browserTickedSinceRender = true;

			if (kind == BrowserFrameKind.Logic)
			{
				browserLogicFrameMilliseconds += Stopwatch.GetElapsedTime(frameStartTimestamp).TotalMilliseconds;
				return true;
			}

			browserLogicFrameMilliseconds = 0;
			browserLastBudgetReset = browserLastAnimationFrame = frameStart;

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
