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
using System.Buffers.Binary;
using System.IO;
using System.Net;
using OpenRA.Server;

namespace OpenRA.Network
{
	/// <summary>
	/// A bidirectional byte stream to a game server, opened by the platform host.
	/// It must carry exactly the bytes a TCP connection to the server would carry.
	/// </summary>
	public interface IStreamTransport
	{
		/// <summary>Queues bytes for sending. Must not block.</summary>
		void Send(ArraySegment<byte> data);

		/// <summary>Closes the stream. No listener callbacks follow.</summary>
		void Close();
	}

	/// <summary>Receives the events of an <see cref="IStreamTransport"/>, on the main thread.</summary>
	public interface IStreamTransportListener
	{
		void OnOpen();
		void OnReceive(ReadOnlySpan<byte> data);

		/// <summary>
		/// The stream was closed. reason is for the log; error, if not null, is a message for the
		/// player that explains the failure better than the generic one (e.g. the relay refused the server).
		/// </summary>
		void OnClose(string reason, string error);
	}

	/// <summary>
	/// Decodes a game server's byte stream as NetworkConnectionReceive() does: the 8-byte handshake
	/// header (protocol version, client id), then [Int32 length][Int32 client][payload] frames.
	/// The stream may arrive in pieces of any size; frames are reported once complete.
	/// </summary>
	public sealed class StreamFrameReader
	{
		readonly Action<int> onHandshake;
		readonly Action<int, byte[]> onFrame;
		byte[] buffer = new byte[4096];
		int count;

		public bool HandshakeReceived { get; private set; }

		public StreamFrameReader(Action<int> onHandshake, Action<int, byte[]> onFrame)
		{
			this.onHandshake = onHandshake;
			this.onFrame = onFrame;
		}

		/// <summary>
		/// Appends bytes and reports every completed handshake and frame. Throws on a protocol
		/// violation (handshake version mismatch, frame length zero or negative), where
		/// NetworkConnectionReceive() also fails; the stream is unusable afterwards.
		/// </summary>
		public void Append(ReadOnlySpan<byte> data)
		{
			if (buffer.Length - count < data.Length)
			{
				var resized = new byte[Math.Max(buffer.Length * 2, count + data.Length)];
				Buffer.BlockCopy(buffer, 0, resized, 0, count);
				buffer = resized;
			}

			data.CopyTo(buffer.AsSpan(count));
			count += data.Length;

			var offset = 0;
			if (!HandshakeReceived)
			{
				if (count < 8)
					return;

				var handshakeProtocol = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));
				if (handshakeProtocol != ProtocolVersion.Handshake)
					throw new InvalidOperationException($"Handshake protocol version mismatch. Server={handshakeProtocol} Client={ProtocolVersion.Handshake}");

				HandshakeReceived = true;
				offset = 8;
				onHandshake(BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(4, 4)));
			}

			while (count - offset >= 8)
			{
				var len = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
				var client = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
				if (len <= 0)
					throw new InvalidDataException($"Invalid frame length {len}");

				if (count - offset - 8 < len)
					break;

				onFrame(client, buffer.AsSpan(offset + 8, len).ToArray());
				offset += 8 + len;
			}

			if (offset > 0)
			{
				Buffer.BlockCopy(buffer, offset, buffer, 0, count - offset);
				count -= offset;
			}
		}
	}

	// Browser transport for NetworkConnection. Browsers cannot open TCP sockets or start threads, so
	// the browser host provides a byte stream (a WebSocket to a WebSocket-to-TCP relay) whose events
	// arrive on the main thread between frames. This file only replaces the socket and the receive
	// thread: it reads the same handshake and frames as NetworkConnectionReceive() and feeds them into
	// the same receivedPackets queue, so everything from IConnection.Receive() on is shared with
	// the desktop code path.
	public sealed partial class NetworkConnection
	{
		/// <summary>Set by the browser host before any connection is made.</summary>
		public static Func<DnsEndPoint, IStreamTransportListener, IStreamTransport> StreamTransportFactory;

		// The relay accepts the WebSocket first, then connects to the game server (5 s timeout) and
		// waits for its handshake header (5 s), so the time to the handshake is longer than desktop's
		// 5 s TCP connect timeout.
		const int BrowserConnectTimeout = 15000;

		IStreamTransport transport;

		void BrowserConnect()
		{
			// Only the first endpoint is used: the relay resolves the address and there is no
			// parallel connect race as on desktop. Every ConnectionTarget a remote server
			// produces has a single endpoint.
			var endpoints = Target.EndPoints;
			if (endpoints.Count == 0 || endpoints[0].Port == 0)
			{
				ErrorMessage = "Failed to resolve address";
				connectionState = ConnectionState.NotConnected;
				return;
			}

			if (StreamTransportFactory == null)
			{
				ErrorMessage = "No network transport is available";
				connectionState = ConnectionState.NotConnected;
				return;
			}

			var endpoint = endpoints[0];
			if (IPAddress.TryParse(endpoint.Host, out var address))
				EndPoint = new IPEndPoint(address, endpoint.Port);

			var listener = new BrowserStreamReader(this);
			try
			{
				transport = StreamTransportFactory(endpoint, listener);
			}
			catch (Exception ex)
			{
				ErrorMessage = "Failed to connect";
				Log.Write("client", $"Failed to connect to {endpoint.Host}:{endpoint.Port}: {ex.Message}");
				connectionState = ConnectionState.NotConnected;
				return;
			}

			Game.RunAfterDelay(BrowserConnectTimeout, () =>
			{
				// transport is null once the connection is closed or disposed (the player cancelled the connect).
				if (transport != null && !listener.HandshakeReceived && !listener.Closed)
				{
					Log.Write("client", $"Failed to connect to {endpoint.Host}:{endpoint.Port}: timed out");
					listener.Fail("Failed to connect");
				}
			});
		}

		void BrowserSend(MemoryStream ms)
		{
			// Same as the desktop path: a send on a broken connection is dropped and the
			// disconnect is picked up by the receiving side.
			if (transport != null && connectionState == ConnectionState.Connected)
				transport.Send(new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length));
		}

		void BrowserClose()
		{
			var t = transport;
			transport = null;
			t?.Close();
		}

		/// <summary>Feeds the transport's bytes into the connection's receive queue.</summary>
		sealed class BrowserStreamReader : IStreamTransportListener
		{
			readonly NetworkConnection connection;
			readonly StreamFrameReader reader;

			public bool Closed { get; private set; }
			public bool HandshakeReceived => reader.HandshakeReceived;

			public BrowserStreamReader(NetworkConnection connection)
			{
				this.connection = connection;
				reader = new StreamFrameReader(
					clientId =>
					{
						connection.clientId = clientId;
						connection.connectionState = ConnectionState.Connected;
					},
					(client, packet) => connection.receivedPackets.Enqueue((client, packet)));
			}

			public void OnOpen() { }

			public void OnReceive(ReadOnlySpan<byte> data)
			{
				if (Closed)
					return;

				try
				{
					reader.Append(data);
				}
				catch (Exception ex)
				{
					Log.Write("client", $"Connection to {connection.Target} failed: {ex.Message}");
					Fail("Connection failed");
				}
			}

			public void OnClose(string reason, string error)
			{
				if (Closed)
					return;

				Log.Write("client", $"Connection to {connection.Target} closed: {reason}");
				Fail(error ?? (HandshakeReceived ? "Connection failed" : "Failed to connect"));
			}

			public void Fail(string error)
			{
				Closed = true;
				connection.ErrorMessage = error;
				connection.connectionState = ConnectionState.NotConnected;
				connection.BrowserClose();
			}
		}
	}
}
