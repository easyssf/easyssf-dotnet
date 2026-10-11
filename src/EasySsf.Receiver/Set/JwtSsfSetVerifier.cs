using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Text.Json;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Transmitter;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EasySsf.Receiver.Set;

/// <summary>
/// <see cref="ISsfSetVerifier"/> that verifies SET signatures against the transmitter's JWK Set
/// and validates the claims required by RFC 8417 and the SSF profile.
/// <para>
/// The JWK Set location is resolved on first use, so a transmitter that is not reachable does not
/// prevent the receiver from starting. The JWK Set is cached and fetched again when a SET is
/// signed with a key that is not in it, at most once per
/// <see cref="SsfSetVerifierOptions.JwkSetRefreshRateLimit"/>.
/// </para>
/// <para>
/// <see cref="JsonWebTokenHandler"/> checks the signature and the algorithm only. The claims are
/// parsed once, here, and <c>typ</c>, <c>iss</c>, <c>aud</c>, <c>jti</c>, <c>iat</c>,
/// <c>events</c> and <c>sub_id</c> are checked here, so that the error codes of RFC 8935 stay
/// ours.
/// </para>
/// </summary>
public sealed class JwtSsfSetVerifier : ISsfSetVerifier, IDisposable
{
    /// <summary>
    /// The media type a SET must be explicitly typed with (RFC 8417, section 2.3).
    /// </summary>
    public const string SecEventJwtType = "secevent+jwt";

    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");

    private static readonly MediaTypeWithQualityHeaderValue JwkSetMediaType = new("application/jwk-set+json");

    private readonly string _issuer;

    private readonly Func<CancellationToken, ValueTask<Uri>> _jwkSetUri;

    private readonly HttpClient _httpClient;

    private readonly SsfSetVerifierOptions _options;

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private volatile JwkSetSnapshot? _jwkSet;

    /// <param name="issuer">the issuer every SET must be issued by</param>
    /// <param name="jwkSetUri">the location of the transmitter's JWK Set</param>
    /// <param name="httpClient">used to fetch the JWK Set</param>
    /// <param name="options">how SETs are verified, the defaults if <see langword="null"/></param>
    public JwtSsfSetVerifier(string issuer, Uri jwkSetUri, HttpClient httpClient, SsfSetVerifierOptions? options = null)
        : this(issuer, Resolved(jwkSetUri), httpClient, options)
    {
    }

    /// <param name="issuer">the issuer every SET must be issued by</param>
    /// <param name="jwkSetUri">
    /// supplies the location of the transmitter's JWK Set, called once on first use; may throw
    /// <see cref="SsfTransmitterUnavailableException"/>
    /// </param>
    /// <param name="httpClient">used to fetch the JWK Set</param>
    /// <param name="options">how SETs are verified, the defaults if <see langword="null"/></param>
    public JwtSsfSetVerifier(string issuer, Func<CancellationToken, ValueTask<Uri>> jwkSetUri, HttpClient httpClient,
            SsfSetVerifierOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentNullException.ThrowIfNull(jwkSetUri);
        ArgumentNullException.ThrowIfNull(httpClient);
        _issuer = issuer;
        _jwkSetUri = jwkSetUri;
        _httpClient = httpClient;
        _options = options ?? new SsfSetVerifierOptions();
        _ = AcceptedAlgorithms();
    }

    /// <summary>
    /// The issuer every SET must be issued by.
    /// </summary>
    public string Issuer => _issuer;

    /// <summary>
    /// How SETs are verified. Read for every SET.
    /// </summary>
    public SsfSetVerifierOptions Options => _options;

    private static Func<CancellationToken, ValueTask<Uri>> Resolved(Uri jwkSetUri)
    {
        ArgumentNullException.ThrowIfNull(jwkSetUri);
        return _ => ValueTask.FromResult(jwkSetUri);
    }

    public async Task<SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
    {
        ParsedJws jws = Parse(encodedSet);
        IReadOnlySet<string> algorithms = AcceptedAlgorithms();
        if (!algorithms.Contains(jws.Algorithm))
        {
            throw Invalid(SsfSetVerificationException.InvalidKey,
                $"The SET is signed with the algorithm {jws.Algorithm}, which is not accepted");
        }
        IReadOnlyList<JsonWebKey> keys = await SelectKeysAsync(jws, cancellationToken).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            throw Invalid(SsfSetVerificationException.InvalidKey, "The SET is signed with an unknown key");
        }
        TokenValidationParameters parameters = new()
        {
            // typ header and claims are validated by this class
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            ValidateTokenReplay = false,
            ValidateIssuerSigningKey = false,
            RequireSignedTokens = true,
            IssuerSigningKeys = keys,
            ValidAlgorithms = algorithms,
        };
        TokenValidationResult result = await _handler.ValidateTokenAsync(encodedSet, parameters).ConfigureAwait(false);
        if (!result.IsValid)
        {
            throw Rejected(result.Exception);
        }
        Validate(jws);
        return ToEventToken(jws.Claims);
    }

    private static SsfSetVerificationException Rejected(Exception? cause)
    {
        return cause switch
        {
            SecurityTokenInvalidSignatureException => Invalid(SsfSetVerificationException.InvalidKey,
                "The signature of the SET is invalid", cause),
            SecurityTokenInvalidAlgorithmException => Invalid(SsfSetVerificationException.InvalidKey,
                "The SET is signed with an algorithm that is not accepted", cause),
            _ => Invalid(SsfSetVerificationException.InvalidRequest,
                "The SET was rejected: " + (cause?.Message ?? "unknown reason"), cause),
        };
    }

    private static ParsedJws Parse(string encodedSet)
    {
        if (string.IsNullOrWhiteSpace(encodedSet))
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET is empty");
        }
        string[] parts = encodedSet.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET is not a JWS in compact serialization");
        }
        JsonElement header;
        JsonElement claims;
        try
        {
            header = SsfJson.Parse(Base64Url.DecodeFromChars(parts[0]));
            claims = SsfJson.Parse(Base64Url.DecodeFromChars(parts[1]));
        }
        catch (FormatException ex)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET is not a JWS in compact serialization", ex);
        }
        catch (JsonException ex)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The header or payload of the SET is not JSON", ex);
        }
        if (header.ValueKind != JsonValueKind.Object || claims.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The header or payload of the SET is not a JSON object");
        }
        string? algorithm = header.StringMember("alg");
        if (algorithm is null || algorithm.Equals("none", StringComparison.OrdinalIgnoreCase) || parts[2].Length == 0)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET is not a signed JWT");
        }
        return new ParsedJws(header, claims, algorithm, header.StringMember("kid"));
    }

    private IReadOnlySet<string> AcceptedAlgorithms()
    {
        HashSet<string> algorithms = new(StringComparer.Ordinal);
        foreach (string name in _options.AcceptedAlgorithms)
        {
            string algorithm = name.Trim();
            if (!SsfSetVerifierOptions.SupportedAlgorithms.Contains(algorithm))
            {
                throw new ArgumentException("Not an asymmetric signature algorithm: " + name);
            }
            algorithms.Add(algorithm);
        }
        if (algorithms.Count == 0)
        {
            throw new ArgumentException("AcceptedAlgorithms must not be empty");
        }
        return algorithms;
    }

    private void Validate(ParsedJws jws)
    {
        string? type = jws.Header.StringMember("typ");
        if (_options.RequireTypeHeader && !SecEventJwtType.Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest,
                "The SET must be typed with a typ header of " + SecEventJwtType);
        }
        JsonElement claims = jws.Claims;
        if (claims.StringMember("iss") != _issuer)
        {
            throw Invalid(SsfSetVerificationException.InvalidIssuer, "The SET was not issued by the expected transmitter");
        }
        IReadOnlyCollection<string> expectedAudiences = ExpectedAudiences();
        if (expectedAudiences.Count > 0 && !Audiences(claims).Intersect(expectedAudiences, StringComparer.Ordinal).Any())
        {
            throw Invalid(SsfSetVerificationException.InvalidAudience, "The SET is not addressed to this receiver");
        }
        if (claims.StringMember("jti") is null)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET has no jti claim");
        }
        DateTimeOffset? issuedAt = IssuedAt(claims);
        if (issuedAt is null)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET has no iat claim");
        }
        if (issuedAt > _options.TimeProvider.GetUtcNow() + _options.ClockSkew)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET was issued in the future");
        }
        if (claims.Member("events") is not { } events || !events.IsNonEmptyObject())
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The SET has no events claim");
        }
        if (_options.SubjectCompatibility == SubjectCompatibilityMode.Strict
                && (claims.Member("sub_id") is not { } subjectId || !subjectId.IsNonEmptyObject()))
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest,
                "The SET has no top-level sub_id claim, which SSF 1.0 requires (the subject compatibility mode"
                    + " Legacy accepts SETs of transmitters that put the subject into the event)");
        }
    }

    private IReadOnlyCollection<string> ExpectedAudiences()
    {
        if (_options.ExpectedAudiencesProvider is { } provider)
        {
            return provider() ?? [];
        }
        return _options.ExpectedAudiences.ToArray();
    }

    private static List<string> Audiences(JsonElement claims)
    {
        List<string> audiences = [];
        JsonElement? aud = claims.Member("aud");
        if (aud is { ValueKind: JsonValueKind.String } single)
        {
            audiences.Add(single.GetString()!);
        }
        else if (aud is { ValueKind: JsonValueKind.Array } several)
        {
            foreach (JsonElement audience in several.EnumerateArray())
            {
                if (audience.ValueKind == JsonValueKind.String)
                {
                    audiences.Add(audience.GetString()!);
                }
                else
                {
                    throw Invalid(SsfSetVerificationException.InvalidRequest, "The aud claim of the SET is malformed");
                }
            }
        }
        else if (aud is not null)
        {
            throw Invalid(SsfSetVerificationException.InvalidRequest, "The aud claim of the SET is malformed");
        }
        return audiences;
    }

    private static DateTimeOffset? IssuedAt(JsonElement claims)
    {
        if (claims.Member("iat") is not { ValueKind: JsonValueKind.Number } iat)
        {
            return null;
        }
        if (iat.TryGetInt64(out long seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        if (iat.TryGetDouble(out double fraction) && double.IsFinite(fraction))
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)fraction);
        }
        return null;
    }

    private static SsfEventToken ToEventToken(JsonElement claims)
    {
        List<KeyValuePair<string, JsonElement>> events = [];
        foreach (JsonProperty property in claims.GetProperty("events").EnumerateObject())
        {
            events.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value));
        }
        JsonElement? subjectId = (claims.Member("sub_id") is { ValueKind: JsonValueKind.Object } subject) ? subject : null;
        return new SsfEventToken(claims.StringMember("jti")!, claims.StringMember("iss")!, IssuedAt(claims)!.Value,
            Audiences(claims), events, subjectId, claims.StringMember("txn"), claims);
    }

    private static SsfSetVerificationException Invalid(string errorCode, string message, Exception? cause = null)
    {
        return new SsfSetVerificationException(errorCode, message, cause);
    }

    private async ValueTask<IReadOnlyList<JsonWebKey>> SelectKeysAsync(ParsedJws jws, CancellationToken cancellationToken)
    {
        JwkSetSnapshot jwkSet = await GetJwkSetAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        List<JsonWebKey> keys = Matching(jwkSet, jws);
        if (keys.Count == 0 && jws.KeyId is not null)
        {
            // the transmitter may have rotated its keys since the JWK Set was fetched
            jwkSet = await GetJwkSetAsync(refresh: true, cancellationToken).ConfigureAwait(false);
            keys = Matching(jwkSet, jws);
        }
        return keys;
    }

    private List<JsonWebKey> Matching(JwkSetSnapshot jwkSet, ParsedJws jws)
    {
        List<JsonWebKey> keys = [];
        foreach (JsonWebKey key in jwkSet.Keys)
        {
            if (jws.KeyId is not null && key.Kid != jws.KeyId)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(key.Use) && key.Use != JsonWebKeyUseNames.Sig)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(key.Alg) && key.Alg != jws.Algorithm)
            {
                continue;
            }
            if (key.Kty != KeyType(jws.Algorithm))
            {
                continue;
            }
            if (key.Kty == JsonWebAlgorithmsKeyTypes.RSA && _options.MinRsaKeySize > 0 && key.KeySize < _options.MinRsaKeySize)
            {
                continue;
            }
            keys.Add(key);
        }
        return keys;
    }

    private static string KeyType(string algorithm)
    {
        return algorithm.StartsWith("ES", StringComparison.Ordinal) ? JsonWebAlgorithmsKeyTypes.EllipticCurve
            : JsonWebAlgorithmsKeyTypes.RSA;
    }

    private async ValueTask<JwkSetSnapshot> GetJwkSetAsync(bool refresh, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _options.TimeProvider.GetUtcNow();
        JwkSetSnapshot? jwkSet = _jwkSet;
        if (jwkSet is not null && !NeedsFetch(jwkSet, now, refresh))
        {
            return jwkSet;
        }
        await _fetchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            jwkSet = _jwkSet;
            if (jwkSet is not null && !NeedsFetch(jwkSet, now, refresh))
            {
                return jwkSet;
            }
            Uri uri = jwkSet?.Uri ?? await _jwkSetUri(cancellationToken).ConfigureAwait(false);
            jwkSet = new JwkSetSnapshot(uri, await FetchJwkSetAsync(uri, cancellationToken).ConfigureAwait(false),
                _options.TimeProvider.GetUtcNow());
            _jwkSet = jwkSet;
            return jwkSet;
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    private bool NeedsFetch(JwkSetSnapshot jwkSet, DateTimeOffset now, bool refresh)
    {
        TimeSpan age = now - jwkSet.FetchedAt;
        if (age >= _options.JwkSetCacheLifetime)
        {
            return true;
        }
        return refresh && age >= _options.JwkSetRefreshRateLimit;
    }

    private async Task<IReadOnlyList<JsonWebKey>> FetchJwkSetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(JwkSetMediaType);
        request.Headers.Accept.Add(JsonMediaType);
        string body;
        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SsfTransmitterUnavailableException(
                    $"The JWK Set endpoint {uri} answered with status {(int)response.StatusCode}");
            }
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new SsfTransmitterUnavailableException("Could not retrieve the keys to verify the SET from " + uri, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // the request timed out
            throw new SsfTransmitterUnavailableException("Could not retrieve the keys to verify the SET from " + uri, ex);
        }
        try
        {
            return new JsonWebKeySet(body).Keys.AsReadOnly();
        }
        catch (ArgumentException ex)
        {
            throw new SsfTransmitterUnavailableException("The JWK Set endpoint " + uri + " did not answer with a JWK Set", ex);
        }
    }

    public void Dispose()
    {
        _fetchLock.Dispose();
    }

    private sealed record ParsedJws(JsonElement Header, JsonElement Claims, string Algorithm, string? KeyId);

    private sealed record JwkSetSnapshot(Uri Uri, IReadOnlyList<JsonWebKey> Keys, DateTimeOffset FetchedAt);
}
