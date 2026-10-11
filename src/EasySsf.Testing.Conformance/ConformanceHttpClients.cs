using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasySsf.Testing.Conformance;

/// <summary>
/// The HTTP clients of the harness and of the receiver under test: each a client of its own, with
/// the trust the self-signed certificate of a conformance suite needs.
/// </summary>
public static class ConformanceHttpClients
{
    /// <summary>
    /// The <c>User-Agent</c> the receiver under test sends.
    /// </summary>
    public const string UserAgent = "easyssf-receiver-conformance";

    /// <summary>
    /// A client that trusts the given certificate in addition to the system's trust, whatever host
    /// name it is presented for (the certificate of a local suite is issued for <c>localhost</c>,
    /// the suite answers as <c>localhost.emobix.co.uk</c>).
    /// </summary>
    /// <param name="trusted">the certificate to trust, <see langword="null"/> for the system's trust only</param>
    /// <param name="trustAll">whether to trust any certificate instead; only for a suite running locally</param>
    /// <param name="timeout">the timeout of a request</param>
    public static HttpClient Create(X509Certificate2? trusted, bool trustAll, TimeSpan timeout)
    {
        SocketsHttpHandler handler = new() { ConnectTimeout = TimeSpan.FromSeconds(20) };
        if (trustAll)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (trusted is not null)
        {
            string pinned = trusted.GetCertHashString(HashAlgorithmName.SHA256);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None
                    || (certificate is not null && string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), pinned, StringComparison.OrdinalIgnoreCase));
        }
        HttpClient client = new(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// Reads a PEM or DER encoded certificate.
    /// </summary>
    public static X509Certificate2 LoadCertificate(byte[] encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        string text = System.Text.Encoding.ASCII.GetString(encoded);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificate(encoded);
    }

    /// <summary>
    /// A certificate with its private key, from PEM, in a form Kestrel can serve.
    /// </summary>
    public static X509Certificate2 LoadCertificateWithKey(string certificatePem, string privateKeyPem)
    {
        using X509Certificate2 ephemeral = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        // re-imported through PKCS#12 so that the platform's TLS stack can use the key
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
