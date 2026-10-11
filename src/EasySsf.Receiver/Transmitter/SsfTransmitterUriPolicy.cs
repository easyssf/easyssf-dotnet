using System.Text.RegularExpressions;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// What the issuer of a transmitter and the endpoints it publishes must look like. SSF requires
/// <c>https</c> and an issuer without query or fragment. Loopback addresses (<c>localhost</c>,
/// <c>127.0.0.1</c>, <c>::1</c>) may use <c>http</c>, so that a transmitter on the developer's
/// machine works; <see cref="AllowInsecureHttp"/> extends that to every host, for test setups only.
/// </summary>
/// <param name="AllowInsecureHttp">whether <c>http</c> is accepted for hosts other than loopback</param>
public sealed partial record SsfTransmitterUriPolicy(bool AllowInsecureHttp)
{
    /// <summary>
    /// <c>https</c>, or <c>http</c> on loopback addresses only.
    /// </summary>
    public static SsfTransmitterUriPolicy Default { get; } = new(false);

    /// <summary>
    /// <c>http</c> on any host as well. For development and test setups only: the metadata of the
    /// transmitter decides where keys come from, and without TLS anyone on the path can change it.
    /// </summary>
    public static SsfTransmitterUriPolicy Insecure { get; } = new(true);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://[^/?#\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteWithAuthority();

    /// <summary>
    /// Whether the URI uses plain <c>http</c>.
    /// </summary>
    public static bool IsInsecure(Uri? uri)
    {
        return uri is { IsAbsoluteUri: true } && uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="issuer">the issuer of the transmitter</param>
    /// <returns>the issuer as a URI</returns>
    /// <exception cref="ArgumentException">if the issuer is not acceptable</exception>
    public Uri CheckIssuer(string issuer)
    {
        Uri uri = Parse(issuer, "Transmitter issuer");
        if (issuer.Contains('?') || issuer.Contains('#'))
        {
            throw new ArgumentException("Transmitter issuer must not have a query or fragment: " + issuer, nameof(issuer));
        }
        CheckScheme(uri, "Transmitter issuer");
        return uri;
    }

    /// <param name="uri">an endpoint of the transmitter: its metadata, JWK Set, stream management or poll endpoint</param>
    /// <param name="description">what the endpoint is, for the message of the exception</param>
    /// <exception cref="ArgumentException">if the endpoint is not acceptable</exception>
    public void CheckEndpoint(Uri? uri, string description)
    {
        if (uri is null)
        {
            throw new ArgumentException(description + " must not be null", nameof(uri));
        }
        string value = uri.OriginalString;
        Parse(value, description);
        if (value.Contains('#'))
        {
            throw new ArgumentException(description + " must not have a fragment: " + value, nameof(uri));
        }
        CheckScheme(uri, description);
    }

    private static Uri Parse(string? value, string description)
    {
        if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new ArgumentException(description + " is not a valid URI: " + value);
        }
        if (!AbsoluteWithAuthority().IsMatch(value) || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException(description + " must be an absolute URI with a host: " + value);
        }
        return uri;
    }

    private void CheckScheme(Uri uri, string description)
    {
        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && (AllowInsecureHttp || IsLoopback(uri)))
        {
            return;
        }
        throw new ArgumentException(description + " must use https: " + uri.OriginalString);
    }

    /// <summary>
    /// Whether the host of the URI is a loopback address: <c>localhost</c> or a name under it,
    /// <c>127.0.0.0/8</c> or <c>::1</c>.
    /// </summary>
    public static bool IsLoopback(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }
        return uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
