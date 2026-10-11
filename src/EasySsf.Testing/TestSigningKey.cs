using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EasySsf.Testing;

/// <summary>
/// An RSA key pair a <see cref="TestTransmitter"/> signs with, together with its <c>kid</c>.
/// Keys of any size can be generated, so that tests can sign with keys a receiver has to reject.
/// </summary>
public sealed class TestSigningKey : IDisposable
{
    private TestSigningKey(string keyId, RSA rsa)
    {
        KeyId = keyId;
        Rsa = rsa;
    }

    /// <summary>
    /// The <c>kid</c> of the key, in the JWK Set and in the header of every token signed with it.
    /// </summary>
    public string KeyId { get; }

    /// <summary>
    /// The key pair.
    /// </summary>
    public RSA Rsa { get; }

    /// <summary>
    /// The size of the key in bits.
    /// </summary>
    public int KeySize => Rsa.KeySize;

    /// <summary>
    /// Generates a key pair.
    /// </summary>
    /// <param name="bits">the size of the key, 2048 for a key a receiver accepts by default</param>
    /// <param name="keyId">the <c>kid</c> of the key</param>
    public static TestSigningKey Generate(int bits, string keyId)
    {
        return new TestSigningKey(keyId, RSA.Create(bits));
    }

    /// <summary>
    /// The public key as a JWK (RFC 7517): <c>kty</c>, <c>kid</c>, <c>n</c> and <c>e</c>.
    /// </summary>
    public JsonObject ToPublicJwk()
    {
        RSAParameters parameters = Rsa.ExportParameters(false);
        return new JsonObject
        {
            ["kty"] = "RSA",
            ["kid"] = KeyId,
            ["n"] = Base64Url.EncodeToString(parameters.Modulus),
            ["e"] = Base64Url.EncodeToString(parameters.Exponent),
        };
    }

    public void Dispose()
    {
        Rsa.Dispose();
    }
}
