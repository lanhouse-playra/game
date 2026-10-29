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

using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;

namespace OpenRA.Mods.Common
{
	/// <summary>
	/// Reads the master server's JSON game list (games?type=json&amp;protocol=2) into the same
	/// MiniYaml shape as its default YAML listing. Used by the browser (WebAssembly) build:
	/// the master server only allows cross-origin reads of the JSON form.
	/// The key names mirror the YAML output of the master server (games.php).
	/// </summary>
	public static class MasterServerJson
	{
		public const string QueryType = "json";

		static readonly Dictionary<string, string> GameKeys = new()
		{
			{ "id", "Id" },
			{ "name", "Name" },
			{ "address", "Address" },
			{ "state", "State" },
			{ "ttl", "TTL" },
			{ "mods", "Mods" },
			{ "mod", "Mod" },
			{ "version", "Version" },
			{ "modtitle", "ModTitle" },
			{ "modversion", "ModVersion" },
			{ "modwebsite", "ModWebsite" },
			{ "modicon32", "ModIcon32" },
			{ "map", "Map" },
			{ "players", "Players" },
			{ "maxplayers", "MaxPlayers" },
			{ "bots", "Bots" },
			{ "spectators", "Spectators" },
			{ "disabled_spawn_points", "DisabledSpawnPoints" },
			{ "protected", "Protected" },
			{ "authentication", "Authentication" },
			{ "location", "Location" },
			{ "started", "Started" },
			{ "playtime", "PlayTime" },
		};

		static readonly Dictionary<string, string> ClientKeys = new()
		{
			{ "name", "Name" },
			{ "fingerprint", "Fingerprint" },
			{ "color", "Color" },
			{ "faction", "Faction" },
			{ "team", "Team" },
			{ "spawnpoint", "SpawnPoint" },
			{ "isadmin", "IsAdmin" },
			{ "isspectator", "IsSpectator" },
			{ "isbot", "IsBot" },
		};

		public static List<MiniYamlNode> ParseGameList(Stream stream)
		{
			using var document = JsonDocument.Parse(stream);
			var games = new List<MiniYamlNode>();
			foreach (var game in document.RootElement.EnumerateArray())
			{
				var nodes = new List<MiniYamlNode>();
				foreach (var property in game.EnumerateObject())
				{
					if (property.Name == "clients" && property.Value.ValueKind == JsonValueKind.Array)
					{
						var clients = new List<MiniYamlNode>();
						foreach (var client in property.Value.EnumerateArray())
							clients.Add(new MiniYamlNode("Client@" + clients.Count, new MiniYaml("", ReadObject(client, ClientKeys))));

						nodes.Add(new MiniYamlNode("Clients", new MiniYaml("", clients)));
					}
					else if (GameKeys.TryGetValue(property.Name, out var key))
						nodes.Add(new MiniYamlNode(key, ToValue(property.Value)));
				}

				games.Add(new MiniYamlNode("Game@" + games.Count, new MiniYaml("", nodes)));
			}

			return games;
		}

		static List<MiniYamlNode> ReadObject(JsonElement element, Dictionary<string, string> keys)
		{
			var nodes = new List<MiniYamlNode>();
			if (element.ValueKind != JsonValueKind.Object)
				return nodes;

			foreach (var property in element.EnumerateObject())
				if (keys.TryGetValue(property.Name, out var key))
					nodes.Add(new MiniYamlNode(key, ToValue(property.Value)));

			return nodes;
		}

		// The master server HTML-escapes every string it lists (htmlspecialchars), in the JSON and the YAML
		// listing alike: "C&amp;C", "Drora&#039;s Game". Decode them, so that names and versions are the real ones.
		static string ToValue(JsonElement value)
		{
			return value.ValueKind switch
			{
				JsonValueKind.True => "true",
				JsonValueKind.False => "false",
				JsonValueKind.String => WebUtility.HtmlDecode(value.GetString()),
				JsonValueKind.Null => "",
				_ => value.GetRawText(),
			};
		}
	}
}
