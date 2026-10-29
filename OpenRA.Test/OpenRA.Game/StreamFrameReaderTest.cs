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
using NUnit.Framework;
using OpenRA.Network;
using OpenRA.Server;

namespace OpenRA.Test
{
	// The browser build receives the game server's TCP byte stream through a WebSocket relay, cut
	// into messages at arbitrary points (one TCP read per message on the relay, split or coalesced
	// by the network). StreamFrameReader must decode any such split exactly as the desktop receive
	// thread (NetworkConnection.NetworkConnectionReceive) decodes the unsplit stream.
	[TestFixture]
	sealed class StreamFrameReaderTest
	{
		const int ClientId = 3;

		// A server stream: handshake header, then frames shaped like real traffic (empty-order
		// frames, syncs and acks, order packets, occasional large lobby syncs and map data).
		static byte[] ServerStream(int frames, int seed)
		{
			var random = new Random(seed);
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			writer.Write(ProtocolVersion.Handshake);
			writer.Write(ClientId);
			for (var i = 0; i < frames; i++)
			{
				var roll = random.Next(100);
				var length = roll < 60 ? 4 + random.Next(30) : roll < 95 ? 30 + random.Next(400) : roll < 99 ? 2000 + random.Next(20000) : 40000 + random.Next(90000);
				var payload = new byte[length];
				random.NextBytes(payload);
				writer.Write(length);
				writer.Write(random.Next(-1, 9));
				writer.Write(payload);
			}

			writer.Flush();
			return ms.ToArray();
		}

		// The desktop receive loop, NetworkConnectionReceive(), over a complete stream.
		static (int ClientId, List<(int Client, byte[] Packet)> Frames) DesktopDecode(byte[] data)
		{
			var stream = new MemoryStream(data);
			var handshakeProtocol = stream.ReadInt32();
			if (handshakeProtocol != ProtocolVersion.Handshake)
				throw new InvalidOperationException("handshake");

			var clientId = stream.ReadInt32();
			var frames = new List<(int, byte[])>();
			while (stream.Position < stream.Length)
			{
				var len = stream.ReadInt32();
				var client = stream.ReadInt32();
				var buf = stream.ReadBytes(len);
				if (len == 0)
					throw new InvalidDataException("zero length");
				frames.Add((client, buf));
			}

			return (clientId, frames);
		}

		static (int ClientId, List<(int Client, byte[] Packet)> Frames) BrowserDecode(byte[] data, IEnumerable<int> pieceSizes)
		{
			var clientId = -1;
			var handshakes = 0;
			var frames = new List<(int, byte[])>();
			var reader = new StreamFrameReader(id => { clientId = id; handshakes++; }, (client, packet) => frames.Add((client, packet)));
			var offset = 0;
			foreach (var size in pieceSizes)
			{
				var n = Math.Min(size, data.Length - offset);
				reader.Append(data.AsSpan(offset, n));
				offset += n;
				if (offset == data.Length)
					break;
			}

			Assert.That(offset, Is.EqualTo(data.Length), "test feeds the whole stream");
			Assert.That(handshakes, Is.EqualTo(1));
			return (clientId, frames);
		}

		static IEnumerable<int> Repeat(int size)
		{
			while (true)
				yield return size;
		}

		static IEnumerable<int> RandomSizes(int seed, int max)
		{
			var random = new Random(seed);
			while (true)
				yield return 1 + random.Next(max);
		}

		static void AssertSame((int ClientId, List<(int Client, byte[] Packet)> Frames) expected, (int ClientId, List<(int Client, byte[] Packet)> Frames) actual)
		{
			Assert.That(actual.ClientId, Is.EqualTo(expected.ClientId));
			Assert.That(actual.Frames, Has.Count.EqualTo(expected.Frames.Count));
			for (var i = 0; i < expected.Frames.Count; i++)
			{
				Assert.That(actual.Frames[i].Client, Is.EqualTo(expected.Frames[i].Client), $"client of frame {i}");
				Assert.That(actual.Frames[i].Packet, Is.EqualTo(expected.Frames[i].Packet), $"bytes of frame {i}");
			}
		}

		[Test]
		public void WholeStreamMatchesDesktop()
		{
			var data = ServerStream(3000, 1);
			AssertSame(DesktopDecode(data), BrowserDecode(data, [data.Length]));
		}

		[Test]
		public void OneByteAtATimeMatchesDesktop()
		{
			var data = ServerStream(1500, 2);
			AssertSame(DesktopDecode(data), BrowserDecode(data, Repeat(1)));
		}

		[TestCase(2)]
		[TestCase(3)]
		[TestCase(7)]
		[TestCase(8)]
		[TestCase(9)]
		[TestCase(1400)]
		[TestCase(4096)]
		[TestCase(65536)]
		public void FixedSplitsMatchDesktop(int size)
		{
			var data = ServerStream(3000, 3);
			AssertSame(DesktopDecode(data), BrowserDecode(data, Repeat(size)));
		}

		[Test]
		public void RandomSplitsAndCoalescingMatchDesktop()
		{
			var data = ServerStream(3000, 4);
			var expected = DesktopDecode(data);
			for (var seed = 0; seed < 20; seed++)
			{
				AssertSame(expected, BrowserDecode(data, RandomSizes(seed, 16)));
				AssertSame(expected, BrowserDecode(data, RandomSizes(seed, 3000)));
				AssertSame(expected, BrowserDecode(data, RandomSizes(seed, 200000)));
			}
		}

		[Test]
		public void SplitsAtEveryHeaderBoundaryMatchDesktop()
		{
			// Cut inside the handshake and at each byte of the first frames' headers.
			var data = ServerStream(50, 5);
			var expected = DesktopDecode(data);
			for (var cut = 1; cut < 64; cut++)
				AssertSame(expected, BrowserDecode(data, [cut, data.Length]));
		}

		[Test]
		public void HandshakeMismatchFails()
		{
			var data = ServerStream(10, 6);
			data[0] ^= 0xFF;
			var reader = new StreamFrameReader(_ => { }, (_, _) => { });
			Assert.Throws<InvalidOperationException>(() => reader.Append(data));
		}

		[TestCase(0)]
		[TestCase(-5)]
		public void InvalidFrameLengthFails(int length)
		{
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			writer.Write(ProtocolVersion.Handshake);
			writer.Write(ClientId);
			writer.Write(length);
			writer.Write(1);
			writer.Flush();

			var frames = 0;
			var reader = new StreamFrameReader(_ => { }, (_, _) => frames++);
			Assert.Throws<InvalidDataException>(() => reader.Append(ms.ToArray()));
			Assert.That(frames, Is.Zero);
		}

		[Test]
		public void IncompleteFrameIsHeldBack()
		{
			var data = ServerStream(1, 7);
			var frames = 0;
			var reader = new StreamFrameReader(_ => { }, (_, _) => frames++);
			reader.Append(data.AsSpan(0, data.Length - 1));
			Assert.That(reader.HandshakeReceived, Is.True);
			Assert.That(frames, Is.Zero);
			reader.Append(data.AsSpan(data.Length - 1));
			Assert.That(frames, Is.EqualTo(1));
		}
	}
}
