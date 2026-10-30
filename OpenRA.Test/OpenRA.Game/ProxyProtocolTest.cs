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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using OpenRA.Server;

namespace OpenRA.Test
{
	[TestFixture]
	sealed class ProxyProtocolTest
	{
		static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

		static IPEndPoint Parse(string header)
		{
			Assert.That(ProxyProtocol.TryParseHeader(Ascii(header), out var source), Is.True, header);
			return source;
		}

		[Test]
		public void ParsesTcp4()
		{
			Assert.That(Parse("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234\r\n"),
				Is.EqualTo(new IPEndPoint(IPAddress.Parse("192.0.2.10"), 51234)));
		}

		[Test]
		public void ParsesTcp6()
		{
			Assert.That(Parse("PROXY TCP6 2001:db8::1 2001:db8::2 0 1234\r\n"),
				Is.EqualTo(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 0)));
		}

		[Test]
		public void MapsIPv4MappedSourceToIPv4()
		{
			// A relay that accepted an IPv4 client but reached the server over IPv6 sends a TCP6 header.
			var source = Parse("PROXY TCP6 ::ffff:192.0.2.10 2001:db8::2 51234 1234\r\n");
			Assert.That(source.Address, Is.EqualTo(IPAddress.Parse("192.0.2.10")));
			Assert.That(source.Address.AddressFamily, Is.EqualTo(AddressFamily.InterNetwork));
		}

		[TestCase("PROXY UNKNOWN\r\n")]
		[TestCase("PROXY UNKNOWN ffff:f...f:ffff ffff:f...f:ffff 65535 65535\r\n")]
		public void AcceptsUnknownWithoutSource(string header)
		{
			Assert.That(Parse(header), Is.Null, "UNKNOWN means: use the connection's own address");
		}

		[Test]
		public void AcceptsTheLongestHeaders()
		{
			// The specification's worst cases: 104 bytes for TCP6 and 107 for UNKNOWN. (An all-ffff
			// source would be a multicast address, which is refused.)
			const string Tcp6 = "PROXY TCP6 2001:0db8:ffff:ffff:ffff:ffff:ffff:ffff ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff 65535 65535\r\n";
			Assert.That(Tcp6, Has.Length.EqualTo(104));
			Assert.That(Parse(Tcp6).Port, Is.EqualTo(65535));

			// Literal lengths, not MaxHeaderLength: the limit itself is what is tested.
			var unknown = "PROXY UNKNOWN " + new string('x', 91) + "\r\n";
			Assert.That(unknown, Has.Length.EqualTo(107));
			Assert.That(Parse(unknown), Is.Null);
			Assert.That(ReadHeader(new MemoryStream(Ascii(unknown))), Is.EqualTo(Ascii(unknown)));

			var tooLong = "PROXY UNKNOWN " + new string('x', 92) + "\r\n";
			Assert.That(tooLong, Has.Length.EqualTo(108));
			Assert.That(ProxyProtocol.TryParseHeader(Ascii(tooLong), out _), Is.False);
			Assert.That(ReadHeader(new MemoryStream(Ascii(tooLong))), Is.Null);
		}

		[TestCase("", TestName = "Empty")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234", TestName = "No CRLF")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234\n", TestName = "LF only")]
		[TestCase("proxy TCP4 192.0.2.10 198.51.100.7 51234 1234\r\n", TestName = "Lower case signature")]
		[TestCase("PROXY tcp4 192.0.2.10 198.51.100.7 51234 1234\r\n", TestName = "Lower case protocol")]
		[TestCase("PROXY UDP4 192.0.2.10 198.51.100.7 51234 1234\r\n", TestName = "Other protocol")]
		[TestCase("PROXY TCP4  192.0.2.10 198.51.100.7 51234 1234\r\n", TestName = "Double space")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234 \r\n", TestName = "Trailing space")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234\r\n", TestName = "Missing port")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234 5\r\n", TestName = "Extra field")]
		[TestCase("PROXY TCP4 192.0.2.10\t198.51.100.7 51234 1234\r\n", TestName = "Tab")]
		[TestCase("PROXY TCP4 2001:db8::1 2001:db8::2 51234 1234\r\n", TestName = "TCP4 with IPv6 addresses")]
		[TestCase("PROXY TCP6 192.0.2.10 198.51.100.7 51234 1234\r\n", TestName = "TCP6 with IPv4 addresses")]
		[TestCase("PROXY TCP4 192.0.2.10 2001:db8::2 51234 1234\r\n", TestName = "Mixed families")]
		[TestCase("PROXY TCP4 1 198.51.100.7 51234 1234\r\n", TestName = "Short IPv4 form")]
		[TestCase("PROXY TCP4 0x7f.0.0.1 198.51.100.7 51234 1234\r\n", TestName = "Hex IPv4 form")]
		[TestCase("PROXY TCP4 192.0.2.010 198.51.100.7 51234 1234\r\n", TestName = "Leading zero in IPv4")]
		[TestCase("PROXY TCP4 192.0.2.256 198.51.100.7 51234 1234\r\n", TestName = "IPv4 octet out of range")]
		[TestCase("PROXY TCP6 fe80::1%eth0 2001:db8::2 51234 1234\r\n", TestName = "IPv6 zone")]
		[TestCase("PROXY TCP6 [2001:db8::1] 2001:db8::2 51234 1234\r\n", TestName = "IPv6 in brackets")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 65536 1234\r\n", TestName = "Port out of range")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 051234 1234\r\n", TestName = "Leading zero in port")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 01234 1234\r\n", TestName = "Leading zero in a five-character port")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 00 1\r\n", TestName = "Port 00")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 1234 01\r\n", TestName = "Leading zero in destination port")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 -1 1234\r\n", TestName = "Negative port")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 +1 1234\r\n", TestName = "Port with sign")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 51234 \r\n", TestName = "Empty port")]
		[TestCase("PROXY \r\n", TestName = "No protocol")]
		[TestCase("PROXY UNKNOWNX\r\n", TestName = "UNKNOWN prefix of another word")]
		[TestCase("PROXY TCP4 0.0.0.0 198.51.100.7 51234 1234\r\n", TestName = "Unspecified IPv4 source")]
		[TestCase("PROXY TCP6 :: 2001:db8::2 51234 1234\r\n", TestName = "Unspecified IPv6 source")]
		[TestCase("PROXY TCP6 ::ffff:0.0.0.0 2001:db8::2 51234 1234\r\n", TestName = "Unspecified IPv4-mapped source")]
		[TestCase("PROXY TCP4 255.255.255.255 198.51.100.7 51234 1234\r\n", TestName = "Broadcast source")]
		[TestCase("PROXY TCP4 224.0.0.1 198.51.100.7 51234 1234\r\n", TestName = "IPv4 multicast source")]
		[TestCase("PROXY TCP4 239.255.255.255 198.51.100.7 51234 1234\r\n", TestName = "IPv4 multicast source, last")]
		[TestCase("PROXY TCP6 ff02::1 2001:db8::2 51234 1234\r\n", TestName = "IPv6 multicast source")]
		public void RejectsMalformedHeader(string header)
		{
			Assert.That(ProxyProtocol.TryParseHeader(Ascii(header), out var source), Is.False);
			Assert.That(source, Is.Null);
		}

		[Test]
		public void AcceptsSourcesNextToTheRefusedRanges()
		{
			Assert.That(Parse("PROXY TCP4 223.255.255.255 198.51.100.7 1 1\r\n").Address, Is.EqualTo(IPAddress.Parse("223.255.255.255")));
			Assert.That(Parse("PROXY TCP4 240.0.0.1 198.51.100.7 1 1\r\n").Address, Is.EqualTo(IPAddress.Parse("240.0.0.1")));
			Assert.That(Parse("PROXY TCP4 0.0.0.1 198.51.100.7 1 1\r\n").Address, Is.EqualTo(IPAddress.Parse("0.0.0.1")));
			Assert.That(Parse("PROXY TCP6 ::1 ::1 1 1\r\n").Address, Is.EqualTo(IPAddress.IPv6Loopback));
			Assert.That(Parse("PROXY TCP4 192.0.2.10 0.0.0.0 1 1\r\n").Address, Is.EqualTo(IPAddress.Parse("192.0.2.10")), "only the source is checked");
		}

		// Each field is validated on its own; a control or non-ASCII byte must be refused in every one of them.
		[Test]
		public void RejectsControlAndNonAsciiBytesInEveryField()
		{
			const string Tcp4 = "PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234\r\n";
			const string Tcp6 = "PROXY TCP6 2001:db8::1 2001:db8::2 51234 1234\r\n";
			byte[] bad = [0x00, 0x01, 0x09, 0x0B, 0x0D, 0x1F, 0x7F, 0x80, 0xB2, 0xFF];
			foreach (var header in new[] { Tcp4, Tcp6 })
			{
				// Replace, and insert before, each character of the line after "PROXY " (the CRLF excluded).
				for (var i = 6; i < header.Length - 2; i++)
				{
					foreach (var b in bad)
					{
						var replaced = Ascii(header);
						replaced[i] = b;
						Assert.That(ProxyProtocol.TryParseHeader(replaced, out _), Is.False, $"0x{b:X2} at {i} of {header.Trim()}");

						byte[] inserted = [.. Ascii(header[..i]), b, .. Ascii(header[i..])];
						Assert.That(ProxyProtocol.TryParseHeader(inserted, out _), Is.False, $"0x{b:X2} before {i} of {header.Trim()}");
					}
				}
			}
		}

		[Test]
		public void RejectsBinaryVersion2Header()
		{
			byte[] v2 = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A, 0x21, 0x11, 0x00, 0x0C];
			Assert.That(ProxyProtocol.TryParseHeader(v2, out _), Is.False);
			Assert.That(ReadHeader(new MemoryStream(v2)), Is.Null);
		}

		static byte[] ReadHeader(Stream stream, int timeoutMs = 5000)
		{
			using var timeout = new CancellationTokenSource(timeoutMs);
			return ProxyProtocol.ReadHeaderAsync(stream, timeout.Token).GetAwaiter().GetResult();
		}

		[Test]
		public void ReadsOnlyTheHeader()
		{
			const string Header = "PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234\r\n";
			byte[] game = [0x0D, 0x00, 0x00, 0x00, 0x0A, 0x0D, 0x0A, 0xFF];
			var stream = new MemoryStream([.. Ascii(Header), .. game]);

			Assert.That(ReadHeader(stream), Is.EqualTo(Ascii(Header)));
			Assert.That(stream.Position, Is.EqualTo(Header.Length), "the game data after the CRLF must stay unread");
		}

		[Test]
		public void ReadsAHeaderSplitIntoSingleBytes()
		{
			const string Header = "PROXY TCP6 2001:db8::1 2001:db8::2 51234 1234\r\n";
			var stream = new TrickleStream(Ascii(Header + "game data"));
			Assert.That(ReadHeader(stream), Is.EqualTo(Ascii(Header)));
		}

		[Test]
		public void StopsAtTheFirstByteThatIsNotTheSignature()
		{
			// A client that sends game data straight away (no relay in between) is refused at once,
			// without waiting for 107 bytes or the timeout.
			var stream = new MemoryStream([0x0D, 0x00, 0x00, 0x00, .. Ascii("PROXY TCP4 192.0.2.10 198.51.100.7 1 1\r\n")]);
			Assert.That(ReadHeader(stream), Is.Null);
			Assert.That(stream.Position, Is.EqualTo(1));
		}

		[Test]
		public void StopsAtALineFeedWithoutCarriageReturn()
		{
			var stream = new MemoryStream(Ascii("PROXY TCP4 192.0.2.10 198.51.100.7 51234 1234\n\r\n"));
			Assert.That(ReadHeader(stream), Is.Null);
			Assert.That(stream.Position, Is.EqualTo(46), "gives up at the bare LF instead of waiting for a CRLF or the timeout");
		}

		[Test]
		public void GivesUpWithoutCrlfWithin107Bytes()
		{
			var stream = new MemoryStream(Ascii("PROXY " + new string('1', 300)));
			Assert.That(ReadHeader(stream), Is.Null);
			Assert.That(stream.Position, Is.EqualTo(107));
		}

		[Test]
		public void ReturnsNullWhenTheStreamEnds()
		{
			Assert.That(ReadHeader(new MemoryStream(Ascii("PROXY TCP4 192.0.2.10"))), Is.Null);
			Assert.That(ReadHeader(new MemoryStream([])), Is.Null);
		}

		[Test]
		public void TimesOutOnASilentPeer()
		{
			var stream = new TrickleStream(Ascii("PROXY TCP4 "), hangAtEnd: true);
			var started = DateTime.UtcNow;
			Assert.That(() => ReadHeader(stream, 200), Throws.InstanceOf<OperationCanceledException>());
			Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)));
		}

		[TestCase("127.0.0.1", "127.0.0.1", true)]
		[TestCase("127.0.0.1", "127.0.0.2", false)]
		[TestCase("127.0.0.1", "::ffff:127.0.0.1", true)]
		[TestCase("::ffff:127.0.0.1", "127.0.0.1", true)]
		[TestCase("::1", "::1", true)]
		[TestCase("::1", "127.0.0.1", false)]
		[TestCase("10.0.0.0/8", "10.200.3.4", true)]
		[TestCase("10.0.0.0/8", "11.0.0.1", false)]
		[TestCase("2001:db8::/32", "2001:db8:ffff::1", true)]
		[TestCase("2001:db8::/32", "2001:db9::1", false)]
		[TestCase("0.0.0.0/0", "198.51.100.7", true)]
		[TestCase("0.0.0.0/0", "2001:db8::1", false)]
		[TestCase("::/0", "2001:db8::1", true)]
		[TestCase("::/0", "::ffff:198.51.100.7", false)] // an IPv4 peer, even IPv4-mapped, only matches IPv4 entries
		[TestCase("::/0", "198.51.100.7", false)]
		public void MatchesTrustedProxies(string entry, string peer, bool trusted)
		{
			var networks = ProxyProtocol.ParseTrustedProxies([entry]);
			var endPoint = new IPEndPoint(IPAddress.Parse(peer), 40000);
			Assert.That(ProxyProtocol.IsTrusted(networks, endPoint), Is.EqualTo(trusted));
		}

		[Test]
		public void TrustsNobodyByDefault()
		{
			var networks = ProxyProtocol.ParseTrustedProxies([]);
			Assert.That(networks, Is.Empty);
			Assert.That(ProxyProtocol.IsTrusted(networks, new IPEndPoint(IPAddress.Loopback, 40000)), Is.False);
			Assert.That(ProxyProtocol.IsTrusted(ProxyProtocol.ParseTrustedProxies(["127.0.0.1"]), new DnsEndPoint("localhost", 40000)), Is.False);
		}

		[TestCase("localhost")]
		[TestCase("1")]
		[TestCase("127.1")]
		[TestCase("10.0.0.1/8")]
		[TestCase("10.0.0.0/33")]
		[TestCase("10.0.0.0/-1")]
		[TestCase("10.0.0.0/")]
		[TestCase("10.0.0.0/+8")]
		[TestCase("10.0.0.0/08")]
		[TestCase("10.0.0.0/8 ")]
		[TestCase("10.0.0.0/ 8")]
		[TestCase("0.0.0.0/-0")]
		[TestCase("0.0.0.0/00")]
		[TestCase("10.0.0.0/\u0668")] // ARABIC-INDIC DIGIT EIGHT
		[TestCase("2001:db8::/032")]
		[TestCase("2001:db8::/129")]
		[TestCase("fe80::1%eth0")]
		[TestCase("")]
		public void RejectsInvalidTrustedProxies(string entry)
		{
			Assert.That(() => ProxyProtocol.ParseTrustedProxies([entry]), Throws.ArgumentException);
		}

		[TestCase("10.0.0.0/8", "10.255.0.1", true)]
		[TestCase("0.0.0.0/0", "198.51.100.7", true)]
		[TestCase("127.0.0.1/32", "127.0.0.1", true)]
		[TestCase("2001:db8::/32", "2001:db8::1", true)]
		[TestCase("::1/128", "::1", true)]
		public void AcceptsPlainPrefixLengths(string entry, string peer, bool trusted)
		{
			var networks = ProxyProtocol.ParseTrustedProxies([entry]);
			Assert.That(ProxyProtocol.IsTrusted(networks, new IPEndPoint(IPAddress.Parse(peer), 1)), Is.EqualTo(trusted));
		}

		// The server's accept path for a trusted peer, on real loopback sockets: the listener hands the
		// accepted socket to ReadClientEndPointAsync, and only a valid header lets the connection in.
		static async Task<(EndPoint Client, string Error, byte[] Remaining, EndPoint Proxy)> AcceptFromProxy(byte[] sent, bool close, int timeoutMs = 5000)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try
			{
				using var proxy = new TcpClient();
				await proxy.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
				using var accepted = await listener.AcceptSocketAsync();
				await proxy.GetStream().WriteAsync(sent);
				if (close)
					proxy.Client.Shutdown(SocketShutdown.Send);

				var (client, error) = await ProxyProtocol.ReadClientEndPointAsync(accepted, TimeSpan.FromMilliseconds(timeoutMs));

				// What the game protocol will read next on that socket.
				var rest = new List<byte>();
				var buffer = new byte[256];
				accepted.ReceiveTimeout = 500;
				try
				{
					int read;
					while (rest.Count < sent.Length && (read = accepted.Receive(buffer)) > 0)
						rest.AddRange(buffer.AsSpan(0, read));
				}
				catch (SocketException)
				{
					// Nothing more within the timeout
				}

				return (client, error, rest.ToArray(), accepted.RemoteEndPoint);
			}
			finally
			{
				listener.Stop();
			}
		}

		[Test]
		public async Task AcceptPathUsesTheHeaderAndLeavesTheGameData()
		{
			byte[] game = [0x0D, 0x00, 0x00, 0x00, 0x0A, 0x0D, 0x0A, 0xFF];
			var (client, error, rest, _) = await AcceptFromProxy([.. Ascii("PROXY TCP4 198.51.100.4 127.0.0.1 51234 1234\r\n"), .. game], true);
			Assert.That(error, Is.Null);
			Assert.That(client, Is.EqualTo(new IPEndPoint(IPAddress.Parse("198.51.100.4"), 51234)));
			Assert.That(rest, Is.EqualTo(game));
		}

		[Test]
		public async Task AcceptPathKeepsTheProxyAddressForUnknown()
		{
			var (client, error, rest, proxy) = await AcceptFromProxy(Ascii("PROXY UNKNOWN\r\nGAME"), true);
			Assert.That(error, Is.Null);
			Assert.That(client, Is.EqualTo(proxy));
			Assert.That(rest, Is.EqualTo(Ascii("GAME")));
		}

		[TestCase("GAME DATA", TestName = "AcceptPathRefusesGameDataFromAProxy")]
		[TestCase("PROXY TCP4 192.0.2.10 198.51.100.7 051234 1234\r\n", TestName = "AcceptPathRefusesAMalformedHeader")]
		[TestCase("PROXY TCP4 0.0.0.0 198.51.100.7 1 1234\r\n", TestName = "AcceptPathRefusesAnUnspecifiedSource")]
		[TestCase("PROXY TCP4 192.0.2.10", TestName = "AcceptPathRefusesAHeaderCutShort")]
		public async Task AcceptPathRefuses(string sent)
		{
			var started = DateTime.UtcNow;
			var (client, error, _, _) = await AcceptFromProxy(Ascii(sent), true);
			Assert.That(client, Is.Null);
			Assert.That(error, Is.EqualTo("missing or malformed PROXY protocol header"));
			Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(3)), "refused without waiting for the timeout");
		}

		[Test]
		public async Task AcceptPathTimesOutOnASilentProxy()
		{
			var started = DateTime.UtcNow;
			var (client, error, _, _) = await AcceptFromProxy(Ascii("PROXY TCP4 "), false, 300);
			Assert.That(client, Is.Null);
			Assert.That(error, Is.EqualTo("timed out waiting for the PROXY protocol header"));
			Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(4)));
		}

		[Test]
		public async Task AcceptPathReportsAClosedSocket()
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try
			{
				using var proxy = new TcpClient();
				await proxy.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
				var accepted = await listener.AcceptSocketAsync();
				accepted.Dispose();
				var (client, error) = await ProxyProtocol.ReadClientEndPointAsync(accepted, TimeSpan.FromSeconds(1));
				Assert.That(client, Is.Null);
				Assert.That(error, Does.StartWith("socket error while reading the PROXY protocol header: "));
			}
			finally
			{
				listener.Stop();
			}
		}

		// Returns one byte per read, asynchronously, like a header arriving in small TCP segments.
		// With hangAtEnd, a read after the last byte waits until it is cancelled (a silent peer).
		sealed class TrickleStream : Stream
		{
			readonly byte[] data;
			readonly bool hangAtEnd;
			int position;

			public TrickleStream(byte[] data, bool hangAtEnd = false)
			{
				this.data = data;
				this.hangAtEnd = hangAtEnd;
			}

			public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
			{
				await Task.Yield();
				if (position == data.Length)
				{
					if (hangAtEnd)
						await Task.Delay(Timeout.Infinite, cancellationToken);

					return 0;
				}

				buffer.Span[0] = data[position++];
				return 1;
			}

			public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => throw new NotSupportedException();
			public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
			public override void Flush() { }
			public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}
}
