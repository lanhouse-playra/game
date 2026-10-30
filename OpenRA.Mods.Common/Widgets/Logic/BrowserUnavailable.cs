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

namespace OpenRA.Mods.Common.Widgets.Logic
{
	/// <summary>
	/// Menu entries and settings the browser build cannot offer stay where they are, disabled, with a tooltip
	/// that says so (<see cref="Game.BrowserNotAvailable"/>). The chrome files are not changed, so that the mod
	/// data stays equal to the release's. Callers guard on <see cref="System.OperatingSystem.IsBrowser"/>.
	/// </summary>
	public static class BrowserUnavailable
	{
		/// <summary>
		/// Disables <paramref name="button"/> (also a checkbox or a dropdown) with a tooltip shown by
		/// <paramref name="tooltipContainer"/>: <see cref="Game.BrowserNotAvailable"/>, then
		/// <paramref name="hint"/> if there is one.
		/// </summary>
		public static void Disable(ButtonWidget button, string tooltipContainer, string hint = null)
		{
			button.IsDisabled = () => true;

			// TooltipContainer is a chrome field: set it the way the widget loader does.
			FieldLoader.LoadFieldOrProperty(button, nameof(ButtonWidget.TooltipContainer), tooltipContainer);
			button.GetTooltipText = () => Game.BrowserNotAvailable;
			button.GetTooltipDesc = () => hint;
		}

		/// <summary>Disables a text field (text fields have no tooltip: a button next to it carries it).</summary>
		public static void Disable(TextFieldWidget field)
		{
			field.IsDisabled = () => true;
		}
	}
}
