using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NSec.Cryptography;

namespace ClubFFXIV.Network;

/// <summary>
/// Generates and verifies passphrases for password-protected clubs. Uses the
/// EFF long diceware wordlist (7776 words, CC BY 3.0) joined with hyphens —
/// e.g. "monsoon-blissful-kazoo-pebble-traffic-vendor". 6 words gives ~155
/// bits of entropy, which is comfortably brute-force-resistant for the
/// "share this with my friends" use case.
///
/// Hashing for the registry uses Argon2id (memory-hard, side-channel-resistant,
/// the modern recommended PHC password hash). Salt is 16 random bytes; both
/// the salt and the hash are stored on the registry so verification can be
/// performed against any subsequent passphrase entry.
/// </summary>
public static class Passphrase
{
    private const int WordCount = 6;
    private const string WordSeparator = "-";

    // Argon2id parameters tuned for ~50-100 ms on a typical desktop CPU.
    // High enough to slow brute-force attempts, low enough that the
    // listener's UI doesn't hitch when verifying.
    private const int HashSizeBytes = 32;
    private const int SaltSizeBytes = 16;
    private const int Argon2MemorySizeKb = 64 * 1024;   // 64 MiB
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 1;

    private static readonly Lazy<string[]> Words = new(LoadWordlist);

    /// <summary>
    /// Generates a fresh random passphrase: 6 EFF-long-list words joined by hyphens.
    /// Uses <see cref="RandomNumberGenerator"/> for cryptographic-quality randomness.
    /// </summary>
    public static string Generate()
    {
        var wordlist = Words.Value;
        var picked = new string[WordCount];
        for (int i = 0; i < WordCount; i++)
        {
            // GetInt32(min, max) is exclusive on max — sample uniformly across
            // the full wordlist without modulo bias.
            var idx = RandomNumberGenerator.GetInt32(0, wordlist.Length);
            picked[i] = wordlist[idx];
        }
        return string.Join(WordSeparator, picked);
    }

    /// <summary>
    /// Hashes a passphrase via Argon2id with a freshly-generated salt. Returns
    /// the salt and the derived hash bytes; caller serializes both into the
    /// registry payload so verification can reproduce the hash from a future
    /// passphrase entry.
    /// </summary>
    public static (byte[] salt, byte[] hash) Hash(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase is empty", nameof(passphrase));

        var salt = new byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);
        var hash = DeriveHash(passphrase, salt);
        return (salt, hash);
    }

    /// <summary>
    /// Re-derives the hash from <paramref name="passphrase"/> using a known
    /// salt and constant-time-compares it to <paramref name="expectedHash"/>.
    /// Used when the listener has the salt+hash from the registry and needs
    /// to confirm their entered passphrase matches before sending the hash up.
    /// </summary>
    public static bool Verify(string passphrase, byte[] salt, byte[] expectedHash)
    {
        if (string.IsNullOrEmpty(passphrase) || salt is null || expectedHash is null) return false;
        var computed = DeriveHash(passphrase, salt);
        return CryptographicOperations.FixedTimeEquals(computed, expectedHash);
    }

    /// <summary>
    /// Derive an Argon2id hash with a caller-supplied salt. Used by the listener
    /// flow: the registry returns a known salt, the listener types a passphrase,
    /// we hash here and ship the bytes back to the registry as proof-of-knowledge
    /// without the registry ever seeing the plaintext.
    /// </summary>
    public static byte[] HashWithSalt(string passphrase, byte[] salt)
    {
        if (string.IsNullOrEmpty(passphrase))
            throw new ArgumentException("Passphrase is empty", nameof(passphrase));
        if (salt is null || salt.Length == 0)
            throw new ArgumentException("Salt is empty", nameof(salt));
        return DeriveHash(passphrase, salt);
    }

    private static byte[] DeriveHash(string passphrase, byte[] salt)
    {
        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = Argon2Parallelism,
            MemorySize = Argon2MemorySizeKb,
            NumberOfPasses = Argon2Iterations,
        });
        return algorithm.DeriveBytes(passphrase, salt, count: HashSizeBytes);
    }

    private static string[] LoadWordlist()
    {
        var asm = Assembly.GetExecutingAssembly();
        // Resource name follows the project's default namespace + folder + file.
        // .csproj's <EmbeddedResource> with logical "Resources/eff_large_wordlist.txt"
        // resolves to "ClubFFXIV.Resources.eff_large_wordlist.txt" by default.
        var name = $"ClubFFXIV.Resources.eff_large_wordlist.txt";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded EFF wordlist not found ({name}); check ClubFFXIV.csproj <EmbeddedResource>.");
        using var reader = new StreamReader(stream);

        var words = new List<string>(7776);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // EFF wordlist format: "11111\tabacus" (5-digit dice prefix + tab + word).
            // Strip the prefix; keep the word.
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var word = line[(tab + 1)..].Trim();
            if (word.Length > 0) words.Add(word);
        }
        return words.ToArray();
    }
}
