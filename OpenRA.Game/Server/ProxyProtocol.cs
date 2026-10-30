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

namespace OpenRA.Server
{
	/// <summary>
	/// Receiving side of the PROXY protocol, version 1 (the text header), as specified in
	/// https://www.haproxy.org/download/3.2/doc/proxy-protocol.txt.
	/// A relay that connects to the server on behalf of a client that cannot open a TCP connection
	/// itself (for example a WebSocket bridge for browser clients) sends one header line naming the
	/// client's address before any game data. The server only reads it from the peers listed in
	/// <see cref="ServerSettings.TrustedProxies"/>, so that bans and GeoIP see each client instead of the relay.
	/// </summary>
	public static class ProxyProtocol
	{
		/// <summary>Longest header a receiver must accept, CRLF included (104 for TCP6, 107 for UNKNOWN).</summary>
		public const int MaxHeaderLength = 107;

		/// <summary>How long a trusted proxy may take to send the header after connecting.</summary>
		public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(5);

		static ReadOnlySpan<byte> Signature => "PROXY "u8;

		/// <summary>
		/// Parses the TrustedProxies setting: IP addresses (a single host) or networks in CIDR notation.
		/// Throws <see cref="ArgumentException"/> for any other entry, so that a typo cannot silently
		/// trust (or fail to trust) a peer.
		/// </summary>
		public static IPNetwork[] ParseTrustedProxies(IEnumerable<string> entries)
		{
			var networks = new List<IPNetwork>();
			foreach (var entry in entries)
			{
				var slash = entry.IndexOf('/');
				var addressText = slash < 0 ? entry : entry[..slash];
				var family = addressText.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
				if (!TryParseAddress(addressText, family, out var address))
					throw new ArgumentException($"Trusted proxy `{entry}` is not an IP address or a CIDR network.");

				address = Normalize(address);
				var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
				var prefix = maxPrefix;
				if (slash >= 0 && !OpenRA.Exts.TryParseInt32Invariant(entry.AsSpan(slash + 1), out prefix))
					throw new ArgumentException($"Trusted proxy `{entry}` has an invalid prefix length.");

				if (prefix < 0 || prefix > maxPrefix)
					throw new ArgumentException($"Trusted proxy `{entry}` has an invalid prefix length.");

				// Refuse "10.0.0.1/8": it is ambiguous whether a single host or the network was meant.
				var bytes = address.GetAddressBytes();
				for (var bit = prefix; bit < maxPrefix; bit++)
					if ((bytes[bit / 8] & (0x80 >> (bit % 8))) != 0)
						throw new ArgumentException($"Trusted proxy `{entry}` has address bits set after its prefix length.");

				networks.Add(new IPNetwork(address, prefix));
			}

			return networks.ToArray();
		}

		/// <summary>
		/// Reads and validates the header on a connection accepted from a trusted proxy, leaving any
		/// byte after it unread. Returns the client's end point (the proxy's own for UNKNOWN), or null
		/// and the reason when the header is missing, malformed or late, or the socket fails.
		/// </summary>
		public static async Task<(EndPoint Client, string Error)> ReadClientEndPointAsync(Socket socket, TimeSpan timeout)
		{
			try
			{
				var proxy = socket.RemoteEndPoint;
				using var cancellation = new CancellationTokenSource(timeout);
				await using var stream = new NetworkStream(socket, false);
				var header = await ReadHeaderAsync(stream, cancellation.Token);
				if (header != null && TryParseHeader(header, out var source))
					return (source ?? proxy, null);

				return (null, "missing or malformed PROXY protocol header");
			}
			catch (OperationCanceledException)
			{
				return (null, "timed out waiting for the PROXY protocol header");
			}
			catch (Exception e) when (e is IOException || e is SocketException || e is ObjectDisposedException)
			{
				return (null, $"socket error while reading the PROXY protocol header: {e.Message}");
			}
		}

		public static bool IsTrusted(IReadOnlyCollection<IPNetwork> trustedProxies, EndPoint peer)
		{
			if (trustedProxies.Count == 0 || peer is not IPEndPoint ipPeer)
				return false;

			var address = Normalize(ipPeer.Address);
			foreach (var network in trustedProxies)
				if (network.Contains(address))
					return true;

			return false;
		}

		/// <summary>
		/// Reads one header line, CRLF included, without consuming any byte that follows it.
		/// Returns null if the stream ends, the data does not start with the PROXY signature, a LF comes
		/// without a CR, or no CRLF arrives within <see cref="MaxHeaderLength"/> bytes. Cancel the token to time out.
		/// </summary>
		public static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken token)
		{
			// One byte per read: the header has no length prefix, and anything after the CRLF
			// belongs to the game protocol. It is read once per connection, from trusted peers only.
			var buffer = new byte[MaxHeaderLength];
			for (var length = 0; length < MaxHeaderLength; length++)
			{
				if (await stream.ReadAsync(buffer.AsMemory(length, 1), token) == 0)
					return null;

				// Give up on the first byte that cannot be part of a header (e.g. a v2 binary header
				// or game data), instead of waiting for more.
				if (length < Signature.Length && buffer[length] != Signature[length])
					return null;

				// The header ends at the first LF, which must follow a CR.
				if (buffer[length] == '\n')
					return length > 0 && buffer[length - 1] == '\r' ? buffer.AsSpan(0, length + 1).ToArray() : null;
			}

			return null;
		}

		/// <summary>
		/// Validates a complete header line (CRLF included). The source is the client's address for
		/// TCP4 and TCP6 headers, and null for UNKNOWN, where the connection's own address must be used.
		/// </summary>
		public static bool TryParseHeader(ReadOnlySpan<byte> header, out IPEndPoint source)
		{
			source = null;
			if (header.Length > MaxHeaderLength || !header.StartsWith(Signature) || !header.EndsWith("\r\n"u8))
				return false;

			var line = header[Signature.Length..^2];

			// The specification tells receivers to ignore anything between UNKNOWN and the CRLF.
			if (line.SequenceEqual("UNKNOWN"u8) || line.StartsWith("UNKNOWN "u8))
				return true;

			foreach (var b in line)
				if (b < 0x20 || b > 0x7E)
					return false;

			var fields = Encoding.ASCII.GetString(line).Split(' ');
			if (fields.Length != 5)
				return false;

			AddressFamily family;
			if (fields[0] == "TCP4")
				family = AddressFamily.InterNetwork;
			else if (fields[0] == "TCP6")
				family = AddressFamily.InterNetworkV6;
			else
				return false;

			if (!TryParseAddress(fields[1], family, out var sourceAddress) || !TryParseAddress(fields[2], family, out _) ||
					!TryParsePort(fields[3], out var sourcePort) || !TryParsePort(fields[4], out _))
				return false;

			source = new IPEndPoint(Normalize(sourceAddress), sourcePort);
			return true;
		}

		/// <summary>Only the plain textual forms: dotted quad for IPv4, hex and colons (optionally an embedded dotted quad) for IPv6.</summary>
		static bool TryParseAddress(string text, AddressFamily family, out IPAddress address)
		{
			address = null;
			if (text.Length == 0 || !IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != family)
				return false;

			if (family == AddressFamily.InterNetwork)
			{
				// IPAddress.TryParse also accepts "1", "0x7f.1" and leading zeros: allow only the canonical form.
				if (parsed.ToString() != text)
					return false;
			}
			else
			{
				foreach (var c in text)
					if (!char.IsAsciiHexDigit(c) && c != ':' && c != '.')
						return false;
			}

			address = parsed;
			return true;
		}

		static bool TryParsePort(string text, out int port)
		{
			port = 0;
			if (text.Length == 0 || text.Length > 5 || (text.Length > 1 && text[0] == '0'))
				return false;

			foreach (var c in text)
			{
				if (!char.IsAsciiDigit(c))
					return false;

				port = port * 10 + (c - '0');
			}

			return port <= IPEndPoint.MaxPort;
		}

		static IPAddress Normalize(IPAddress address)
		{
			return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
		}
	}
}
