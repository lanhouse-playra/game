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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace OpenRA.Test
{
	// The browser build signs with ManagedRsa; servers verify with RSACryptoServiceProvider (CryptoUtil.VerifySignature).
	// These tests check that the two agree, in both directions, and that keys made by ManagedRsa work everywhere the
	// desktop key does (key file, PEM, fingerprint).
	[TestFixture]
	sealed class ManagedRsaTest
	{
		const int Inputs = 100;

		// Generated once: 2048-bit generation takes a moment.
		static readonly Lazy<RSAParameters[]> ManagedKeys = new(() => Enumerable.Range(0, 3).Select(_ => ManagedRsa.GenerateKey(2048)).ToArray());

		static RSAParameters DesktopKey()
		{
			using (var rsa = new RSACryptoServiceProvider(2048))
				return rsa.ExportParameters(true);
		}

		static BigInteger B(byte[] value) => new(value, isUnsigned: true, isBigEndian: true);

		// The same value with its lowest bit flipped.
		static byte[] Tampered(byte[] value)
		{
			var tampered = (byte[])value.Clone();
			tampered[^1] ^= 1;
			return tampered;
		}

		// The first prime from start up that the key generator could use (p - 1 not a multiple of e).
		static BigInteger NextKeyPrime(BigInteger start)
		{
			var c = start | BigInteger.One;
			while (!ManagedRsa.IsProbablePrime(c) || (c % ManagedRsa.PublicExponent).IsOne)
				c += 2;

			return c;
		}

		static byte[] RandomInput(Random random)
		{
			var data = new byte[random.Next(0, 300)];
			random.NextBytes(data);
			return data;
		}

		// A string like the server's AuthToken (Convert.ToBase64String of random bytes), sometimes with non-ASCII text.
		static string RandomToken(Random random)
		{
			var token = Convert.ToBase64String(RandomInput(random));
			return random.Next(4) == 0 ? token + "é✓" : token;
		}

		static bool DesktopVerify(RSAParameters key, byte[] data, byte[] signature)
		{
			using (var rsa = new RSACryptoServiceProvider())
			{
				rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
				return rsa.VerifyHash(SHA1.HashData(data), CryptoConfig.MapNameToOID("SHA1"), signature);
			}
		}

		[Test]
		public void ManagedSignaturesVerifyOnDesktop()
		{
			var random = new Random(1);
			foreach (var key in ManagedKeys.Value)
			{
				for (var i = 0; i < Inputs; i++)
				{
					var token = RandomToken(random);
					var signature = Convert.ToBase64String(ManagedRsa.SignSha1(key, Encoding.UTF8.GetBytes(token)));

					// Exactly what Server.cs does with the handshake's AuthSignature.
					Assert.That(CryptoUtil.VerifySignature(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent }, token, signature), Is.True);
				}
			}
		}

		[Test]
		public void SignaturesAreIdenticalToDesktop()
		{
			// PKCS#1 v1.5 is deterministic: same key and data, same bytes. Both on desktop keys and on managed keys.
			var random = new Random(2);
			var keys = ManagedKeys.Value.Append(DesktopKey()).Append(DesktopKey());
			foreach (var key in keys)
			{
				for (var i = 0; i < Inputs; i++)
				{
					var data = RandomInput(random);
					var managed = ManagedRsa.SignSha1(key, data);

					// CryptoUtil.Sign is the engine's desktop path (RSACryptoServiceProvider): its base64 text, too.
					Assert.That(Convert.ToBase64String(managed), Is.EqualTo(CryptoUtil.Sign(key, data)));
					Assert.That(managed, Has.Length.EqualTo(key.Modulus.Length));
				}
			}
		}

		[Test]
		public void DesktopSignaturesVerifyManaged()
		{
			var random = new Random(3);
			for (var k = 0; k < 2; k++)
			{
				var key = DesktopKey();
				for (var i = 0; i < Inputs; i++)
				{
					var data = RandomInput(random);
					var signature = Convert.FromBase64String(CryptoUtil.Sign(key, data));
					Assert.That(ManagedRsa.VerifySha1(key, data, signature), Is.True);
				}
			}
		}

		[Test]
		public void WrongSignaturesFail()
		{
			var random = new Random(4);
			var key = ManagedKeys.Value[0];
			var other = ManagedKeys.Value[1];
			for (var i = 0; i < 20; i++)
			{
				var data = RandomInput(random);
				var signature = ManagedRsa.SignSha1(key, data);

				var tampered = (byte[])signature.Clone();
				tampered[random.Next(tampered.Length)] ^= (byte)(1 << random.Next(8));
				Assert.That(DesktopVerify(key, data, tampered), Is.False);
				Assert.That(ManagedRsa.VerifySha1(key, data, tampered), Is.False);

				var otherData = data.Append((byte)1).ToArray();
				Assert.That(DesktopVerify(key, otherData, signature), Is.False);
				Assert.That(ManagedRsa.VerifySha1(key, otherData, signature), Is.False);

				Assert.That(DesktopVerify(other, data, signature), Is.False);
				Assert.That(ManagedRsa.VerifySha1(other, data, signature), Is.False);
				Assert.That(ManagedRsa.VerifySha1(key, data, signature[1..]), Is.False);
			}
		}

		[Test]
		public void GeneratedKeysAreValid()
		{
			foreach (var key in ManagedKeys.Value)
			{
				Assert.That(key.Modulus, Has.Length.EqualTo(256));
				Assert.That(key.Modulus[0] & 0x80, Is.Not.Zero, "the modulus has exactly 2048 bits");
				Assert.That(key.Exponent, Is.EqualTo(new byte[] { 1, 0, 1 }));
				Assert.That(key.D, Has.Length.EqualTo(256));
				foreach (var half in new[] { key.P, key.Q, key.DP, key.DQ, key.InverseQ })
					Assert.That(half, Has.Length.EqualTo(128));

				var n = B(key.Modulus);
				var p = B(key.P);
				var q = B(key.Q);
				var d = B(key.D);
				var e = B(key.Exponent);
				Assert.That(p * q, Is.EqualTo(n));
				Assert.That(p, Is.Not.EqualTo(q));
				Assert.That(BigInteger.Abs(p - q).GetBitLength(), Is.GreaterThan(1024 - 100), "|p - q| > 2^(nlen/2 - 100) (FIPS 186-5 A.1.3)");
				Assert.That(d * e % (p - 1), Is.EqualTo(BigInteger.One));
				Assert.That(d * e % (q - 1), Is.EqualTo(BigInteger.One));
				Assert.That(B(key.DP), Is.EqualTo(d % (p - 1)));
				Assert.That(B(key.DQ), Is.EqualTo(d % (q - 1)));
				Assert.That(B(key.InverseQ) * q % p, Is.EqualTo(BigInteger.One));

				// The desktop runtime accepts it as a private key and gets the same public key out of it.
				using (var rsa = new RSACryptoServiceProvider())
				{
					rsa.ImportParameters(key);
					Assert.That(rsa.KeySize, Is.EqualTo(2048));
					Assert.That(rsa.ExportParameters(false).Modulus, Is.EqualTo(key.Modulus));
				}
			}

			Assert.That(ManagedKeys.Value.Select(k => Convert.ToBase64String(k.Modulus)).Distinct().Count(), Is.EqualTo(ManagedKeys.Value.Length));
		}

		[Test]
		public void ClosePrimesAreRejected()
		{
			// FIPS 186-5 A.1.3 asks for |p - q| > 2^(nlen/2 - 100): Fermat's method factors n at once when p and q are
			// close. A key is made only from primes that are far enough apart.
			var key = ManagedKeys.Value[0];
			var p = B(key.P);
			var same = ManagedRsa.KeyGenerator.FromPrimes(2048, p, B(key.Q));
			Assert.That(same, Is.Not.Null);
			Assert.That(ManagedRsa.ToXmlString(same.Value, true), Is.EqualTo(ManagedRsa.ToXmlString(key, true)));

			Assert.That(ManagedRsa.KeyGenerator.FromPrimes(2048, p, NextKeyPrime(p + 2)), Is.Null);
			Assert.That(ManagedRsa.KeyGenerator.FromPrimes(2048, p, NextKeyPrime(p + (BigInteger.One << 923))), Is.Null);
			Assert.That(ManagedRsa.KeyGenerator.FromPrimes(2048, p, NextKeyPrime(p + (BigInteger.One << 924))), Is.Not.Null);
		}

		[Test]
		public void PrimeTestRejectsPseudoprimes()
		{
			// Strong pseudoprimes to the smallest bases: 2047 to base 2, 3215031751 to 2, 3, 5 and 7, 3825123056546413051 to
			// the primes up to 23, 318665857834031151167461 to the primes up to 37. Carmichael numbers, a product of two
			// Mersenne primes. A test with fixed small bases accepts some of them; random bases reject them all.
			var composites = new[]
			{
				2047, 3215031751, 3825123056546413051, BigInteger.Parse("318665857834031151167461", CultureInfo.InvariantCulture),
				561, 1105, 1729, 2465, 15841, ((BigInteger.One << 61) - 1) * ((BigInteger.One << 89) - 1),
			};

			foreach (var n in composites)
				for (var i = 0; i < 20; i++)
					Assert.That(ManagedRsa.IsProbablePrime(n), Is.False, $"{n} is composite");

			// One round lets a composite through with a probability of up to 1/4, so the number of rounds matters. These
			// pass a single round about 2.5%, 2.8% and 5.1% of the time: one round would let some of the 6000 tests through
			// for certain, while the key generator's rounds let one through with a probability of about 10^-7.
			var accepted = 0;
			foreach (var n in new BigInteger[] { 1105, 2465, 15841 })
				for (var i = 0; i < 2000; i++)
					if (ManagedRsa.IsProbablePrime(n))
						accepted++;

			Assert.That(accepted, Is.Zero, "composites accepted");

			var primes = new[]
			{
				2, 3, 5, 7, 65537, (BigInteger.One << 61) - 1, (BigInteger.One << 127) - 1, (BigInteger.One << 521) - 1,
				B(ManagedKeys.Value[0].P), B(ManagedKeys.Value[0].Q),
			};

			foreach (var n in primes)
				Assert.That(ManagedRsa.IsProbablePrime(n), Is.True, $"{n} is prime");

			Assert.That(ManagedRsa.IsProbablePrime(1), Is.False);
			Assert.That(ManagedRsa.IsProbablePrime(4), Is.False);
		}

		[Test]
		public void FaultySignaturesAreNeverReturned()
		{
			// A signature made with a wrong CRT parameter is right modulo one prime and wrong modulo the other, so
			// gcd(s^e - m, n) gives away a factor of n (Boneh-DeMillo-Lipton). Signing must throw instead.
			var key = ManagedKeys.Value[0];
			var data = Encoding.UTF8.GetBytes("token");
			var faulty = new[]
			{
				key with { DP = Tampered(key.DP) },
				key with { DQ = Tampered(key.DQ) },
				key with { InverseQ = Tampered(key.InverseQ) },
			};

			foreach (var k in faulty)
				Assert.Throws<CryptographicException>(() => ManagedRsa.SignSha1(k, data));

			Assert.That(ManagedRsa.VerifySha1(key, data, ManagedRsa.SignSha1(key, data)), Is.True);
		}

		[Test]
		public void PrivateOperationIsBlinded()
		{
			// Blinding cannot be seen in a correct result: (m * r^e)^d * r^-1 = m^d for every r. It shows with a wrong DP:
			// the result modulo p is then (m * r^e)^dp' * r^-1 = m^dp' * r^(e * dp' - 1), which changes with r, where an
			// unblinded exponentiation would give the same wrong value every time.
			var key = ManagedKeys.Value[0];
			var n = B(key.Modulus);
			var e = B(key.Exponent);
			var input = new byte[255];
			new Random(5).NextBytes(input);
			var m = B(input);

			var s = ManagedRsa.PrivateOperation(key, n, e, m);
			Assert.That(BigInteger.ModPow(s, e, n), Is.EqualTo(m));
			Assert.That(ManagedRsa.PrivateOperation(key, n, e, m), Is.EqualTo(s));

			var faulty = key with { DP = Tampered(key.DP) };
			var first = ManagedRsa.PrivateOperation(faulty, n, e, m);
			Assert.That(first, Is.Not.EqualTo(s));
			Assert.That(ManagedRsa.PrivateOperation(faulty, n, e, m), Is.Not.EqualTo(first));
		}

		[Test]
		public void KeyFileFormatMatchesDesktop()
		{
			// The key file (player.oraid) is base64 of RSA.ToXmlString(true): written in the browser, read on desktop.
			foreach (var key in ManagedKeys.Value)
			{
				using (var rsa = new RSACryptoServiceProvider())
				{
					rsa.FromXmlString(ManagedRsa.ToXmlString(key, true));
					var read = rsa.ExportParameters(true);
					Assert.That(read.Modulus, Is.EqualTo(key.Modulus));
					Assert.That(read.D, Is.EqualTo(key.D));
					Assert.That(read.InverseQ, Is.EqualTo(key.InverseQ));
					Assert.That(rsa.ToXmlString(true), Is.EqualTo(ManagedRsa.ToXmlString(key, true)));
				}
			}

			// And written on desktop, read in the browser.
			using (var rsa = new RSACryptoServiceProvider(2048))
			{
				var desktop = rsa.ExportParameters(true);
				var read = ManagedRsa.FromXmlString(rsa.ToXmlString(true));
				var pairs = new[]
				{
					(desktop.Modulus, read.Modulus), (desktop.Exponent, read.Exponent), (desktop.P, read.P), (desktop.Q, read.Q),
					(desktop.DP, read.DP), (desktop.DQ, read.DQ), (desktop.InverseQ, read.InverseQ), (desktop.D, read.D)
				};

				foreach (var (a, b) in pairs)
					Assert.That(b, Is.EqualTo(a));

				var data = Encoding.UTF8.GetBytes("token");
				Assert.That(Convert.ToBase64String(ManagedRsa.SignSha1(read, data)), Is.EqualTo(CryptoUtil.Sign(desktop, data)));

				var publicOnly = ManagedRsa.FromXmlString(rsa.ToXmlString(false));
				Assert.That(publicOnly.Modulus, Is.EqualTo(desktop.Modulus));
				Assert.That(publicOnly.D, Is.Null);
			}

			Assert.Throws<CryptographicException>(() => ManagedRsa.FromXmlString("<RSAKeyValue><Modulus>AQAB</Modulus></RSAKeyValue>"));
			Assert.Throws<CryptographicException>(() => ManagedRsa.FromXmlString("<Other/>"));
		}

		[Test]
		public void PemAndFingerprintMatchDesktop()
		{
			foreach (var key in ManagedKeys.Value)
			{
				var pem = CryptoUtil.EncodePEMPublicKey(key);

				// The SubjectPublicKeyInfo the forum stores: the desktop runtime reads it back to the same key.
				using (var rsa = RSA.Create())
				{
					rsa.ImportFromPem(pem);
					var read = rsa.ExportParameters(false);
					Assert.That(read.Modulus, Is.EqualTo(key.Modulus));
					Assert.That(read.Exponent, Is.EqualTo(key.Exponent));
				}

				// What Server.cs does with the PublicKey of the forum's profile (the PEM with \n line ends, as PHP keeps it).
				var decoded = CryptoUtil.DecodePEMPublicKey(pem.Replace("\r\n", "\n"));
				Assert.That(decoded.Modulus, Is.EqualTo(key.Modulus));
				Assert.That(decoded.Exponent, Is.EqualTo(key.Exponent));

				// The forum's fingerprint: sha1($details['rsa']['n'] . $details['rsa']['e']), raw big-endian bytes.
				var expected = Convert.ToHexStringLower(SHA1.HashData(key.Modulus.Concat(key.Exponent).ToArray()));
				Assert.That(CryptoUtil.PublicKeyFingerprint(key), Is.EqualTo(expected));
				Assert.That(expected, Has.Length.EqualTo(40));

				// PLAYRA_RSA_DUMP_DIR: also write the PEMs there, to check them with other tools (openssl, PHP).
				var dump = Environment.GetEnvironmentVariable("PLAYRA_RSA_DUMP_DIR");
				if (!string.IsNullOrEmpty(dump))
				{
					Directory.CreateDirectory(dump);
					File.WriteAllText(Path.Combine(dump, CryptoUtil.PublicKeyFingerprint(key) + ".pem"), pem);
				}
			}
		}

		[Test]
		public void StepwiseGenerationWorks()
		{
			var generator = new ManagedRsa.KeyGenerator(1024);
			Assert.Throws<InvalidOperationException>(() => _ = generator.Result);

			var steps = 1;
			while (!generator.Step())
				steps++;

			Assert.That(generator.Step(), Is.True);
			Assert.That(steps, Is.GreaterThan(2));
			Assert.That(generator.Result.Modulus, Has.Length.EqualTo(128));
			var data = Encoding.UTF8.GetBytes("stepwise");
			Assert.That(DesktopVerify(generator.Result, data, ManagedRsa.SignSha1(generator.Result, data)), Is.True);
			Assert.Throws<ArgumentOutOfRangeException>(() => _ = new ManagedRsa.KeyGenerator(1000));
		}
	}
}
