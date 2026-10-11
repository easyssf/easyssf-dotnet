using System.Net.Http.Headers;
using System.Text.Json;

using EasySsf.Core.Metadata;
using EasySsf.Core.Support;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Transmitter;

/// <summary>
/// Resolves the <see cref="SsfTransmitterMetadata"/> of the transmitter. The metadata is fetched on
/// first use and cached once it was retrieved successfully, so a receiver can start even when the
/// transmitter is not reachable.
/// </summary>
public sealed class SsfTransmitterMetadataResolver : IDisposable
{
    private const string WellKnownPath = "/.well-known/ssf-configuration";

    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private readonly HttpClient _httpClient;

    private readonly ILogger _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile SsfTransmitterMetadata? _metadata;

    private bool _warnedInsecure;

    /// <param name="issuer">the issuer of the transmitter</param>
    /// <param name="metadataUri">where the metadata is, <see langword="null"/> to derive it from the issuer</param>
    /// <param name="httpClient">retrieves the metadata</param>
    /// <param name="uriPolicy">what the issuer and the endpoints of the transmitter must look like, <see cref="SsfTransmitterUriPolicy.Default"/> if <see langword="null"/></param>
    /// <param name="logger">logs where the metadata came from, nothing if <see langword="null"/></param>
    /// <exception cref="ArgumentException">if the issuer or the metadata URI is not acceptable</exception>
    public SsfTransmitterMetadataResolver(string issuer, Uri? metadataUri, HttpClient httpClient,
            SsfTransmitterUriPolicy? uriPolicy = null, ILogger<SsfTransmitterMetadataResolver>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(httpClient);
        UriPolicy = uriPolicy ?? SsfTransmitterUriPolicy.Default;
        UriPolicy.CheckIssuer(issuer);
        if (metadataUri is not null)
        {
            UriPolicy.CheckEndpoint(metadataUri, "Transmitter metadata URL");
        }
        Issuer = issuer;
        MetadataUris = (metadataUri is not null) ? [metadataUri] : DeriveMetadataUris(issuer);
        _httpClient = httpClient;
        _logger = logger ?? NullLogger<SsfTransmitterMetadataResolver>.Instance;
    }

    /// <summary>
    /// The issuer of the transmitter.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// The locations the metadata is looked up at, in order.
    /// </summary>
    public IReadOnlyList<Uri> MetadataUris { get; }

    /// <summary>
    /// What the issuer and the endpoints of the transmitter must look like.
    /// </summary>
    public SsfTransmitterUriPolicy UriPolicy { get; }

    /// <summary>
    /// The metadata if it was retrieved already, without retrieving it.
    /// </summary>
    public SsfTransmitterMetadata? ResolvedMetadata => _metadata;

    /// <exception cref="SsfTransmitterUnavailableException">if the metadata cannot be retrieved</exception>
    public async Task<SsfTransmitterMetadata> ResolveAsync(CancellationToken cancellationToken = default)
    {
        SsfTransmitterMetadata? metadata = _metadata;
        if (metadata is not null)
        {
            return metadata;
        }
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            metadata = _metadata;
            if (metadata is null)
            {
                metadata = await FetchAsync(cancellationToken).ConfigureAwait(false);
                _metadata = metadata;
            }
            return metadata;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SsfTransmitterMetadata> FetchAsync(CancellationToken cancellationToken)
    {
        List<Exception> failures = [];
        foreach (Uri metadataUri in MetadataUris)
        {
            try
            {
                SsfTransmitterMetadata metadata = await FetchAsync(metadataUri, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Resolved SSF transmitter metadata from {MetadataUri}", metadataUri);
                return metadata;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve SSF transmitter metadata from {MetadataUri}", metadataUri);
                failures.Add(ex);
            }
        }
        throw new SsfTransmitterUnavailableException(
            "Could not resolve SSF transmitter metadata from [" + string.Join(", ", MetadataUris) + "]",
            (failures.Count == 1) ? failures[0] : new AggregateException(failures));
    }

    private async Task<SsfTransmitterMetadata> FetchAsync(Uri metadataUri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, metadataUri);
        request.Headers.Accept.Add(JsonMediaType);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("The transmitter answered with status " + (int)response.StatusCode);
        }
        JsonElement claims = SsfJson.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        if (claims.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The transmitter metadata is not a JSON object");
        }
        string? metadataIssuer = claims.StringMember("issuer");
        if (metadataIssuer != Issuer)
        {
            throw new InvalidOperationException($"SSF transmitter metadata issuer '{metadataIssuer}' does not match the"
                + $" configured transmitter issuer '{Issuer}'");
        }
        string? jwksUri = claims.StringMember("jwks_uri");
        SsfTransmitterMetadata metadata = new(Issuer, (jwksUri is not null) ? new Uri(jwksUri, UriKind.RelativeOrAbsolute) : null, claims);
        CheckEndpoints(metadata);
        return metadata;
    }

    /// <summary>
    /// The metadata controls where keys and stream operations come from, so its endpoints are held
    /// to the same standard as the issuer.
    /// </summary>
    private void CheckEndpoints(SsfTransmitterMetadata metadata)
    {
        List<string> insecure = [];
        Check(metadata.JwksUri, "jwks_uri", insecure);
        Check(metadata.ConfigurationEndpoint, "configuration_endpoint", insecure);
        Check(metadata.StatusEndpoint, "status_endpoint", insecure);
        Check(metadata.AddSubjectEndpoint, "add_subject_endpoint", insecure);
        Check(metadata.RemoveSubjectEndpoint, "remove_subject_endpoint", insecure);
        Check(metadata.VerificationEndpoint, "verification_endpoint", insecure);
        if (insecure.Count > 0 && UriPolicy.AllowInsecureHttp && !_warnedInsecure)
        {
            _warnedInsecure = true;
            _logger.LogWarning("The SSF transmitter {Issuer} publishes endpoints without TLS, accepted because insecure http"
                + " is allowed. For development only: {Endpoints}", Issuer, string.Join(", ", insecure));
        }
    }

    private void Check(Uri? endpoint, string name, List<string> insecure)
    {
        if (endpoint is null)
        {
            return;
        }
        UriPolicy.CheckEndpoint(endpoint, "SSF transmitter metadata " + name);
        if (SsfTransmitterUriPolicy.IsInsecure(endpoint) && !SsfTransmitterUriPolicy.IsLoopback(endpoint))
        {
            insecure.Add(name + " " + endpoint);
        }
    }

    /// <summary>
    /// Derives the metadata locations for an issuer. SSF 1.0 (section 7.2) inserts
    /// <c>/.well-known/ssf-configuration</c> between the host and the path of the issuer.
    /// Transmitters that grew out of OpenID providers tend to append it to the issuer instead, so
    /// that location is used as a fallback.
    /// </summary>
    /// <exception cref="ArgumentException">if the issuer is not an absolute URI</exception>
    public static IReadOnlyList<Uri> DeriveMetadataUris(string issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException("Transmitter issuer must be an absolute URI: " + issuer, nameof(issuer));
        }
        string authority = uri.GetLeftPart(UriPartial.Authority);
        string path = "/" + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        path = path.TrimEnd('/');
        List<Uri> uris = [new Uri(authority + WellKnownPath + path)];
        Uri appended = new(authority + path + WellKnownPath);
        if (appended != uris[0])
        {
            uris.Add(appended);
        }
        return uris.AsReadOnly();
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
