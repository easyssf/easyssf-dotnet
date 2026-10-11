using EasySsf.Core;
using EasySsf.Core.Stream;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Builds the parts of a transmitter from its <see cref="SsfTransmitterOptions"/>. The hosting
/// integration uses it for every configured transmitter; an application that assembles a receiver
/// by hand can use it for the parts it does not want to build itself.
/// </summary>
public static class SsfTransmitterFactory
{
    /// <summary>
    /// The configuration prefix of the transmitter, for messages.
    /// </summary>
    public static string Prefix(string name)
    {
        return (name == SsfTransmitter.DefaultName) ? SsfReceiverDefaults.ConfigurationSection + ":"
            : SsfReceiverDefaults.ConfigurationSection + ":Transmitters:" + name + ":";
    }

    /// <exception cref="SsfReceiverConfigurationException">if no issuer is configured</exception>
    public static string Issuer(string name, SsfTransmitterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? issuer = options.TransmitterIssuer;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "TransmitterIssuer", issuer,
                "The issuer of the SSF transmitter must be configured");
        }
        return issuer;
    }

    public static SsfTransmitterUriPolicy UriPolicy(SsfTransmitterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.AllowInsecureHttp ? SsfTransmitterUriPolicy.Insecure : SsfTransmitterUriPolicy.Default;
    }

    /// <exception cref="SsfReceiverConfigurationException">if the issuer or the metadata URL is not acceptable</exception>
    public static SsfTransmitterMetadataResolver MetadataResolver(string name, SsfTransmitterOptions options, HttpClient httpClient,
            ILoggerFactory? loggerFactory = null)
    {
        string issuer = Issuer(name, options);
        ILoggerFactory loggers = loggerFactory ?? NullLoggerFactory.Instance;
        SsfTransmitterMetadataResolver resolver;
        try
        {
            resolver = new SsfTransmitterMetadataResolver(issuer, options.TransmitterMetadataUrl, httpClient, UriPolicy(options),
                loggers.CreateLogger<SsfTransmitterMetadataResolver>());
        }
        catch (ArgumentException ex)
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "TransmitterIssuer", issuer, ex.Message);
        }
        ILogger logger = loggers.CreateLogger(typeof(SsfTransmitterFactory));
        logger.LogInformation("SSF transmitter {Transmitter}: metadata is resolved from {MetadataUris}", Describe(name, issuer),
            string.Join(", ", resolver.MetadataUris));
        if (options.AllowInsecureHttp)
        {
            List<Uri> insecure = new Uri?[] { new(issuer), options.TransmitterJwksUrl }.Concat(resolver.MetadataUris)
                .OfType<Uri>()
                .Where(uri => SsfTransmitterUriPolicy.IsInsecure(uri) && !SsfTransmitterUriPolicy.IsLoopback(uri))
                .ToList();
            logger.LogWarning("{Prefix}AllowInsecureHttp is on, the SSF transmitter {Transmitter} is used without TLS. For development only{Insecure}",
                Prefix(name), Describe(name, issuer), (insecure.Count == 0) ? "." : ": " + string.Join(", ", insecure));
        }
        return resolver;
    }

    /// <summary>
    /// The verifier of the transmitter. The audience is the configured one; without one, the
    /// audience of the stream once it is registered; without a registered stream, any.
    /// </summary>
    /// <exception cref="SsfReceiverConfigurationException">if an algorithm or the JWK Set URL is not acceptable</exception>
    public static JwtSsfSetVerifier Verifier(string name, SsfTransmitterOptions options, HttpClient httpClient,
            SsfTransmitterMetadataResolver metadataResolver, SsfReceiverStream receiverStream, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(metadataResolver);
        ArgumentNullException.ThrowIfNull(receiverStream);
        SsfSetValidationOptions validation = options.SetValidation;
        SsfSetVerifierOptions verifierOptions = new()
        {
            MinRsaKeySize = validation.MinRsaKeySize,
            RequireTypeHeader = validation.RequireTypeHeader,
            SubjectCompatibility = validation.SubjectCompatibility,
            ClockSkew = validation.ClockSkew,
        };
        verifierOptions.AcceptedAlgorithms.Clear();
        foreach (string algorithm in validation.EffectiveAcceptedAlgorithms)
        {
            verifierOptions.AcceptedAlgorithms.Add(algorithm);
        }
        if (!string.IsNullOrWhiteSpace(options.ExpectedAudience))
        {
            verifierOptions.ExpectedAudiences.Add(options.ExpectedAudience);
        }
        else if (RegistersStream(options))
        {
            // the transmitter tells the audience of the stream
            verifierOptions.ExpectedAudiencesProvider = () => receiverStream.Audience;
        }
        else
        {
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(typeof(SsfTransmitterFactory)).LogWarning(
                "{Prefix}ExpectedAudience is not set, SETs that the transmitter {Transmitter} issued for other receivers are accepted as well",
                Prefix(name), Describe(name, options.TransmitterIssuer ?? ""));
        }
        try
        {
            return new JwtSsfSetVerifier(Issuer(name, options), JwkSetUri(name, options, metadataResolver), httpClient, verifierOptions);
        }
        catch (ArgumentException ex)
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "SetValidation:AcceptedAlgorithms",
                string.Join(", ", validation.EffectiveAcceptedAlgorithms), ex.Message);
        }
    }

    /// <exception cref="SsfReceiverConfigurationException">if a token endpoint is configured without client credentials</exception>
    public static ISsfTransmitterTokenProvider TokenProvider(string name, SsfTransmitterOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.TransmitterAccessToken))
        {
            return SsfTransmitterTokenProviders.Fixed(options.TransmitterAccessToken);
        }
        SsfOAuth2Options oauth2 = options.OAuth2;
        if (oauth2.TokenUri is null)
        {
            return SsfTransmitterTokenProviders.None;
        }
        if (string.IsNullOrWhiteSpace(oauth2.ClientId) || string.IsNullOrWhiteSpace(oauth2.ClientSecret))
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "OAuth2:ClientId", oauth2.ClientId,
                $"{Prefix(name)}OAuth2:ClientId and {Prefix(name)}OAuth2:ClientSecret are required to obtain access tokens from {Prefix(name)}OAuth2:TokenUri");
        }
        SsfClientCredentialsOptions credentials = new()
        {
            AuthenticateWithRequestBody = oauth2.ClientAuthenticationMethod == SsfClientAuthenticationMethod.Post,
            ExpirySafetyWindow = oauth2.ExpirySafetyWindow,
        };
        foreach (string scope in oauth2.Scopes)
        {
            credentials.Scopes.Add(scope);
        }
        foreach ((string parameter, string value) in oauth2.AdditionalParameters)
        {
            credentials.AdditionalParameters[parameter] = value;
        }
        return new ClientCredentialsSsfTransmitterTokenProvider(httpClient, oauth2.TokenUri, oauth2.ClientId, oauth2.ClientSecret, credentials);
    }

    public static SsfStreamClient StreamClient(HttpClient httpClient, ISsfTransmitterTokenProvider tokenProvider,
            SsfTransmitterMetadataResolver metadataResolver)
    {
        return new SsfStreamClient(httpClient, tokenProvider, metadataResolver);
    }

    public static SsfStreamVerification StreamVerification(SsfReceiverStream receiverStream, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(receiverStream);
        return new SsfStreamVerification((loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SsfStreamVerification>())
        {
            StreamId = () => receiverStream.StreamId,
        };
    }

    /// <returns>the registrar, <see langword="null"/> if the stream is neither looked up nor managed</returns>
    /// <exception cref="SsfReceiverConfigurationException">if a managed stream is not configured completely</exception>
    public static SsfStreamRegistrar? StreamRegistrar(string name, SsfTransmitterOptions options, SsfStreamClient streamClient,
            SsfReceiverStream receiverStream, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!RegistersStream(options))
        {
            return null;
        }
        ILogger<SsfStreamRegistrar> logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SsfStreamRegistrar>();
        SsfStreamOptions stream = options.Stream;
        if (stream.Management != SsfStreamManagement.Receiver)
        {
            return Stream.SsfStreamRegistrar.ForExistingStream(streamClient, receiverStream, stream.Id!, logger: logger);
        }
        SsfStreamRegistrar registrar = Stream.SsfStreamRegistrar.ForManagedStream(streamClient, receiverStream, DesiredStream(name, options),
            logger: logger);
        registrar.DeleteOnShutdown = stream.DeleteOnShutdown;
        return registrar;
    }

    /// <returns>the poller, <see langword="null"/> with PUSH delivery</returns>
    /// <exception cref="SsfReceiverConfigurationException">if the poll endpoint cannot be known</exception>
    public static SsfPoller? Poller(string name, SsfTransmitterOptions options, HttpClient httpClient, ISsfTransmitterTokenProvider tokenProvider,
            SsfSetProcessor processor, SsfReceiverStream receiverStream, ISsfReceiverMetrics? metrics = null, ISsfPollAckStore? ackStore = null,
            ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DeliveryMethod != SsfDeliveryMethod.Poll)
        {
            return null;
        }
        SsfPollOptions poll = options.Poll;
        SsfPollerOptions pollerOptions = new()
        {
            Transmitter = options.TransmitterIssuer,
            Metrics = metrics ?? NullSsfReceiverMetrics.Instance,
            Interval = poll.Interval,
            InitialDelay = poll.InitialDelay,
            MaxEvents = poll.MaxEvents,
            LongPollingHold = poll.LongPolling ? poll.LongPollingHold : null,
            RateLimitFallback = poll.RateLimit.FallbackBackoff,
            MaxPause = poll.RateLimit.MaxBackoff,
        };
        if (ackStore is not null)
        {
            pollerOptions.AckStore = ackStore;
        }
        try
        {
            return new SsfPoller(httpClient, tokenProvider, PollEndpoint(name, options, receiverStream), processor, pollerOptions,
                (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<SsfPoller>());
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "Poll", null, ex.Message);
        }
    }

    /// <summary>
    /// The timeout the poll client of a transmitter needs: the hold time plus a margin when long
    /// polling, else the regular one.
    /// </summary>
    public static TimeSpan PollHttpClientTimeout(SsfTransmitterOptions options, TimeSpan regularTimeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.DeliveryMethod != SsfDeliveryMethod.Poll || !options.Poll.LongPolling)
        {
            return regularTimeout;
        }
        TimeSpan longPoll = options.Poll.LongPollingHold + TimeSpan.FromSeconds(15);
        return (longPoll > regularTimeout) ? longPoll : regularTimeout;
    }

    /// <summary>
    /// A builder with every part of the transmitter built from its options.
    /// </summary>
    /// <param name="name">the name of the transmitter</param>
    /// <param name="options">its options</param>
    /// <param name="httpClient">calls the transmitter for metadata, keys, tokens and stream management</param>
    /// <param name="pollHttpClient">calls the poll endpoint; its timeout has to allow for a long poll</param>
    /// <param name="processor">processes the polled SETs</param>
    /// <param name="metrics">records what the poller does, nothing if <see langword="null"/></param>
    /// <param name="ackStore">keeps the acknowledgements a poller owes the transmitter, the in-memory default if <see langword="null"/></param>
    /// <param name="loggerFactory">creates the loggers of the parts, none if <see langword="null"/></param>
    public static SsfTransmitterBuilder Builder(string name, SsfTransmitterOptions options, HttpClient httpClient, HttpClient pollHttpClient,
            SsfSetProcessor processor, ISsfReceiverMetrics? metrics = null, ISsfPollAckStore? ackStore = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(pollHttpClient);
        ArgumentNullException.ThrowIfNull(processor);
        SsfReceiverStream receiverStream = new();
        SsfTransmitterMetadataResolver metadataResolver = MetadataResolver(name, options, httpClient, loggerFactory);
        ISsfTransmitterTokenProvider tokenProvider = TokenProvider(name, options, httpClient);
        SsfStreamClient streamClient = StreamClient(httpClient, tokenProvider, metadataResolver);
        return new SsfTransmitterBuilder(name, Issuer(name, options))
        {
            MetadataResolver = metadataResolver,
            Verifier = Verifier(name, options, httpClient, metadataResolver, receiverStream, loggerFactory),
            TokenProvider = tokenProvider,
            StreamClient = streamClient,
            ReceiverStream = receiverStream,
            StreamVerification = StreamVerification(receiverStream, loggerFactory),
            StreamRegistrar = StreamRegistrar(name, options, streamClient, receiverStream, loggerFactory),
            Poller = Poller(name, options, pollHttpClient, tokenProvider, processor, receiverStream, metrics, ackStore, loggerFactory),
            AutoStartPolling = options.Poll.AutoStartup,
            PushAuthorizationHeader = options.Push.ExpectedAuthHeader,
        };
    }

    /// <summary>
    /// Whether the stream of the transmitter is looked up or managed on startup.
    /// </summary>
    public static bool RegistersStream(SsfTransmitterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Stream.Management == SsfStreamManagement.Receiver || !string.IsNullOrWhiteSpace(options.Stream.Id);
    }

    /// <summary>
    /// Checks the options of a transmitter the way the factory does when it builds the parts.
    /// </summary>
    /// <returns>what is wrong, empty if the options are usable</returns>
    public static IReadOnlyList<string> Validate(string name, SsfTransmitterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        string prefix = Prefix(name);
        if (string.IsNullOrWhiteSpace(options.TransmitterIssuer))
        {
            failures.Add(prefix + "TransmitterIssuer: The issuer of the SSF transmitter must be configured");
            return failures;
        }
        SsfTransmitterUriPolicy policy = UriPolicy(options);
        Check(failures, prefix + "TransmitterIssuer", () => policy.CheckIssuer(options.TransmitterIssuer));
        if (options.TransmitterMetadataUrl is not null)
        {
            Check(failures, prefix + "TransmitterMetadataUrl", () => policy.CheckEndpoint(options.TransmitterMetadataUrl, "Transmitter metadata URL"));
        }
        if (options.TransmitterJwksUrl is not null)
        {
            Check(failures, prefix + "TransmitterJwksUrl", () => policy.CheckEndpoint(options.TransmitterJwksUrl, "The JWK Set URL"));
        }
        foreach (string algorithm in options.SetValidation.EffectiveAcceptedAlgorithms)
        {
            if (!SsfSetVerifierOptions.SupportedAlgorithms.Contains(algorithm.Trim()))
            {
                failures.Add(prefix + "SetValidation:AcceptedAlgorithms: Not an asymmetric signature algorithm: " + algorithm);
            }
        }
        if (string.IsNullOrWhiteSpace(options.TransmitterAccessToken) && options.OAuth2.TokenUri is not null
                && (string.IsNullOrWhiteSpace(options.OAuth2.ClientId) || string.IsNullOrWhiteSpace(options.OAuth2.ClientSecret)))
        {
            failures.Add($"{prefix}OAuth2:ClientId: {prefix}OAuth2:ClientId and {prefix}OAuth2:ClientSecret are required to obtain access tokens from {prefix}OAuth2:TokenUri");
        }
        if (options.Stream.Management == SsfStreamManagement.Receiver)
        {
            if (options.DeliveryMethod == SsfDeliveryMethod.Push && options.Push.DeliveryEndpointUrl is null)
            {
                failures.Add(prefix + "Push:DeliveryEndpointUrl: A stream managed by the receiver needs the URL under which the transmitter"
                    + " reaches the push endpoint of this application");
            }
        }
        if (options.DeliveryMethod == SsfDeliveryMethod.Poll && options.Poll.EndpointUrl is null && !RegistersStream(options))
        {
            failures.Add($"{prefix}Poll:EndpointUrl: POLL delivery needs the poll endpoint of the stream: set {prefix}Poll:EndpointUrl,"
                + $" {prefix}Stream:Id or {prefix}Stream:Management=Receiver");
        }
        if (options.DeliveryMethod == SsfDeliveryMethod.Poll)
        {
            SsfPollOptions poll = options.Poll;
            if (poll.Interval <= TimeSpan.Zero || poll.InitialDelay < TimeSpan.Zero || poll.MaxEvents <= 0 || poll.RateLimit.MaxBackoff <= TimeSpan.Zero
                    || (poll.LongPolling && poll.LongPollingHold <= TimeSpan.Zero) || poll.RateLimit.FallbackBackoff is { } fallback && fallback < TimeSpan.Zero)
            {
                failures.Add(prefix + "Poll: Interval, LongPollingHold and RateLimit:MaxBackoff must be positive, InitialDelay and"
                    + " RateLimit:FallbackBackoff not negative, MaxEvents at least 1");
            }
        }
        return failures;
    }

    private static void Check(List<string> failures, string key, Action check)
    {
        try
        {
            check();
        }
        catch (ArgumentException ex)
        {
            failures.Add(key + ": " + ex.Message);
        }
    }

    private static SsfStreamConfiguration DesiredStream(string name, SsfTransmitterOptions options)
    {
        SsfStreamOptions stream = options.Stream;
        if (options.DeliveryMethod == SsfDeliveryMethod.Poll)
        {
            return SsfStreamConfiguration.Poll(stream.EffectiveEventsRequested, stream.Description);
        }
        Uri? deliveryEndpointUrl = options.Push.DeliveryEndpointUrl;
        if (deliveryEndpointUrl is null)
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "Push:DeliveryEndpointUrl", null,
                "A stream managed by the receiver needs the URL under which the transmitter reaches the push endpoint of this application");
        }
        string? authorizationHeader = options.Push.ExpectedAuthHeader;
        return SsfStreamConfiguration.Push(deliveryEndpointUrl, string.IsNullOrWhiteSpace(authorizationHeader) ? null : authorizationHeader,
            stream.EffectiveEventsRequested, stream.Description);
    }

    private static Func<Uri?> PollEndpoint(string name, SsfTransmitterOptions options, SsfReceiverStream receiverStream)
    {
        Uri? configured = options.Poll.EndpointUrl;
        if (configured is not null)
        {
            return () => configured;
        }
        if (!RegistersStream(options))
        {
            throw new SsfReceiverConfigurationException(Prefix(name) + "Poll:EndpointUrl", null,
                $"POLL delivery needs the poll endpoint of the stream: set {Prefix(name)}Poll:EndpointUrl, {Prefix(name)}Stream:Id or"
                    + $" {Prefix(name)}Stream:Management=Receiver");
        }
        return () =>
        {
            SsfStreamConfiguration? stream = receiverStream.Configuration;
            return (stream is not null && stream.DeliveryMethod == SsfDeliveryMethod.Poll.Uri()) ? stream.DeliveryEndpointUrl : null;
        };
    }

    private static Func<CancellationToken, ValueTask<Uri>> JwkSetUri(string name, SsfTransmitterOptions options,
            SsfTransmitterMetadataResolver metadataResolver)
    {
        Uri? configured = options.TransmitterJwksUrl;
        if (configured is not null)
        {
            try
            {
                UriPolicy(options).CheckEndpoint(configured, "The JWK Set URL");
            }
            catch (ArgumentException ex)
            {
                throw new SsfReceiverConfigurationException(Prefix(name) + "TransmitterJwksUrl", configured, ex.Message);
            }
            return _ => ValueTask.FromResult(configured);
        }
        return async cancellationToken =>
        {
            Uri? discovered = (await metadataResolver.ResolveAsync(cancellationToken).ConfigureAwait(false)).JwksUri;
            return discovered ?? throw new SsfTransmitterUnavailableException(
                "The SSF transmitter metadata has no jwks_uri, configure " + Prefix(name) + "TransmitterJwksUrl");
        };
    }

    private static string Describe(string name, string issuer)
    {
        return (name == SsfTransmitter.DefaultName) ? issuer : $"'{name}' ({issuer})";
    }
}
