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

using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using OpenRA.Network;

namespace OpenRA.Web
{
	/// <summary>Network state for automated tests and the page's status line. Not used by the game.</summary>
	[SupportedOSPlatform("browser")]
	public static partial class NetDiagnostics
	{
		[JSExport]
		public static string Status()
		{
			var om = Game.BrowserOrderManager;
			var sb = new StringBuilder();
			using (var w = new Utf8JsonWriterString(sb))
			{
				var j = w.Writer;
				j.WriteStartObject();
				j.WriteString("connection", om?.Connection?.GetType().Name ?? "none");
				if (om?.Connection is NetworkConnection nc)
				{
					j.WriteString("state", nc.ConnectionState.ToString());
					j.WriteString("target", nc.Target.ToString());
					j.WriteString("error", nc.ErrorMessage);
				}

				if (om != null)
				{
					j.WriteNumber("clientId", om.Connection.LocalClientId);
					j.WriteBoolean("gameStarted", om.GameStarted);
					j.WriteBoolean("outOfSync", om.IsOutOfSync);
					j.WriteNumber("netFrame", om.NetFrameNumber);
					j.WriteNumber("localFrame", om.LocalFrameNumber);
					j.WriteNumber("orderQueueLength", om.OrderQueueLength);
					j.WriteNumber("suggestedTimestep", om.SuggestedTimestep);
					j.WriteString("worldType", om.World?.Type.ToString() ?? "none");
					j.WriteNumber("worldTick", om.World?.WorldTick ?? 0);
					j.WriteBoolean("gameOver", om.World?.IsGameOver ?? false);
					j.WriteString("serverError", om.ServerError);
					j.WriteString("map", om.LobbyInfo?.GlobalSettings?.Map);

					j.WriteStartArray("clients");
					if (om.LobbyInfo != null)
					{
						foreach (var c in om.LobbyInfo.Clients)
						{
							j.WriteStartObject();
							j.WriteNumber("index", c.Index);
							j.WriteString("name", c.Name);
							j.WriteString("bot", c.Bot);
							j.WriteString("slot", c.Slot);
							j.WriteNumber("team", c.Team);
							j.WriteString("state", c.State.ToString());
							j.WriteBoolean("admin", c.IsAdmin);
							j.WriteString("quality", c.ConnectionQuality.ToString());
							j.WriteEndObject();
						}
					}

					j.WriteEndArray();

					j.WriteStartArray("players");
					if (om.World?.Players != null)
					{
						foreach (var p in om.World.Players)
						{
							if (p.NonCombatant)
								continue;

							j.WriteStartObject();
							j.WriteString("name", p.ResolvedPlayerName);
							j.WriteNumber("clientIndex", p.ClientIndex);
							j.WriteString("winState", p.WinState.ToString());
							j.WriteEndObject();
						}
					}

					j.WriteEndArray();
				}

				j.WriteNumber("wsBytesSent", WebSocketTransport.BytesSent);
				j.WriteNumber("wsBytesReceived", WebSocketTransport.BytesReceived);
				j.WriteNumber("wsMessagesSent", WebSocketTransport.MessagesSent);
				j.WriteNumber("wsMessagesReceived", WebSocketTransport.MessagesReceived);
				j.WriteNumber("runTime", Game.RunTime);
				j.WriteEndObject();
			}

			return sb.ToString();
		}

		/// <summary>World and view state: local player, its actors, shroud at their cells, viewport.</summary>
		[JSExport]
		public static string WorldInfo()
		{
			var wr = Game.BrowserGetWorldRenderer();
			var world = wr?.World;
			var sb = new StringBuilder();
			using (var w = new Utf8JsonWriterString(sb))
			{
				var j = w.Writer;
				j.WriteStartObject();
				if (world != null)
				{
					j.WriteString("type", world.Type.ToString());
					j.WriteString("localPlayer", world.LocalPlayer?.PlayerName);
					j.WriteString("renderPlayer", world.RenderPlayer?.PlayerName);
					j.WriteString("viewportCenter", wr.Viewport.CenterPosition.ToString());
					j.WriteNumber("zoom", wr.Viewport.Zoom);
					j.WriteNumber("actors", world.Actors.Count());
					j.WriteStartArray("localActors");
					if (world.LocalPlayer != null)
					{
						foreach (var a in world.Actors.Where(a => a.Owner == world.LocalPlayer && a.IsInWorld && a.OccupiesSpace != null).Take(8))
						{
							j.WriteStartObject();
							j.WriteString("name", a.Info.Name);
							j.WriteString("pos", a.CenterPosition.ToString());
							j.WriteBoolean("explored", world.LocalPlayer.Shroud.IsExplored(a.Location));
							j.WriteBoolean("visible", world.LocalPlayer.Shroud.IsVisible(a.Location));
							j.WriteEndObject();
						}
					}

					j.WriteEndArray();
				}

				j.WriteEndObject();
			}

			return sb.ToString();
		}

		/// <summary>Sends a lobby/server command (as the lobby UI does), e.g. "slot_bot Multi1 0 normal".</summary>
		[JSExport]
		public static void Command(string text)
		{
			Game.BrowserOrderManager?.IssueOrder(Order.Command(text));
		}

		/// <summary>Sends a chat line.</summary>
		[JSExport]
		public static void Chat(string text)
		{
			Game.BrowserOrderManager?.IssueOrder(Order.Chat(text));
		}

		sealed class Utf8JsonWriterString : System.IDisposable
		{
			readonly StringBuilder target;
			readonly System.IO.MemoryStream stream = new();
			public readonly Utf8JsonWriter Writer;

			public Utf8JsonWriterString(StringBuilder target)
			{
				this.target = target;
				Writer = new Utf8JsonWriter(stream);
			}

			public void Dispose()
			{
				Writer.Flush();
				target.Append(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
				Writer.Dispose();
				stream.Dispose();
			}
		}
	}
}
