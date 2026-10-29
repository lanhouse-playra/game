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
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using OpenRA.Network;

namespace OpenRA.Web
{
	/// <summary>
	/// Game connections in the browser: a WebSocket to the WebSocket-to-TCP relay configured in
	/// Game.WebSocketProxy, which opens a TCP connection to the game server and copies bytes both ways.
	/// </summary>
	/// <remarks>
	/// This uses the page's WebSocket object through JS interop (wwwroot/main.js, module "webra-net")
	/// rather than System.Net.WebSockets.ClientWebSocket. In the single-threaded browser runtime the
	/// completions of ClientWebSocket.ReceiveAsync/SendAsync run as runtime background jobs, which the
	/// runtime schedules with setTimeout(0) (and SendAsync polls bufferedAmount with setTimeout). Browsers
	/// throttle timers in hidden tabs to about once per second, which would add up to a second of latency
	/// to every lockstep frame. WebSocket events are not throttled: here each "message" event calls
	/// straight into OnMessage, which hands the bytes to NetworkConnection on the main thread.
	/// </remarks>
	[SupportedOSPlatform("browser")]
	public static partial class WebSocketTransport
	{
		const string ModuleName = "webra-net";

		static readonly Dictionary<int, Socket> Sockets = [];

		// Statistics for tests and diagnostics.
		public static long BytesSent { get; private set; }
		public static long BytesReceived { get; private set; }
		public static int MessagesSent { get; private set; }
		public static int MessagesReceived { get; private set; }

		[JSImport("open", ModuleName)]
		private static partial int JsOpen(string url);

		[JSImport("send", ModuleName)]
		private static partial void JsSend(int id, [JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> data);

		[JSImport("close", ModuleName)]
		private static partial void JsClose(int id);

		sealed class Socket : IStreamTransport
		{
			public readonly int Id;
			public readonly IStreamTransportListener Listener;
			bool closed;

			public Socket(int id, IStreamTransportListener listener)
			{
				Id = id;
				Listener = listener;
			}

			public void Send(ArraySegment<byte> data)
			{
				if (closed)
					return;

				BytesSent += data.Count;
				MessagesSent++;
				JsSend(Id, data);
			}

			public void Close()
			{
				if (closed)
					return;

				closed = true;
				Sockets.Remove(Id);
				JsClose(Id);
			}
		}

		/// <summary>Builds the relay URL for a game server: {proxy}?target={host}:{port}.</summary>
		public static string RelayUrl(string proxy, DnsEndPoint endpoint)
		{
			// IP literals in canonical form: .NET also parses "127.1" or a bare number as IPv4, which the
			// relay would treat as a host name.
			var host = endpoint.Host;
			if (IPAddress.TryParse(host, out var address))
				host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();

			var separator = proxy.Contains('?') ? '&' : '?';
			return proxy + separator + "target=" + Uri.EscapeDataString(host + ":" + endpoint.Port);
		}

		public static IStreamTransport Open(DnsEndPoint endpoint, IStreamTransportListener listener)
		{
			var proxy = Game.Settings.Game.WebSocketProxy;
			if (string.IsNullOrEmpty(proxy))
				throw new InvalidOperationException("No WebSocket relay configured (Game.WebSocketProxy).");

			var url = RelayUrl(proxy, endpoint);
			Log.Write("client", $"Connecting to {endpoint.Host}:{endpoint.Port} through {url}");
			var id = JsOpen(url);
			var socket = new Socket(id, listener);
			Sockets.Add(id, socket);
			return socket;
		}

		// The callbacks below run from WebSocket events, between frames, on the main thread.
		// An exception must not escape into the browser's event dispatch: log it and drop the socket.
		[JSExport]
		static void OnOpen(int id)
		{
			if (Sockets.TryGetValue(id, out var socket))
				Dispatch(socket, s => s.Listener.OnOpen());
		}

		[JSExport]
		static void OnMessage(int id, byte[] data)
		{
			if (!Sockets.TryGetValue(id, out var socket))
				return;

			BytesReceived += data.Length;
			MessagesReceived++;
			Dispatch(socket, s => s.Listener.OnReceive(data));
		}

		[JSExport]
		static void OnClose(int id, int code, string reason)
		{
			if (!Sockets.Remove(id, out var socket))
				return;

			Dispatch(socket, s => s.Listener.OnClose($"WebSocket closed (code {code}{(string.IsNullOrEmpty(reason) ? "" : ", " + reason)})", CloseMessage(code)));
		}

		/// <summary>
		/// The relay accepts the WebSocket before it checks the target and connects, and reports a
		/// refusal with these close codes (source/proxy README), because a page cannot read the HTTP
		/// status of a failed WebSocket upgrade. Other codes keep the engine's generic message.
		/// </summary>
		public static string CloseMessage(int code)
		{
			return code switch
			{
				4400 => "Invalid server address",
				4403 => "The web version can only join servers on the public server list",
				4429 => "Too many connection attempts, try again in a moment",
				4502 => "The server did not answer",
				4503 => "The relay is busy, try again later",
				_ => null,
			};
		}

		static void Dispatch(Socket socket, Action<Socket> action)
		{
			try
			{
				action(socket);
			}
			catch (Exception e)
			{
				Console.Error.WriteLine("OpenRA.Web: network callback failed: " + e);
				socket.Close();
			}
		}
	}
}
