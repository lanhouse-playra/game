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
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using OpenRA.Mods.Common;

namespace OpenRA.Test
{
	// The browser build reads the master server's JSON game list (the only form that allows cross-origin
	// reads) into the shape of its YAML listing, which the server browser then loads as on desktop.
	[TestFixture]
	sealed class MasterServerJsonTest
	{
		static List<MiniYamlNode> Parse(string json)
		{
			return MasterServerJson.ParseGameList(new MemoryStream(Encoding.UTF8.GetBytes(json)));
		}

		// Every key and value of a node tree, one "path=value" line each, in order.
		static List<string> Flatten(IEnumerable<MiniYamlNode> nodes, string prefix = "")
		{
			var lines = new List<string>();
			foreach (var node in nodes)
			{
				lines.Add($"{prefix}{node.Key}={node.Value.Value}");
				lines.AddRange(Flatten(node.Value.Nodes, prefix + node.Key + "/"));
			}

			return lines;
		}

		[Test]
		public void MatchesTheYamlListing()
		{
			const string Json = "[{\"id\":2070617,\"name\":\"Gscheid heabraggn\",\"address\":\"195.16.73.100:3007\",\"state\":2," +
				"\"ttl\":250,\"mod\":\"ra\",\"version\":\"playtest-20261026\",\"map\":\"a2065b1f889436c76a2b6ae2eb34d8612eab8397\"," +
				"\"players\":2,\"maxplayers\":4,\"bots\":\"0\",\"spectators\":0,\"protected\":\"true\",\"authentication\":false," +
				"\"location\":\"Norway\",\"started\":\"2026-11-12 10:00:00\",\"playtime\":61,\"disabled_spawn_points\":\"\"," +
				"\"clients\":[{\"name\":\"a\",\"fingerprint\":\"0123\",\"color\":\"FF0000\",\"faction\":\"soviet\",\"team\":1," +
				"\"spawnpoint\":2,\"isadmin\":\"true\",\"isspectator\":\"false\",\"isbot\":false}]}]";

			const string Yaml = "Game@0:\n" +
				"\tId: 2070617\n\tName: Gscheid heabraggn\n\tAddress: 195.16.73.100:3007\n\tState: 2\n\tTTL: 250\n\tMod: ra\n" +
				"\tVersion: playtest-20261026\n\tMap: a2065b1f889436c76a2b6ae2eb34d8612eab8397\n\tPlayers: 2\n\tMaxPlayers: 4\n" +
				"\tBots: 0\n\tSpectators: 0\n\tProtected: true\n\tAuthentication: false\n\tLocation: Norway\n" +
				"\tStarted: 2026-11-12 10:00:00\n\tPlayTime: 61\n\tDisabledSpawnPoints:\n" +
				"\tClients:\n\t\tClient@0:\n\t\t\tName: a\n\t\t\tFingerprint: 0123\n\t\t\tColor: FF0000\n\t\t\tFaction: soviet\n" +
				"\t\t\tTeam: 1\n\t\t\tSpawnPoint: 2\n\t\t\tIsAdmin: true\n\t\t\tIsSpectator: false\n\t\t\tIsBot: false\n";

			Assert.That(Flatten(Parse(Json)), Is.EqualTo(Flatten(MiniYaml.FromString(Yaml, "games.yaml"))));
		}

		[Test]
		public void DecodesHtmlEscapedStrings()
		{
			// The master server HTML-escapes every string it lists.
			var game = Parse("[{\"name\":\"C&amp;C Drora&#039;s &lt;Game&gt;\"}]").Single();
			Assert.That(game.Value.NodeWithKey("Name").Value.Value, Is.EqualTo("C&C Drora's <Game>"));
		}

		[Test]
		public void IgnoresUnknownKeysAndNulls()
		{
			var game = Parse("[{\"name\":\"n\",\"unknown\":{\"x\":1},\"started\":null,\"clients\":\"none\"}]").Single();
			Assert.That(Flatten([game]), Is.EqualTo(new[] { "Game@0=", "Game@0/Name=n", "Game@0/Started=" }));
		}

		[Test]
		public void SkipsEntriesThatAreNotGames()
		{
			var games = Parse("[1, null, \"x\", [], {\"id\":5}, true, {\"id\":6}]");
			Assert.That(Flatten(games), Is.EqualTo(new[] { "Game@0=", "Game@0/Id=5", "Game@1=", "Game@1/Id=6" }));
		}

		[TestCase("{\"error\":\"rate_limited\"}")]
		[TestCase("\"games\"")]
		public void RefusesAListThatIsNotAnArray(string json)
		{
			Assert.Throws<InvalidOperationException>(() => Parse(json));
		}
	}
}
