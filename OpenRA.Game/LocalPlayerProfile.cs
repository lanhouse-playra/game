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
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OpenRA.Support;

namespace OpenRA
{
	public sealed class LocalPlayerProfile
	{
		public enum LinkState { Unlinked, Linking, CheckingLink, ConnectionFailed, Linked }
		public enum LinkResult { Success, AuthFailure, LoginAttempts, Banned, Error, ConnectionFailed }

		sealed record KeyData(RSAParameters Parameters, string Fingerprint, string PublicKey);
		const int AuthKeySize = 2048;

		readonly string filePath;
		readonly PlayerDatabase playerDatabase;

		volatile LinkState innerState = LinkState.Unlinked;
		volatile PlayerProfile profileData;
		volatile KeyData keyData;
		volatile bool refreshing;

		// Browser build only: a key made in the browser that the forum does not know (yet). The player adds its
		// public key on the forum (User Control Panel), then checks; until then the profile is Unlinked.
		volatile KeyData browserUnregisteredKey;
		volatile bool browserKeyUnknown;

		public LinkState State
		{
			get => innerState;
			private set
			{
				innerState = value;
				OnStateChanged();
			}
		}

		public event Action OnStateChanged = () => { };

		public string Fingerprint => keyData?.Fingerprint;
		public string PublicKey => keyData?.PublicKey;

		public PlayerProfile ProfileData => profileData;

		/// <summary>
		/// Browser build: the public key (PEM) of this browser's key, whether or not the forum knows it yet, or null
		/// when there is no key. The player adds it to the forum account by hand: in the browser the game never
		/// sees the forum password (<see cref="LinkForumAccount"/> is not used there).
		/// </summary>
		public string BrowserPublicKey => (keyData ?? browserUnregisteredKey)?.PublicKey;

		/// <summary>Browser build: there is a key, and the last check found that the forum does not know it.</summary>
		public bool BrowserKeyUnknown => browserKeyUnknown && browserUnregisteredKey != null;

		/// <summary>Browser build: there is a key that the forum did not know at the last check.</summary>
		public bool BrowserHasUnregisteredKey => browserUnregisteredKey != null;

		public LocalPlayerProfile(string filePath, PlayerDatabase playerDatabase)
		{
			this.filePath = filePath;
			this.playerDatabase = playerDatabase;
			LoadKeyPair();
			RefreshPlayerData();
		}

		void LoadKeyPair()
		{
			try
			{
				if (File.Exists(filePath) && OperatingSystem.IsBrowser())
				{
					// RSACryptoServiceProvider is not supported in the browser: the same file format, in managed code.
					string xml;
					using (var data = File.OpenRead(filePath))
						xml = new string(Encoding.ASCII.GetChars(Convert.FromBase64String(data.ReadAllText())));

					var parameters = ManagedRsa.FromXmlString(xml);
					keyData = new KeyData(parameters, CryptoUtil.PublicKeyFingerprint(parameters), CryptoUtil.EncodePEMPublicKey(parameters));
					innerState = LinkState.ConnectionFailed;
				}
				else if (File.Exists(filePath))
				{
					using (var rsa = new RSACryptoServiceProvider())
					{
						using (var data = File.OpenRead(filePath))
						{
							var xmlData = Convert.FromBase64String(data.ReadAllText());
							rsa.FromXmlString(new string(Encoding.ASCII.GetChars(xmlData)));
						}

						var parameters = rsa.ExportParameters(true);
						var publicKey = CryptoUtil.EncodePEMPublicKey(parameters);
						var fingerprint = CryptoUtil.PublicKeyFingerprint(parameters);

						keyData = new KeyData(parameters, fingerprint, publicKey);
						innerState = LinkState.ConnectionFailed;
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Failed to load keys:");
				Console.WriteLine(e);
				Log.Write("debug", $"Failed to load player keypair from `{filePath}` with exception:");
				Log.Write("debug", e);
			}
		}

		public void DeleteKeypair()
		{
			try
			{
				File.Delete(filePath);
			}
			catch (Exception e)
			{
				Log.Write("debug", "Failed to delete keypair with exception:");
				Log.Write("debug", e);
				Console.WriteLine("Key deletion failed:");
				Console.WriteLine(e);
			}

			keyData = null;
			profileData = null;
			browserUnregisteredKey = null;
			browserKeyUnknown = false;
			State = LinkState.Unlinked;
		}

		public void RefreshPlayerData()
		{
			if (refreshing || State == LinkState.Unlinked)
				return;

			refreshing = true;
			State = LinkState.CheckingLink;

			Task.Run(async () =>
			{
				try
				{
					var client = HttpClientFactory.Create();
					var url = playerDatabase.Profile + Fingerprint;
					var httpResponseMessage = await client.GetAsync(url);

					// In the browser the answer comes through our relay, which reports its own failures (rate limit,
					// forum unreachable) with an error status: that is a connection failure, not an unknown key.
					if (OperatingSystem.IsBrowser())
						httpResponseMessage.EnsureSuccessStatusCode();

					var result = await httpResponseMessage.Content.ReadAsStreamAsync();

					var yaml = MiniYaml.FromStream(result, url).First();
					if (yaml.Key == "Player")
					{
						var data = FieldLoader.Load<PlayerProfile>(yaml.Value);
						if (data.KeyRevoked)
						{
							Log.Write("debug", $"Revoking key with fingerprint {Fingerprint}");
							DeleteKeypair();
						}
						else
						{
							profileData = data;
							State = LinkState.Linked;
						}
					}
					else if (OperatingSystem.IsBrowser())
					{
						// The player adds the key on the forum after the game made it: keep it until they do.
						Log.Write("debug", $"Unknown key with fingerprint {Fingerprint}, kept until it is added on the forum");
						browserUnregisteredKey = keyData;
						browserKeyUnknown = true;
						keyData = null;
						State = LinkState.Unlinked;
					}
					else
					{
						Log.Write("debug", $"Unknown key with fingerprint {Fingerprint}");
						DeleteKeypair();
					}
				}
				catch (Exception e)
				{
					Log.Write("debug", "Failed to parse player data result with exception:");
					Log.Write("debug", e);
					State = LinkState.ConnectionFailed;
				}

				refreshing = false;
			});
		}

		public void LinkForumAccount(string username, string password, Action<LinkResult> onComplete = null)
		{
			if (State != LinkState.Unlinked)
				return;

			State = LinkState.Linking;

			Task.Run(async () =>
			{
				try
				{
					var rsa = new RSACryptoServiceProvider(AuthKeySize);
					var parameters = rsa.ExportParameters(true);
					var publicKey = CryptoUtil.EncodePEMPublicKey(parameters);

					var args = new Dictionary<string, string>
					{
						{ "username", username },
						{ "password", password },
						{ "pubkey", publicKey },
					};

					var manifest = Game.ModData.Manifest;
					var agentEngineVersion = Uri.EscapeDataString(Game.EngineVersion);
					var agentModId = Uri.EscapeDataString(manifest.Id);
					var agentModVersion = Uri.EscapeDataString(manifest.Metadata.Version);

					var client = HttpClientFactory.Create();
					client.DefaultRequestHeaders.Add("User-Agent", $"OpenRA/{agentEngineVersion} {agentModId}/{agentModVersion}");
					var httpResponseMessage = await client.PostAsync(playerDatabase.Link, new FormUrlEncodedContent(args));
					var result = await httpResponseMessage.Content.ReadAsStringAsync();
					if (httpResponseMessage.IsSuccessStatusCode)
					{
						switch (result)
						{
							case "Success":
							case "Error: key exists":
							{
								var data = Convert.ToBase64String(Encoding.ASCII.GetBytes(rsa.ToXmlString(true)));
								await File.WriteAllTextAsync(filePath, data);
								LoadKeyPair();
								onComplete?.Invoke(LinkResult.Success);
								RefreshPlayerData();
								return;
							}

							case "Error: authentication failed":
								State = LinkState.Unlinked;
								onComplete?.Invoke(LinkResult.AuthFailure);
								return;

							case "Error: too many login attempts":
								State = LinkState.Unlinked;
								onComplete?.Invoke(LinkResult.LoginAttempts);
								return;

							case "Error: banned":
								State = LinkState.Unlinked;
								onComplete?.Invoke(LinkResult.Banned);
								return;

							default:
								State = LinkState.Unlinked;
								onComplete?.Invoke(LinkResult.Error);
								return;
						}
					}

					State = LinkState.Unlinked;
					onComplete?.Invoke(LinkResult.ConnectionFailed);
				}
				catch (Exception e)
				{
					Log.Write("debug", "Failed to link forum account with exception:");
					Log.Write("debug", e);
					State = LinkState.Unlinked;
					onComplete?.Invoke(LinkResult.Error);
				}
			});
		}

		/// <summary>
		/// Browser build: makes this browser's key (once; it is kept in the same file as on desktop) without
		/// sending anything anywhere. The profile stays Unlinked until <see cref="BrowserCheckKey"/> finds the
		/// key on the forum. The key is generated in small steps between frames.
		/// </summary>
		public void BrowserCreateKey(Action<bool> onComplete = null)
		{
			if (!OperatingSystem.IsBrowser())
				throw new PlatformNotSupportedException("BrowserCreateKey is only used in the browser.");

			if (State != LinkState.Unlinked || browserUnregisteredKey != null)
				return;

			State = LinkState.Linking;
			Task.Run(async () =>
			{
				try
				{
					var generator = new ManagedRsa.KeyGenerator(AuthKeySize);
					while (!generator.Step())
						await Task.Yield();

					var parameters = generator.Result;
					var data = Convert.ToBase64String(Encoding.ASCII.GetBytes(ManagedRsa.ToXmlString(parameters, true)));
					await File.WriteAllTextAsync(filePath, data);

					browserUnregisteredKey = new KeyData(parameters, CryptoUtil.PublicKeyFingerprint(parameters), CryptoUtil.EncodePEMPublicKey(parameters));
					browserKeyUnknown = false;
					State = LinkState.Unlinked;
					onComplete?.Invoke(true);
				}
				catch (Exception e)
				{
					Log.Write("debug", "Failed to create a key with exception:");
					Log.Write("debug", e);
					State = LinkState.Unlinked;
					onComplete?.Invoke(false);
				}
			});
		}

		/// <summary>Browser build: asks the forum whether it knows this browser's key now (it links if it does).</summary>
		public void BrowserCheckKey()
		{
			if (refreshing || State != LinkState.Unlinked || browserUnregisteredKey == null)
				return;

			keyData = browserUnregisteredKey;
			browserUnregisteredKey = null;
			browserKeyUnknown = false;
			innerState = LinkState.ConnectionFailed;
			RefreshPlayerData();
		}

		public string Sign(params string[] data)
		{
			// If we don't have any keys, or we know for sure that they haven't been linked to the forum
			// then we can't do much here. If we have keys but don't yet know if they have been linked to the
			// forum (LinkState.CheckingLink or ConnectionFailed) then we sign to avoid blocking the main thread
			// but accept that - if the cert is invalid - the server will reject the result.
			if (State < LinkState.CheckingLink)
				return null;

			return CryptoUtil.Sign(keyData.Parameters, data.Where(x => !string.IsNullOrEmpty(x)).JoinWith(string.Empty));
		}

		public string DecryptString(string data)
		{
			if (State < LinkState.CheckingLink)
				return null;

			return CryptoUtil.DecryptString(keyData.Parameters, data);
		}
	}
}
