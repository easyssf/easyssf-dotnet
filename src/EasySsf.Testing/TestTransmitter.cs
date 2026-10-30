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
/// JWK Set, signs SETs and access tokens, issues access tokens for the client credentials grant and
/// offers stream management and a poll endpoint that behave like the ones of Keycloak. Everything
/// is served through an <see cref="HttpMessageHandler"/>: give the receiver under test an
/// <see cref="HttpClient"/> from <see cref="CreateHttpClient"/>, no socket is involved.
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

    private readonly List<JsonObject> _streams = [];

    private readonly OrderedDictionary<string, string> _queuedSets = new(StringComparer.Ordinal);

    private readonly Lock _queueLock = new();

    private TaskCompletionSource _setQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<string> _acknowledgedSets = [];

    private readonly OrderedDictionary<string, JsonNode?> _reportedErrors = new(StringComparer.Ordinal);

    private readonly List<string> _verificationRequests = [];

    private readonly List<JsonObject> _addedSubjects = [];

    private readonly List<JsonObject> _removedSubjects = [];

    private int _requests;

    private int _tokenRequests;

    private int _pollRequests;

    private volatile JsonObject _lastPollRequest = new();

    private volatile string _validAccessToken = DefaultAccessToken;

    private volatile string? _lastUserAgent;

    public TestTransmitter()
        : this(MetadataLocation.Ssf)
    {
    }

    /// <summary>
    /// A transmitter that publishes its metadata at the given location, or not at all.
    /// </summary>
    /// <param name="metadataLocation">where the metadata is published</param>
    /// <param name="host">
    /// the host of the issuer, <c>transmitter.example</c> if <see langword="null"/>; give every
    /// transmitter of a test its own, as a receiver tells transmitters apart by issuer
    /// </param>
    public static TestTransmitter WithMetadata(MetadataLocation metadataLocation, string? host = null)
    {
        return new TestTransmitter(metadataLocation, host);
    }

    private TestTransmitter(MetadataLocation metadataLocation, string? host = null)
    {
        Key = TestSigningKey.Generate(2048, "test-key");
        Issuer = "https://" + (host ?? "transmitter.example") + Realm;
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
    /// Whether the token, stream management and poll endpoints answer; <see langword="false"/>
    /// makes them answer with 503. Published documents (the JWK Set, the metadata) are served
    /// regardless.
    /// </summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// Makes the transmitter create streams with the given issuer instead of its own.
    /// </summary>
    public string? StreamIssuer { get; set; }

    /// <summary>
    /// Lets the transmitter create a stream per request. By default it allows one stream per
    /// receiver and answers a second create request with <c>409</c>, as Keycloak does.
    /// </summary>
    public bool SeveralStreamsAllowed { get; set; }

    /// <summary>
    /// How long a poll request without <c>returnImmediately: true</c> is held while no SET is
    /// queued. Zero by default: the transmitter then answers at once, as if it did not support long
    /// polling.
    /// </summary>
    public TimeSpan LongPollHold { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// The number of HTTP requests received so far.
    /// </summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>
    /// The number of token requests answered with a token.
    /// </summary>
    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    /// <summary>
    /// The number of poll requests received.
    /// </summary>
    public int PollRequests => Volatile.Read(ref _pollRequests);

    /// <summary>
    /// The body of the last poll request, empty if none was received.
    /// </summary>
    public JsonObject LastPollRequest => _lastPollRequest;

    /// <summary>
    /// The <c>User-Agent</c> header of the most recent request.
    /// </summary>
    public string? LastUserAgent => _lastUserAgent;

    /// <summary>
    /// The streams registered at the transmitter, a snapshot.
    /// </summary>
    public IReadOnlyList<JsonObject> Streams
    {
        get
        {
            lock (_streams)
            {
                return _streams.Select(stream => (JsonObject)stream.DeepClone()).ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// The <c>jti</c>s of the SETs a polling receiver acknowledged, a snapshot.
    /// </summary>
    public IReadOnlyList<string> AcknowledgedSets
    {
        get
        {
            lock (_queueLock)
            {
                return _acknowledgedSets.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// The errors a polling receiver reported, by <c>jti</c>, a snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> ReportedErrors
    {
        get
        {
            lock (_queueLock)
            {
                return new Dictionary<string, JsonNode?>(_reportedErrors, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The <c>state</c>s of the verification requests received, a snapshot.
    /// </summary>
    public IReadOnlyList<string> VerificationRequests
    {
        get
        {
            lock (_verificationRequests)
            {
                return _verificationRequests.ToList().AsReadOnly();
            }
        }
    }

    public IReadOnlyList<JsonObject> AddedSubjects
    {
        get
        {
            lock (_addedSubjects)
            {
                return _addedSubjects.ToList().AsReadOnly();
            }
        }
    }

    public IReadOnlyList<JsonObject> RemovedSubjects
    {
        get
        {
            lock (_removedSubjects)
            {
                return _removedSubjects.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// The number of SETs queued and not yet acknowledged.
    /// </summary>
    public int QueuedSetCount
    {
        get
        {
            lock (_queueLock)
            {
                return _queuedSets.Count;
            }
        }
    }

    /// <summary>
    /// Serves the transmitter's documents and endpoints to an <see cref="HttpClient"/>.
    /// </summary>
    public HttpMessageHandler Handler => _handler;

    /// <summary>
    /// A client whose requests the transmitter answers in-process.
    /// </summary>
    public HttpClient CreateHttpClient()
    {
        return new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Makes the access tokens issued so far invalid: requests with them are answered with 401 and
    /// the token endpoint issues another token.
    /// </summary>
    public void ExpireAccessTokens()
    {
        _validAccessToken = DefaultAccessToken + "-" + Guid.NewGuid();
    }

    /// <summary>
    /// Makes the given SET available at the poll endpoint.
    /// </summary>
    public void QueueSet(string set)
    {
        QueueSet(Claims(set)["jti"]!.GetValue<string>(), set);
    }

    public void QueueSet(string jti, string set)
    {
        lock (_queueLock)
        {
            _queuedSets[jti] = set;
            // wakes up held poll requests
            _setQueued.TrySetResult();
            _setQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Forgets streams, queued SETs and what the receiver reported.
    /// </summary>
    public void Reset()
    {
        lock (_streams)
        {
            _streams.Clear();
        }
        lock (_queueLock)
        {
            _queuedSets.Clear();
            _acknowledgedSets.Clear();
            _reportedErrors.Clear();
            _setQueued.TrySetResult();
            _setQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        lock (_verificationRequests)
        {
            _verificationRequests.Clear();
        }
        lock (_addedSubjects)
        {
            _addedSubjects.Clear();
        }
        lock (_removedSubjects)
        {
            _removedSubjects.Clear();
        }
        Volatile.Write(ref _pollRequests, 0);
        _lastPollRequest = new JsonObject();
        LongPollHold = TimeSpan.Zero;
        Available = true;
        _validAccessToken = DefaultAccessToken;
        StreamIssuer = null;
        SeveralStreamsAllowed = false;
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
    /// Registers a stream the way the transmitter does for a create request: assigns the identifier,
    /// the audience and, for POLL delivery, the poll endpoint.
    /// </summary>
    public JsonObject AddStream(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject stream = (JsonObject)request.DeepClone();
        if (stream["stream_id"] is not JsonValue)
        {
            stream["stream_id"] = Guid.NewGuid().ToString();
        }
        string streamId = stream["stream_id"]!.GetValue<string>();
        stream["iss"] = StreamIssuer ?? Issuer;
        stream["aud"] = new JsonArray(ClientId + "/" + streamId);
        stream["events_delivered"] = stream["events_requested"]?.DeepClone() ?? new JsonArray();
        JsonObject delivery = (stream["delivery"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
        delivery.Remove("authorization_header");
        if (delivery["method"]?.GetValue<string>() == "urn:ietf:rfc:8936")
        {
            delivery["endpoint_url"] = PollUri;
        }
        stream["delivery"] = delivery;
        lock (_streams)
        {
            _streams.Add(stream);
        }
        return (JsonObject)stream.DeepClone();
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

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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
        string endpoint = path[(Realm.Length + 1)..];
        string body = (request.Content is not null) ? await request.Content.ReadAsStringAsync(cancellationToken) : "";
        if (endpoint == "token")
        {
            return HandleToken(request, body);
        }
        AuthenticationHeaderValue? authorization = request.Headers.Authorization;
        if (authorization is null || authorization.Scheme != "Bearer" || authorization.Parameter != _validAccessToken)
        {
            return Respond(HttpStatusCode.Unauthorized, null);
        }
        JsonObject requestBody = (string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject) ?? new JsonObject();
        string? streamId = QueryParameter(request.RequestUri, "stream_id") ?? requestBody["stream_id"]?.GetValue<string>();
        switch (endpoint)
        {
            case "streams":
                return HandleStreams(request.Method, streamId, requestBody);
            case "streams/status":
                return Respond((FindStream(streamId) is not null) ? HttpStatusCode.OK : HttpStatusCode.NotFound,
                    new JsonObject
                    {
                        ["stream_id"] = streamId ?? "null",
                        ["status"] = requestBody["status"]?.GetValue<string>() ?? "enabled",
                    }.ToJsonString(JsonOptions));
            case "verify":
                lock (_verificationRequests)
                {
                    _verificationRequests.Add(requestBody["state"]?.ToJsonString().Trim('"') ?? "null");
                }
                return Respond(HttpStatusCode.NoContent, null);
            case "poll":
                return Respond(HttpStatusCode.OK, (await PollAsync(requestBody, cancellationToken)).ToJsonString(JsonOptions));
            case "subjects/add":
                lock (_addedSubjects)
                {
                    _addedSubjects.Add(Subject(requestBody));
                }
                return Respond(HttpStatusCode.OK, null);
            case "subjects/remove":
                lock (_removedSubjects)
                {
                    _removedSubjects.Add(Subject(requestBody));
                }
                return Respond(HttpStatusCode.NoContent, null);
            default:
                return Respond(HttpStatusCode.NotFound, null);
        }
    }

    private HttpResponseMessage HandleToken(HttpRequestMessage request, string form)
    {
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret));
        AuthenticationHeaderValue? authorization = request.Headers.Authorization;
        bool basic = authorization is { Scheme: "Basic" } && authorization.Parameter == credentials;
        bool authenticated = basic || (form.Contains("client_id=" + ClientId, StringComparison.Ordinal)
            && form.Contains("client_secret=" + ClientSecret, StringComparison.Ordinal));
        if (!authenticated || !form.Contains("grant_type=client_credentials", StringComparison.Ordinal))
        {
            return Respond(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_client" }.ToJsonString(JsonOptions));
        }
        Interlocked.Increment(ref _tokenRequests);
        return Respond(HttpStatusCode.OK, new JsonObject
        {
            ["access_token"] = _validAccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
        }.ToJsonString(JsonOptions));
    }

    private HttpResponseMessage HandleStreams(HttpMethod method, string? streamId, JsonObject request)
    {
        lock (_streams)
        {
            JsonObject? stream = FindStreamLocked(streamId);
            if (method == HttpMethod.Get)
            {
                if (streamId is null)
                {
                    JsonArray all = [];
                    foreach (JsonObject existing in _streams)
                    {
                        all.Add(existing.DeepClone());
                    }
                    return Respond(HttpStatusCode.OK, all.ToJsonString(JsonOptions));
                }
                return Respond((stream is not null) ? HttpStatusCode.OK : HttpStatusCode.NotFound, stream?.ToJsonString(JsonOptions));
            }
            if (method == HttpMethod.Post)
            {
                if (_streams.Count > 0 && !SeveralStreamsAllowed)
                {
                    return Respond(HttpStatusCode.Conflict, new JsonObject
                    {
                        ["error"] = "stream_error",
                        ["error_description"] = "Only one stream per receiver is allowed",
                    }.ToJsonString(JsonOptions));
                }
                return Respond(HttpStatusCode.Created, AddStream(request).ToJsonString(JsonOptions));
            }
            if (method == HttpMethod.Patch || method == HttpMethod.Put)
            {
                if (stream is null)
                {
                    return Respond(HttpStatusCode.NotFound, null);
                }
                JsonObject updated = (JsonObject)stream.DeepClone();
                foreach ((string name, JsonNode? value) in request)
                {
                    updated[name] = value?.DeepClone();
                }
                _streams.Remove(stream);
                return Respond(HttpStatusCode.OK, AddStream(updated).ToJsonString(JsonOptions));
            }
            if (method == HttpMethod.Delete)
            {
                if (stream is null)
                {
                    return Respond(HttpStatusCode.NotFound, null);
                }
                _streams.Remove(stream);
                return Respond(HttpStatusCode.NoContent, null);
            }
            return Respond(HttpStatusCode.MethodNotAllowed, null);
        }
    }

    private async Task<JsonObject> PollAsync(JsonObject request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pollRequests);
        _lastPollRequest = (JsonObject)request.DeepClone();
        int maxEvents = request["maxEvents"]?.GetValue<int>() ?? 10;
        bool returnImmediately = request["returnImmediately"]?.GetValue<bool>() ?? false;
        DateTimeOffset deadline = DateTimeOffset.UtcNow + LongPollHold;
        lock (_queueLock)
        {
            if (request["ack"] is JsonArray acks)
            {
                foreach (JsonNode? ack in acks)
                {
                    string jti = ack!.GetValue<string>();
                    if (_queuedSets.Remove(jti))
                    {
                        _acknowledgedSets.Add(jti);
                    }
                }
            }
            if (request["setErrs"] is JsonObject errors)
            {
                foreach ((string jti, JsonNode? error) in errors)
                {
                    _queuedSets.Remove(jti);
                    _reportedErrors[jti] = error?.DeepClone();
                }
            }
        }
        // a long poll (returnImmediately false or absent, RFC 8936 section 2.5) is held until a
        // SET is queued or the hold time elapses
        if (!returnImmediately && maxEvents > 0)
        {
            while (true)
            {
                Task queued;
                lock (_queueLock)
                {
                    if (_queuedSets.Count > 0)
                    {
                        break;
                    }
                    queued = _setQueued.Task;
                }
                TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                await Task.WhenAny(queued, Task.Delay(remaining, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        lock (_queueLock)
        {
            JsonObject sets = [];
            foreach ((string jti, string set) in _queuedSets.Take(maxEvents))
            {
                sets[jti] = set;
            }
            return new JsonObject { ["sets"] = sets, ["moreAvailable"] = _queuedSets.Count > sets.Count };
        }
    }

    private static JsonObject Subject(JsonObject request)
    {
        return (request["subject"] as JsonObject)?.DeepClone() as JsonObject ?? new JsonObject();
    }

    private JsonObject? FindStream(string? streamId)
    {
        lock (_streams)
        {
            return FindStreamLocked(streamId);
        }
    }

    private JsonObject? FindStreamLocked(string? streamId)
    {
        return (streamId is null) ? null : _streams.FirstOrDefault(stream => stream["stream_id"]?.GetValue<string>() == streamId);
    }

    private static string? QueryParameter(Uri? uri, string name)
    {
        if (uri is null || string.IsNullOrEmpty(uri.Query))
        {
            return null;
        }
        foreach (string parameter in uri.Query.TrimStart('?').Split('&'))
        {
            int separator = parameter.IndexOf('=');
            if (separator > 0 && Uri.UnescapeDataString(parameter[..separator]) == name)
            {
                return Uri.UnescapeDataString(parameter[(separator + 1)..]);
            }
        }
        return null;
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await transmitter.HandleAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Respond(HttpStatusCode.InternalServerError, new JsonObject { ["error"] = ex.ToString() }.ToJsonString(JsonOptions));
            }
        }
    }
}
