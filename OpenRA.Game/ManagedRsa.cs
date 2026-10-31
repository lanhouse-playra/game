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
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OpenRA
{
	/// <summary>
	/// RSA in managed code, for the browser build: System.Security.Cryptography.RSA is not supported on
	/// browser-wasm. It does what the player profile needs, with the same results as RSACryptoServiceProvider:
	/// key generation (2048 bits, e = 65537), RSASSA-PKCS1-v1_5 signatures over SHA-1 (what CryptoUtil.Sign
	/// makes and CryptoUtil.VerifySignature checks) and the key file format of RSA.ToXmlString.
	/// Signing is synchronous and deterministic (PKCS#1 v1.5 has no randomness), blinded against timing
	/// attacks and checked against the public key before it is returned.
	/// </summary>
	public static class ManagedRsa
	{
		public const int PublicExponent = 65537;

		// Miller-Rabin rounds for each prime: more than the 5 FIPS 186-5 (table B.1) asks for 1024-bit primes
		// of a 2048-bit key at an error probability of 2^-100.
		const int MillerRabinRounds = 8;

		// DER DigestInfo prefix for SHA-1 (RFC 8017 section 9.2, note 1).
		static readonly byte[] Sha1DigestInfo = [0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e, 0x03, 0x02, 0x1a, 0x05, 0x00, 0x04, 0x14];

		// Odd primes below 2048, for trial division of prime candidates.
		static readonly int[] SmallPrimes = OddPrimesBelow(2048);

		static int[] OddPrimesBelow(int limit)
		{
			var composite = new bool[limit];
			var primes = new List<int>();
			for (var i = 3; i < limit; i += 2)
			{
				if (composite[i])
					continue;

				primes.Add(i);
				for (var j = i * i; j < limit; j += 2 * i)
					composite[j] = true;
			}

			return primes.ToArray();
		}

		static BigInteger FromBytes(ReadOnlySpan<byte> data) => new(data, isUnsigned: true, isBigEndian: true);

		static byte[] ToBytes(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

		// Big-endian, left-padded with zeros to length bytes (RSAParameters wants fixed lengths).
		static byte[] ToBytes(BigInteger value, int length)
		{
			var data = ToBytes(value);
			if (data.Length > length)
				throw new CryptographicException("Value does not fit in the requested length.");

			if (data.Length == length)
				return data;

			var padded = new byte[length];
			data.CopyTo(padded, length - data.Length);
			return padded;
		}

		static BigInteger RandomBits(int bits)
		{
			var data = new byte[(bits + 7) / 8];
			RandomNumberGenerator.Fill(data);
			var excess = data.Length * 8 - bits;
			data[0] &= (byte)(0xFF >> excess);
			return FromBytes(data);
		}

		// Uniform in [2, n - 2].
		static BigInteger RandomBase(BigInteger n)
		{
			var bits = (int)(n - 1).GetBitLength();
			while (true)
			{
				var a = RandomBits(bits);
				if (a >= 2 && a <= n - 2)
					return a;
			}
		}

		static BigInteger ModInverse(BigInteger a, BigInteger m)
		{
			BigInteger r0 = m, r1 = a % m;
			BigInteger t0 = 0, t1 = 1;
			if (r1.Sign < 0)
				r1 += m;

			while (!r1.IsZero)
			{
				var quotient = BigInteger.DivRem(r0, r1, out var remainder);
				(r0, r1) = (r1, remainder);
				(t0, t1) = (t1, t0 - quotient * t1);
			}

			if (!r0.IsOne)
				throw new ArithmeticException("Value has no modular inverse.");

			return t0.Sign < 0 ? t0 + m : t0;
		}

		/// <summary>One Miller-Rabin round with a random base: false means n is certainly composite.</summary>
		static bool MillerRabinRound(BigInteger n, BigInteger d, int s)
		{
			var nMinusOne = n - 1;
			var x = BigInteger.ModPow(RandomBase(n), d, n);
			if (x.IsOne || x == nMinusOne)
				return true;

			for (var i = 1; i < s; i++)
			{
				x = BigInteger.ModPow(x, 2, n);
				if (x == nMinusOne)
					return true;

				if (x.IsOne)
					return false;
			}

			return false;
		}

		/// <summary>
		/// Generates an RSA key in small steps, so that a single-threaded host (the browser) can keep drawing
		/// frames in between: call <see cref="Step"/> until it returns true, then read <see cref="Result"/>.
		/// Each step is at most one modular exponentiation of a candidate prime (or the trial division of a
		/// new batch of candidates).
		/// </summary>
		public sealed class KeyGenerator
		{
			// The candidate batch: start + 2i for the i not ruled out by trial division.
			const int BatchSize = 4096;

			readonly int bits;
			readonly int primeBits;
			readonly BigInteger e = PublicExponent;

			BigInteger start;
			readonly bool[] ruledOut = new bool[BatchSize];
			int next = BatchSize;

			// The candidate under Miller-Rabin.
			BigInteger candidate, candidateD;
			int candidateS, roundsPassed = -1;

			BigInteger p;
			RSAParameters? result;

			public KeyGenerator(int bits)
			{
				if (bits < 1024 || bits % 16 != 0)
					throw new ArgumentOutOfRangeException(nameof(bits), "The key size must be a multiple of 16, at least 1024.");

				this.bits = bits;
				primeBits = bits / 2;
			}

			public RSAParameters Result => result ?? throw new InvalidOperationException("The key is not generated yet.");

			public bool Step()
			{
				if (result != null)
					return true;

				if (roundsPassed < 0)
				{
					NextCandidate();
					return false;
				}

				if (!MillerRabinRound(candidate, candidateD, candidateS))
				{
					roundsPassed = -1;
					return false;
				}

				if (++roundsPassed < MillerRabinRounds)
					return false;

				// candidate is prime.
				roundsPassed = -1;
				if (p.IsZero)
				{
					p = candidate;
					return false;
				}

				if (TryFinish(p, candidate))
					return true;

				// Too close to p (or the modulus came out a bit short): look for another q.
				return false;
			}

			// Takes the next candidate that survives trial division, starting a new batch when this one is used up.
			// Each one is odd, has its two top bits set (so that the product of two has exactly bits bits) and is
			// not 1 mod e (so that e is invertible mod p - 1).
			void NextCandidate()
			{
				while (next < BatchSize && ruledOut[next])
					next++;

				if (next >= BatchSize)
				{
					NewBatch();
					return;
				}

				var c = start + 2 * next++;
				if (c.GetBitLength() != primeBits || (c % e).IsOne)
					return;

				candidate = c;
				candidateD = c - 1;
				candidateS = 0;
				while (candidateD.IsEven)
				{
					candidateD >>= 1;
					candidateS++;
				}

				roundsPassed = 0;
			}

			void NewBatch()
			{
				start = RandomBits(primeBits) | (BigInteger.One << (primeBits - 1)) | (BigInteger.One << (primeBits - 2)) | BigInteger.One;
				Array.Clear(ruledOut);
				foreach (var prime in SmallPrimes)
				{
					// Offsets i with start + 2i = 0 (mod prime): i = -start / 2 (mod prime).
					var r = (int)(start % prime);
					var inverseOfTwo = (prime + 1) / 2;
					var first = (int)((long)(prime - r) % prime * inverseOfTwo % prime);
					for (var i = first; i < BatchSize; i += prime)
						ruledOut[i] = true;
				}

				next = 0;
			}

			bool TryFinish(BigInteger p, BigInteger q)
			{
				// FIPS 186-5 A.1.3: |p - q| > 2^(nlen/2 - 100).
				if (BigInteger.Abs(p - q).GetBitLength() <= primeBits - 100)
					return false;

				var n = p * q;
				if (n.GetBitLength() != bits)
					return false;

				var pMinusOne = p - 1;
				var qMinusOne = q - 1;
				var lambda = pMinusOne / BigInteger.GreatestCommonDivisor(pMinusOne, qMinusOne) * qMinusOne;
				var d = ModInverse(e, lambda);

				// FIPS 186-5 A.1.1: d > 2^(nlen/2). Practically always true; retry if not.
				if (d.GetBitLength() <= primeBits)
					return false;

				var modulusLength = bits / 8;
				var halfLength = modulusLength / 2;
				var parameters = new RSAParameters
				{
					Modulus = ToBytes(n, modulusLength),
					Exponent = ToBytes(e),
					D = ToBytes(d, modulusLength),
					P = ToBytes(p, halfLength),
					Q = ToBytes(q, halfLength),
					DP = ToBytes(d % pMinusOne, halfLength),
					DQ = ToBytes(d % qMinusOne, halfLength),
					InverseQ = ToBytes(ModInverse(q, p), halfLength),
				};

				// Check that the key works before it is kept.
				var probe = Encoding.ASCII.GetBytes("OpenRA key self-test");
				if (!VerifySha1(parameters, probe, SignSha1(parameters, probe)))
					throw new CryptographicException("The generated key failed its self-test.");

				result = parameters;
				return true;
			}
		}

		/// <summary>Generates a key in one call (tests, or hosts that may block).</summary>
		public static RSAParameters GenerateKey(int bits)
		{
			var generator = new KeyGenerator(bits);
			while (!generator.Step()) { }

			return generator.Result;
		}

		// EMSA-PKCS1-v1_5 encoding of a SHA-1 hash (RFC 8017 section 9.2): 00 01 FF..FF 00 DigestInfo hash.
		static byte[] EncodeSha1(ReadOnlySpan<byte> hash, int length)
		{
			var tLength = Sha1DigestInfo.Length + hash.Length;
			if (length < tLength + 11)
				throw new CryptographicException("The key is too short.");

			var em = new byte[length];
			em[1] = 0x01;
			em.AsSpan(2, length - tLength - 3).Fill(0xFF);
			Sha1DigestInfo.CopyTo(em, length - tLength);
			hash.CopyTo(em.AsSpan(length - hash.Length));
			return em;
		}

		/// <summary>
		/// RSASSA-PKCS1-v1_5 signature of SHA-1(<paramref name="data"/>), as
		/// <c>rsa.SignHash(SHA1.HashData(data), "SHA1")</c> makes it: big-endian, as long as the modulus.
		/// </summary>
		public static byte[] SignSha1(RSAParameters key, byte[] data)
		{
			var length = key.Modulus.Length;
			var n = FromBytes(key.Modulus);
			var e = FromBytes(key.Exponent);
			var m = FromBytes(EncodeSha1(SHA1.HashData(data), length));
			if (m >= n)
				throw new CryptographicException("Invalid key.");

			var s = PrivateOperation(key, n, e, m);

			// A fault in the private operation could reveal the key (Boneh-DeMillo-Lipton): never return a
			// signature that does not verify.
			if (BigInteger.ModPow(s, e, n) != m)
				throw new CryptographicException("The signature failed its self-check.");

			return ToBytes(s, length);
		}

		// m^d mod n with the CRT, blinded: the exponentiations see m * r^e for a fresh random r, so how long
		// they take says nothing about m (BigInteger.ModPow is not constant-time).
		static BigInteger PrivateOperation(RSAParameters key, BigInteger n, BigInteger e, BigInteger m)
		{
			BigInteger r, rInverse;
			while (true)
			{
				r = RandomBits((int)n.GetBitLength() - 1);
				if (r < 2 || !BigInteger.GreatestCommonDivisor(r, n).IsOne)
					continue;

				rInverse = ModInverse(r, n);
				break;
			}

			var blinded = m * BigInteger.ModPow(r, e, n) % n;
			BigInteger s;
			if (key.P != null && key.Q != null && key.DP != null && key.DQ != null && key.InverseQ != null)
			{
				var p = FromBytes(key.P);
				var q = FromBytes(key.Q);
				var m1 = BigInteger.ModPow(blinded % p, FromBytes(key.DP), p);
				var m2 = BigInteger.ModPow(blinded % q, FromBytes(key.DQ), q);
				var h = FromBytes(key.InverseQ) * (m1 - m2) % p;
				if (h.Sign < 0)
					h += p;

				s = m2 + h * q;
			}
			else if (key.D != null)
				s = BigInteger.ModPow(blinded, FromBytes(key.D), n);
			else
				throw new CryptographicException("The key has no private part.");

			return s * rInverse % n;
		}

		/// <summary>Checks an RSASSA-PKCS1-v1_5 signature of SHA-1(<paramref name="data"/>).</summary>
		public static bool VerifySha1(RSAParameters key, byte[] data, byte[] signature)
		{
			var length = key.Modulus.Length;
			if (signature == null || signature.Length != length)
				return false;

			var n = FromBytes(key.Modulus);
			var s = FromBytes(signature);
			if (s >= n)
				return false;

			var em = ToBytes(BigInteger.ModPow(s, FromBytes(key.Exponent), n), length);
			return CryptographicOperations.FixedTimeEquals(em, EncodeSha1(SHA1.HashData(data), length));
		}

		static readonly string[] PrivateElements = ["P", "Q", "DP", "DQ", "InverseQ", "D"];

		/// <summary>The key as <c>RSA.ToXmlString</c> writes it (the format of the player profile's key file).</summary>
		public static string ToXmlString(RSAParameters key, bool includePrivateParameters)
		{
			var output = new StringBuilder("<RSAKeyValue>");
			void Element(string name, byte[] value)
			{
				if (value == null)
					throw new CryptographicException($"The key has no {name}.");

				output.Append('<').Append(name).Append('>').Append(Convert.ToBase64String(value)).Append("</").Append(name).Append('>');
			}

			Element("Modulus", key.Modulus);
			Element("Exponent", key.Exponent);
			if (includePrivateParameters)
			{
				Element("P", key.P);
				Element("Q", key.Q);
				Element("DP", key.DP);
				Element("DQ", key.DQ);
				Element("InverseQ", key.InverseQ);
				Element("D", key.D);
			}

			return output.Append("</RSAKeyValue>").ToString();
		}

		/// <summary>
		/// Reads a key written by <c>RSA.ToXmlString</c> (or <see cref="ToXmlString"/>): an RSAKeyValue element
		/// with base64 children, either the public part only or all of the private part too.
		/// </summary>
		public static RSAParameters FromXmlString(string xml)
		{
			var body = Inner(xml, "RSAKeyValue") ?? throw new CryptographicException("Not an RSAKeyValue element.");
			byte[] Element(string name)
			{
				var value = Inner(body, name);
				return value == null ? null : Convert.FromBase64String(value);
			}

			var key = new RSAParameters
			{
				Modulus = Element("Modulus") ?? throw new CryptographicException("The key has no Modulus."),
				Exponent = Element("Exponent") ?? throw new CryptographicException("The key has no Exponent."),
				P = Element("P"),
				Q = Element("Q"),
				DP = Element("DP"),
				DQ = Element("DQ"),
				InverseQ = Element("InverseQ"),
				D = Element("D"),
			};

			var present = 0;
			foreach (var name in PrivateElements)
				if (Inner(body, name) != null)
					present++;

			if (present != 0 && present != PrivateElements.Length)
				throw new CryptographicException("The key's private part is incomplete.");

			return key;
		}

		// The text between <name> and </name> (trimmed), or null. The key file has no attributes, comments or
		// nesting beyond RSAKeyValue, so a plain search is enough.
		static string Inner(string xml, string name)
		{
			var open = "<" + name + ">";
			var close = "</" + name + ">";
			var begin = xml.IndexOf(open, StringComparison.Ordinal);
			if (begin < 0)
				return null;

			begin += open.Length;
			var end = xml.IndexOf(close, begin, StringComparison.Ordinal);
			if (end < 0)
				throw new CryptographicException($"Unterminated {name} element.");

			return xml[begin..end].Trim();
		}
	}
}
