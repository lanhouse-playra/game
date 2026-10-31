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
using OpenRA.Widgets;

namespace OpenRA.Mods.Common.Widgets.Logic
{
	/// <summary>
	/// Menu entries that start a local server (skirmish, missions, saved games, Create Server, the map
	/// editor's Play Map), in builds that cannot run one (<see cref="Game.CanHostLocalServer"/>, i.e. the
	/// browser build): disabled, with a tooltip that says why. The chrome files are not changed, so that
	/// the mod data stays equal to the release's.
	/// </summary>
	public static class LocalServerButtons
	{
		/// <summary>
		/// Adds an empty tooltip container to <paramref name="parent"/> for panels whose chrome has none.
		/// Returns its id.
		/// </summary>
		public static string AddTooltipContainer(Widget parent, string id)
		{
			parent.AddChild(new TooltipContainerWidget { Id = id });
			return id;
		}

		/// <summary>
		/// Disables <paramref name="button"/> with a tooltip shown by <paramref name="tooltipContainer"/>:
		/// <see cref="Game.BrowserNoLocalServer"/>, then <paramref name="hint"/> (default <see cref="Game.BrowserNoLocalServerHint"/>).
		/// </summary>
		public static void Disable(ButtonWidget button, string tooltipContainer, string hint = Game.BrowserNoLocalServerHint)
		{
			// Dev pages only (release pages ignore ?env=): keep the entries, to test the browser's UI action guard.
			if (Environment.GetEnvironmentVariable("PLAYRA_TEST_LOCAL_SERVER_ENTRIES") == "1")
				return;

			BrowserUnavailable.Disable(button, tooltipContainer, hint);
		}
	}
}
