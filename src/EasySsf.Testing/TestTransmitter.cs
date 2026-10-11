using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;

namespace EasySsf.Testing;

/// <summary>
/// A minimal SSF transmitter / identity provider for tests: publishes transmitter metadata and a
/// JWK Set over an <see cref="HttpMessageHandler"/>, and signs SETs and access tokens. Give the
/// receiver under test an <see cref="HttpClient"/> from <see cref="CreateHttpClient"/>, no socket
/// is involved.
/// </summary>
public sealed class TestTransmitter : IDisposable
{
    public const string Audience = "https://receiver.example";

    public const string ClientId = "receiver";

    public const string ClientSecret = "receiver-secret";

    public const string DefaultAccessToken = "transmitter-access-token";

    /// <summary>
    /// The <c>typ</c> header of a SET (RFC 8417, section 2.3).
    /// </summary>
    public const string SecEventJwtType = "secevent+jwt";

    /// <summary>
    /// The <c>typ</c> header of a plain JWT such as an access token.
    /// </summary>
    public const string JwtType = "JWT";

    private const string Realm = "/realms/test";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);

    private readonly TestTransmitterHandler _handler;

    private int _requests;

    private volatile string? _lastUserAgent;

    public TestTransmitter()
        : this(MetadataLocation.Ssf)
    {
    }

    /// <summary>
    /// A transmitter that publishes its metadata at the given location, or not at all.
    /// </summary>
    public static TestTransmitter WithMetadata(MetadataLocation metadataLocation)
    {
        return new TestTransmitter(metadataLocation);
    }

    private TestTransmitter(MetadataLocation metadataLocation)
    {
        Key = TestSigningKey.Generate(2048, "test-key");
        Issuer = "https://transmitter.example" + Realm;
        _handler = new TestTransmitterHandler(this);
        PublishJwks(Key);
        PublishMetadata(metadataLocation);
    }

    /// <summary>
    /// The issuer of the transmitter: the <c>iss</c> of every SET and the base of its endpoints.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// The key the transmitter signs with.
    /// </summary>
    public TestSigningKey Key { get; }

    public string JwksUri => Issuer + "/jwks";

    public string TokenUri => Issuer + "/token";

    public string PollUri => Issuer + "/poll";

    /// <summary>
    /// Whether the endpoints under the issuer answer; <see langword="false"/> makes them answer
    /// with 503. Published documents (the JWK Set, the metadata) are served regardless.
    /// </summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// The number of HTTP requests received so far.
    /// </summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>
    /// The <c>User-Agent</c> header of the most recent request.
    /// </summary>
    public string? LastUserAgent => _lastUserAgent;

    /// <summary>
    /// Serves the transmitter's documents and endpoints to an <see cref="HttpClient"/>.
    /// </summary>
    public HttpMessageHandler Handler => _handler;

    /// <summary>
    /// A client whose requests the transmitter answers in-process.
    /// </summary>
    public HttpClient CreateHttpClient()
    {
        return new HttpClient(_handler, disposeHandler: false);
    }

    /// <summary>
    /// Publishes a document at a path of the transmitter's host, for example a JWK Set.
    /// </summary>
    public void PublishDocument(string path, string json)
    {
        _documents[path] = json;
    }

    /// <summary>
    /// Removes a published document, so that requests for it are answered with 404.
    /// </summary>
    public void RemoveDocument(string path)
    {
        _documents.TryRemove(path, out _);
    }

    public void PublishJwks(params TestSigningKey[] keys)
    {
        JsonArray jwks = [];
        foreach (TestSigningKey key in keys)
        {
            jwks.Add((JsonNode)key.ToPublicJwk());
        }
        PublishDocument(Realm + "/jwks", new JsonObject { ["keys"] = jwks }.ToJsonString(JsonOptions));
    }

    public void PublishMetadata(MetadataLocation metadataLocation)
    {
        string metadata = new JsonObject
        {
            ["issuer"] = Issuer,
            ["jwks_uri"] = JwksUri,
            ["configuration_endpoint"] = Issuer + "/streams",
            ["status_endpoint"] = Issuer + "/streams/status",
            ["verification_endpoint"] = Issuer + "/verify",
            ["add_subject_endpoint"] = Issuer + "/subjects/add",
            ["remove_subject_endpoint"] = Issuer + "/subjects/remove",
        }.ToJsonString(JsonOptions);
        switch (metadataLocation)
        {
            case MetadataLocation.Ssf:
                PublishDocument("/.well-known/ssf-configuration" + Realm, metadata);
                break;
            case MetadataLocation.OidcStyle:
                PublishDocument(Realm + "/.well-known/ssf-configuration", metadata);
                break;
            case MetadataLocation.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(metadataLocation));
        }
    }

    public enum MetadataLocation
    {
        /// <summary>
        /// SSF 1.0, section 6.2.1: <c>/.well-known/ssf-configuration</c> followed by the path of
        /// the issuer.
        /// </summary>
        Ssf,

        /// <summary>
        /// <c>/.well-known/ssf-configuration</c> under the path of the issuer, as OpenID Connect
        /// Discovery places its document and some transmitters do.
        /// </summary>
        OidcStyle,

        /// <summary>
        /// No metadata is published.
        /// </summary>
        None,
    }

    /// <summary>
    /// Claims of a valid SET with a single event whose payload has an <c>event_timestamp</c>.
    /// </summary>
    /// <param name="eventType">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <param name="subjectId">the <c>sub_id</c> claim, left out if <see langword="null"/></param>
    public JsonObject SetClaims(string eventType, JsonObject? subjectId)
    {
        return SetClaims(eventType, subjectId,
            new JsonObject { ["event_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    /// <summary>
    /// Claims of a valid SET with a single event.
    /// </summary>
    /// <param name="eventType">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <param name="subjectId">the <c>sub_id</c> claim, left out if <see langword="null"/></param>
    /// <param name="payload">the payload of the event</param>
    public JsonObject SetClaims(string eventType, JsonObject? subjectId, JsonObject payload)
    {
        JsonObject claims = new()
        {
            ["iss"] = Issuer,
            ["jti"] = Guid.NewGuid().ToString(),
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["aud"] = Audience,
            ["events"] = new JsonObject { [SsfEventTypes.Resolve(eventType)] = payload.DeepClone() },
        };
        if (subjectId is not null)
        {
            claims["sub_id"] = subjectId.DeepClone();
        }
        return claims;
    }

    /// <summary>
    /// A valid, signed SET with a single event.
    /// </summary>
    public string Set(string eventType, JsonObject? subjectId)
    {
        return SignSet(SetClaims(eventType, subjectId));
    }

    /// <summary>
    /// Signs the claims as a SET: typed <c>secevent+jwt</c>, with the transmitter's key.
    /// </summary>
    public string SignSet(JsonObject claims)
    {
        return Sign(Key, SecEventJwtType, claims);
    }

    /// <summary>
    /// An access token of the identity provider, as an OAuth client would present it.
    /// </summary>
    public string AccessToken(string subject, string sessionId, DateTimeOffset issuedAt)
    {
        JsonObject claims = new()
        {
            ["iss"] = Issuer,
            ["sub"] = subject,
            ["sid"] = sessionId,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddSeconds(300).ToUnixTimeSeconds(),
        };
        return Sign(Key, JwtType, claims);
    }

    /// <summary>
    /// Signs the claims with <c>RS256</c>.
    /// </summary>
    public static string Sign(TestSigningKey key, string type, JsonObject claims)
    {
        return Sign(key, "RS256", type, claims);
    }

    /// <summary>
    /// Signs the claims as a JWS in compact serialization.
    /// </summary>
    /// <param name="key">the key to sign with, its <c>kid</c> goes into the header</param>
    /// <param name="algorithm">an RSA algorithm of RFC 7518: <c>RS256</c>, <c>RS384</c>, <c>RS512</c>, <c>PS256</c>, <c>PS384</c> or <c>PS512</c></param>
    /// <param name="type">the <c>typ</c> header, left out if <see langword="null"/></param>
    /// <param name="claims">the payload</param>
    public static string Sign(TestSigningKey key, string algorithm, string? type, JsonObject claims)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(claims);
        JsonObject header = new() { ["alg"] = algorithm };
        if (type is not null)
        {
            header["typ"] = type;
        }
        header["kid"] = key.KeyId;
        string signingInput = Encode(header) + "." + Encode(claims);
        HashAlgorithmName hash = algorithm[2..] switch
        {
            "256" => HashAlgorithmName.SHA256,
            "384" => HashAlgorithmName.SHA384,
            "512" => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException("Not an RSA signature algorithm: " + algorithm, nameof(algorithm)),
        };
        RSASignaturePadding padding = algorithm.StartsWith("PS", StringComparison.Ordinal) ? RSASignaturePadding.Pss
            : algorithm.StartsWith("RS", StringComparison.Ordinal) ? RSASignaturePadding.Pkcs1
            : throw new ArgumentException("Not an RSA signature algorithm: " + algorithm, nameof(algorithm));
        // no minimum key size: tests need to sign with keys the receiver has to reject
        byte[] signature = key.Rsa.SignData(Encoding.ASCII.GetBytes(signingInput), hash, padding);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>
    /// The claims as an unsecured JWT (RFC 7519, section 6): <c>alg</c> of <c>none</c>, no
    /// signature.
    /// </summary>
    public static string Unsigned(JsonObject claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return Encode(new JsonObject { ["alg"] = "none" }) + "." + Encode(claims) + ".";
    }

    /// <summary>
    /// The claims of a JWS in compact serialization, without any verification.
    /// </summary>
    public static JsonObject Claims(string jws)
    {
        ArgumentNullException.ThrowIfNull(jws);
        string[] parts = jws.Split('.');
        if (parts.Length != 3)
        {
            throw new ArgumentException("Not a JWS in compact serialization", nameof(jws));
        }
        return JsonNode.Parse(Base64Url.DecodeFromChars(parts[1])) as JsonObject
            ?? throw new ArgumentException("The payload is not a JSON object", nameof(jws));
    }

    private static string Encode(JsonObject json)
    {
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json.ToJsonString(JsonOptions)));
    }

    private HttpResponseMessage Handle(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _requests);
        _lastUserAgent = request.Headers.UserAgent.ToString();
        string path = request.RequestUri?.AbsolutePath ?? "/";
        if (_documents.TryGetValue(path, out string? document))
        {
            return Respond(HttpStatusCode.OK, document);
        }
        if (!path.StartsWith(Realm + "/", StringComparison.Ordinal))
        {
            return Respond(HttpStatusCode.NotFound, null);
        }
        if (!Available)
        {
            return Respond(HttpStatusCode.ServiceUnavailable, null);
        }
        // the token, stream management and poll endpoints follow with the poll and stream
        // management slice of the port
        return Respond(HttpStatusCode.NotFound, null);
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json)
    {
        HttpResponseMessage response = new(status);
        if (json is not null)
        {
            response.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }
        return response;
    }

    public void Dispose()
    {
        _handler.Dispose();
        Key.Dispose();
    }

    private sealed class TestTransmitterHandler(TestTransmitter transmitter) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(transmitter.Handle(request));
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return transmitter.Handle(request);
        }
    }
}
