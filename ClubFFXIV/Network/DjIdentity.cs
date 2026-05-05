using System;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace ClubFFXIV.Network;

/// <summary>
/// Ed25519 keypair representing a single DJ. The public key hash is the DJ's stable
/// identity in the registry; the private key is the only credential that can publish
/// or revoke clubs under that identity.
///
/// Persistence is the caller's responsibility — see Configuration.DjPrivateKeyBase64.
/// LOSING THE PRIVATE KEY = LOSING ALL PUBLISHED CLUBS UNDER THIS IDENTITY.
/// </summary>
public sealed class DjIdentity : IDisposable
{
    private readonly Key key;

    public string PublicKeyBase64 { get; }
    public string DjId { get; }

    private DjIdentity(Key key)
    {
        this.key = key;
        var pkBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        PublicKeyBase64 = Convert.ToBase64String(pkBytes);
        DjId = ComputeDjId(PublicKeyBase64);
    }

    public static DjIdentity Generate()
    {
        var newKey = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return new DjIdentity(newKey);
    }

    public static DjIdentity FromPrivateKeyBase64(string privBase64)
    {
        var bytes = Convert.FromBase64String(privBase64);
        var imported = Key.Import(SignatureAlgorithm.Ed25519, bytes, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return new DjIdentity(imported);
    }

    public string ExportPrivateKeyBase64()
    {
        var bytes = key.Export(KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(bytes);
    }

    public string Sign(string message)
    {
        var msgBytes = Encoding.UTF8.GetBytes(message);
        var sig = SignatureAlgorithm.Ed25519.Sign(key, msgBytes);
        return Convert.ToBase64String(sig);
    }

    public void Dispose() => key.Dispose();

    private static string ComputeDjId(string pubkeyBase64)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(pubkeyBase64));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
