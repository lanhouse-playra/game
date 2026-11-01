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

using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using OpenRA.Mods.Common.Widgets;
using OpenRA.Widgets;

namespace OpenRA.Web
{
	/// <summary>
	/// The engine side of the page's on-screen keyboard (playra-keyboard.js). Phones and tablets show their
	/// keyboard only for a text input of the page that the page focuses while it handles the player's tap, so
	/// the page asks, inside its touchend handler, whether the tap put a text field in focus (<see cref="Tap"/>),
	/// and focuses an input of its own if it did. What is typed there comes back through <see cref="Type"/> and
	/// <see cref="Key"/>; <see cref="Focused"/> tells the page what the field holds and when it lost focus.
	/// </summary>
	[SupportedOSPlatform("browser")]
	public static partial class TextInput
	{
		/// <summary>
		/// Called while the page handles a tap that ended at (<paramref name="x"/>, <paramref name="y"/>), in the
		/// engine's window coordinates. Delivers the input that SDL has queued (the tap itself), then returns the
		/// state of the focused text field (see <see cref="Focused"/>) if the tap was on it, or an empty array.
		/// </summary>
		[JSExport]
		public static string[] Tap(int x, int y)
		{
			Game.BrowserPumpInput();
			var field = FocusedField();
			return field != null && field.RenderBounds.Contains(x, y) ? State(field) : [];
		}

		/// <summary>
		/// The text field with keyboard focus, as [kind, id, maxLength, cursor, x, y, width, height, text]:
		/// kind is "password", "number" or "text", the rectangle is in the engine's window coordinates.
		/// An empty array when no enabled text field has keyboard focus.
		/// </summary>
		[JSExport]
		public static string[] Focused()
		{
			var field = FocusedField();
			return field != null ? State(field) : [];
		}

		/// <summary>Text typed in the page's input, delivered as the engine's text input.</summary>
		[JSExport]
		public static void Type(string text)
		{
			if (!string.IsNullOrEmpty(text))
				Game.BrowserTextInput(text);
		}

		/// <summary>
		/// A key pressed in the page's input, by its DOM key name (Enter, Backspace, Delete, Escape, Tab, the arrows,
		/// Home, End, or a letter with Ctrl or Meta), with the engine's <see cref="Modifiers"/> as a number.
		/// Returns false for a key it does not deliver.
		/// </summary>
		[JSExport]
		public static bool Key(string key, int modifiers)
		{
			Keycode code;
			switch (key)
			{
				case "Enter": code = Keycode.RETURN; break;
				case "Backspace": code = Keycode.BACKSPACE; break;
				case "Delete": code = Keycode.DELETE; break;
				case "Escape": code = Keycode.ESCAPE; break;
				case "Tab": code = Keycode.TAB; break;
				case "ArrowLeft": code = Keycode.LEFT; break;
				case "ArrowRight": code = Keycode.RIGHT; break;
				case "ArrowUp": code = Keycode.UP; break;
				case "ArrowDown": code = Keycode.DOWN; break;
				case "Home": code = Keycode.HOME; break;
				case "End": code = Keycode.END; break;
				default:
					if (key.Length == 1 && char.ToLowerInvariant(key[0]) is >= 'a' and <= 'z')
						code = (Keycode)char.ToLowerInvariant(key[0]);
					else
						return false;
					break;
			}

			Game.BrowserKeyInput(code, (Modifiers)modifiers);
			return true;
		}

		static TextFieldWidget FocusedField()
		{
			return Ui.KeyboardFocusWidget is TextFieldWidget field && field.IsVisible() && !field.IsDisabled() ? field : null;
		}

		static string[] State(TextFieldWidget field)
		{
			var kind = field is PasswordFieldWidget ? "password" : field.Type == TextFieldType.Integer ? "number" : "text";
			var r = field.RenderBounds;
			var c = CultureInfo.InvariantCulture;
			return
			[
				kind, field.Id ?? "", field.MaxLength.ToString(c), field.CursorPosition.ToString(c),
				r.X.ToString(c), r.Y.ToString(c), r.Width.ToString(c), r.Height.ToString(c), field.Text
			];
		}
	}
}
