using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Net.Http.Headers;

using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Metrics;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Revocation;
using EasySsf.Receiver.Session;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting;

/// <summary>
/// Registers the SSF receiver with the dependency injection container.
/// </summary>
public static class SsfReceiverServiceCollectionExtensions
{
    /// <summary>
    /// Adds the receiver with the options bound from the <see cref="SsfReceiverDefaults.ConfigurationSection"/>
    /// section of the application's configuration, and the health check.
    /// </summary>
    public static SsfReceiverBuilder AddSsfReceiver(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Services.AddSsfReceiver(builder.Configuration.GetSection(SsfReceiverDefaults.ConfigurationSection));
    }

    /// <summary>
    /// Adds the receiver with the options bound from the given configuration section.
    /// </summary>
    public static SsfReceiverBuilder AddSsfReceiver(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SsfReceiverBuilder builder = services.AddSsfReceiver();
        services.Configure<SsfReceiverOptions>(configuration);
        return builder;
    }

    /// <summary>
    /// Adds the receiver with the options configured in code.
    /// </summary>
    public static SsfReceiverBuilder AddSsfReceiver(this IServiceCollection services, Action<SsfReceiverOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        SsfReceiverBuilder builder = services.AddSsfReceiver();
        services.Configure(configure);
        return builder;
    }

    /// <summary>
    /// Adds the receiver: options with validation on start, the <see cref="HttpClient"/>s that
    /// call the transmitters, the in-memory de-duplication store, metrics if the application has an
    /// <see cref="IMeterFactory"/>, the <see cref="SsfSetProcessor"/>, the
    /// <see cref="SsfTransmitters"/> built from the options, the <see cref="SsfPushHandler"/>, and
    /// the hosted service that runs the stream registration and polling. Services registered before
    /// this call are kept: register your own <see cref="ISsfJtiDedupStore"/>,
    /// <see cref="ISsfPollAckStore"/> or <see cref="ISsfReceiverMetrics"/> first to replace the
    /// defaults.
    /// </summary>
    public static SsfReceiverBuilder AddSsfReceiver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(SsfReceiverMarker)) is { ImplementationInstance: SsfReceiverMarker added })
        {
            return new SsfReceiverBuilder(services, added);
        }
        SsfReceiverMarker marker = new();
        services.AddSingleton(marker);
        services.AddOptions<SsfReceiverOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SsfReceiverOptions>, SsfReceiverOptionsValidator>());
        services.AddLogging();
        services.AddHttpClient(SsfReceiverDefaults.HttpClientName).ConfigureHttpClient((provider, client) =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            client.Timeout = options.Http.Timeout;
            Configure(client, options);
        });
        services.AddHttpClient(SsfReceiverDefaults.PollHttpClientName).ConfigureHttpClient((provider, client) =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            TimeSpan timeout = options.Http.Timeout;
            foreach (SsfTransmitterOptions transmitter in SafeTransmitters(options))
            {
                TimeSpan needed = SsfTransmitterFactory.PollHttpClientTimeout(transmitter, options.Http.Timeout);
                timeout = (needed > timeout) ? needed : timeout;
            }
            client.Timeout = timeout;
            Configure(client, options);
        });
        marker.TryAddDefaultStore<ISsfJtiDedupStore>(services, provider =>
        {
            SsfDedupOptions dedup = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value.Dedup;
            return new InMemorySsfJtiDedupStore(dedup.Capacity, dedup.Lease);
        });
        services.TryAddSingleton<ISsfReceiverMetrics>(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            IMeterFactory? meterFactory = provider.GetService<IMeterFactory>();
            if (!options.Metrics.Enabled || meterFactory is null)
            {
                return NullSsfReceiverMetrics.Instance;
            }
            // the transmitters are built after the metrics, so they are looked up per measurement
            return new MeterSsfReceiverMetrics(meterFactory, issuer => provider.GetService<SsfTransmitters>()?.NameOf(issuer) ?? issuer);
        });
        services.TryAddSingleton(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            SsfReceiverOptionsValidator.RegisterEventAliases(options);
            SsfSetProcessorOptions processorOptions = new()
            {
                Metrics = provider.GetRequiredService<ISsfReceiverMetrics>(),
                StreamVerifications = issuer => provider.GetRequiredService<SsfTransmitters>().StreamVerification(issuer),
                CriticalSubjectMembers = issuer => provider.GetRequiredService<SsfTransmitters>().CriticalSubjectMembers(issuer),
                // a handler may run longer than the lease: the claim is renewed meanwhile
                ClaimRenewal = options.Dedup.Enabled ? options.Dedup.Lease / 3 : null,
            };
            processorOptions.UnderstoodSubjectMembers.Clear();
            foreach (string member in options.EffectiveUnderstoodSubjectMembers)
            {
                processorOptions.UnderstoodSubjectMembers.Add(member);
            }
            // the issuer of a SET selects the transmitter that verifies it; the transmitters are
            // looked up when the first SET arrives, as their pollers need the processor
            ISsfSetVerifier verifier = new TransmittersVerifier(provider);
            ISsfJtiDedupStore? dedupStore = options.Dedup.Enabled ? provider.GetRequiredService<ISsfJtiDedupStore>() : null;
            return new SsfSetProcessor(verifier, dedupStore, provider.GetServices<ISsfEventHandler>(), processorOptions,
                provider.GetRequiredService<ILogger<SsfSetProcessor>>());
        });
        services.TryAddSingleton(provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            SsfReceiverOptionsValidator.ThrowIfInvalid(options);
            IHttpClientFactory httpClients = provider.GetRequiredService<IHttpClientFactory>();
            ILoggerFactory loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            SsfSetProcessor processor = provider.GetRequiredService<SsfSetProcessor>();
            ISsfReceiverMetrics metrics = provider.GetRequiredService<ISsfReceiverMetrics>();
            ISsfPollAckStore? ackStore = provider.GetService<ISsfPollAckStore>();
            IEnumerable<ISsfTransmitterCustomizer> customizers = provider.GetServices<ISsfTransmitterCustomizer>();
            List<SsfTransmitter> transmitters = [];
            foreach ((string name, SsfTransmitterOptions transmitterOptions) in options.ConfiguredTransmitters())
            {
                SsfTransmitterBuilder builder = SsfTransmitterFactory.Builder(name, transmitterOptions,
                    httpClients.CreateClient(SsfReceiverDefaults.HttpClientName), httpClients.CreateClient(SsfReceiverDefaults.PollHttpClientName),
                    processor, metrics, ackStore, loggerFactory);
                foreach (ISsfTransmitterCustomizer customizer in customizers)
                {
                    customizer.Customize(builder);
                }
                transmitters.Add(builder.Build());
            }
            try
            {
                return new SsfTransmitters(transmitters);
            }
            catch (ArgumentException ex)
            {
                throw new SsfReceiverConfigurationException(SsfReceiverDefaults.ConfigurationSection + ":Transmitters", null, ex.Message);
            }
        });
        services.TryAddSingleton(provider =>
        {
            SsfTransmitters transmitters = provider.GetRequiredService<SsfTransmitters>();
            SsfSetProcessor processor = provider.GetRequiredService<SsfSetProcessor>();
            ISsfReceiverMetrics metrics = provider.GetRequiredService<ISsfReceiverMetrics>();
            ILogger<SsfPushHandler> logger = provider.GetRequiredService<ILogger<SsfPushHandler>>();
            // with one transmitter the header is checked before the SET is even parsed
            return (transmitters.All.Count == 1)
                ? new SsfPushHandler(processor, transmitters.All[0].PushAuthorizationHeader, metrics, logger)
                : new SsfPushHandler(processor, transmitters.PushAuthorizationHeader, metrics, logger);
        });
        marker.TryAddDefaultStore<ISsfTokenRevocationStore>(services, provider =>
        {
            SsfReceiverOptions options = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value;
            return new InMemorySsfTokenRevocationStore(options.Revocation.Ttl);
        });
        services.AddSingleton<ISsfEventHandler>(provider =>
        {
            SsfRevocationOptions revocation = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value.Revocation;
            if (!revocation.Enabled)
            {
                return NullEventHandler.Instance;
            }
            return new SsfTokenRevocationEventHandler(provider.GetRequiredService<ISsfTokenRevocationStore>(), revocation.EffectiveSessionEventTypes,
                revocation.UserEventTypes, provider.GetRequiredService<ILogger<SsfTokenRevocationEventHandler>>());
        });
        services.AddSingleton<ISsfEventHandler>(provider =>
        {
            SsfSessionTerminationOptions termination = provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value.SessionTermination;
            ISsfSessionTerminator? terminator = provider.GetService<ISsfSessionTerminator>();
            if (!termination.Enabled || terminator is null)
            {
                return NullEventHandler.Instance;
            }
            return new SsfSessionTerminationEventHandler(terminator, termination.EffectiveSessionEventTypes, termination.EffectiveUserEventTypes,
                provider.GetRequiredService<ILogger<SsfSessionTerminationEventHandler>>());
        });
        services.TryAddSingleton<SsfReceiverHealthCheck>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SsfReceiverHostedService>());
        return new SsfReceiverBuilder(services, marker);
    }

    /// <summary>
    /// Marks that <see cref="AddSsfReceiver(IServiceCollection)"/> ran, so that a second call adds
    /// nothing twice, and remembers the stores it registered by default, so that
    /// <see cref="SsfReceiverBuilder.ReplaceDefaultStore{TStore}"/> replaces those and nothing else.
    /// </summary>
    internal sealed class SsfReceiverMarker
    {
        private readonly List<ServiceDescriptor> _defaultStores = [];

        public void TryAddDefaultStore<TStore>(IServiceCollection services, Func<IServiceProvider, TStore> factory)
                where TStore : class
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(TStore)))
            {
                return;
            }
            ServiceDescriptor descriptor = ServiceDescriptor.Singleton(factory);
            services.Add(descriptor);
            _defaultStores.Add(descriptor);
        }

        public bool IsDefaultStore(ServiceDescriptor descriptor)
        {
            return _defaultStores.Contains(descriptor);
        }
    }

    /// <summary>
    /// Stands in for an integration that is switched off or has nothing to work with.
    /// </summary>
    private sealed class NullEventHandler : ISsfEventHandler
    {
        public static NullEventHandler Instance { get; } = new();

        public Task HandleAsync(SsfEventContext eventContext, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static void Configure(HttpClient client, SsfReceiverOptions options)
    {
        // every call of the receiver buffers the response (HttpCompletionOption.ResponseContentRead),
        // so the limit applies to all of them. A value out of range is left to the validator, whose
        // message names the setting, rather than failing here
        if (options.Http.MaxResponseSize is > 0 and <= int.MaxValue)
        {
            client.MaxResponseContentBufferSize = options.Http.MaxResponseSize;
        }
        if (!string.IsNullOrWhiteSpace(options.Http.UserAgent)
                && ProductInfoHeaderValue.TryParse(options.Http.UserAgent, out ProductInfoHeaderValue? userAgent))
        {
            client.DefaultRequestHeaders.UserAgent.Add(userAgent);
        }
    }

    private static IEnumerable<SsfTransmitterOptions> SafeTransmitters(SsfReceiverOptions options)
    {
        try
        {
            return options.ConfiguredTransmitters().Values;
        }
        catch (SsfReceiverConfigurationException)
        {
            // reported by the validation
            return [];
        }
    }

    /// <summary>
    /// Verifies a SET with the transmitter that issued it, looked up on first use.
    /// </summary>
    private sealed class TransmittersVerifier(IServiceProvider provider) : ISsfSetVerifier
    {
        private SsfTransmitters? _transmitters;

        public Task<Core.Event.SsfEventToken> VerifyAsync(string encodedSet, CancellationToken cancellationToken = default)
        {
            _transmitters ??= provider.GetRequiredService<SsfTransmitters>();
            return _transmitters.Verifier.VerifyAsync(encodedSet, cancellationToken);
        }
    }
}

/// <summary>
/// Adds handlers and customizations to the receiver registered with
/// <see cref="SsfReceiverServiceCollectionExtensions.AddSsfReceiver(IServiceCollection)"/>.
/// </summary>
public sealed class SsfReceiverBuilder
{
    private readonly SsfReceiverServiceCollectionExtensions.SsfReceiverMarker _marker;

    internal SsfReceiverBuilder(IServiceCollection services, SsfReceiverServiceCollectionExtensions.SsfReceiverMarker marker)
    {
        Services = services;
        _marker = marker;
    }

    /// <summary>
    /// The services of the application.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Configures the options in code, after any configuration binding.
    /// </summary>
    public SsfReceiverBuilder Configure(Action<SsfReceiverOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Adds an event handler, invoked for every SET.
    /// </summary>
    public SsfReceiverBuilder AddEventHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
            where THandler : class, ISsfEventHandler
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISsfEventHandler, THandler>());
        return this;
    }

    /// <summary>
    /// Adds an event handler, invoked for every SET.
    /// </summary>
    public SsfReceiverBuilder AddEventHandler(ISsfEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Services.AddSingleton(handler);
        return this;
    }

    /// <summary>
    /// Adds an event handler built from the services, invoked for every SET.
    /// </summary>
    public SsfReceiverBuilder AddEventHandler(Func<IServiceProvider, ISsfEventHandler> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.AddSingleton(factory);
        return this;
    }

    /// <summary>
    /// Adds a handler for the SETs that contain the given event.
    /// </summary>
    /// <param name="aliasOrUri">an alias such as <c>CaepSessionRevoked</c> or an event type URI</param>
    /// <param name="handler">the handler</param>
    /// <seealso cref="SsfEventHandler.For"/>
    public SsfReceiverBuilder AddEventHandler(string aliasOrUri, Func<SsfEventContext, CancellationToken, Task> handler)
    {
        Services.AddSingleton(SsfEventHandler.For(aliasOrUri, handler));
        return this;
    }

    /// <summary>
    /// Replaces a store the receiver registered by default, the in-memory <see cref="ISsfJtiDedupStore"/>
    /// or <see cref="ISsfTokenRevocationStore"/>, and adds a store it did not register, such as the
    /// <see cref="ISsfPollAckStore"/>. A store the application registered itself is kept, so an
    /// integration like the database stores does not override the application's choice.
    /// </summary>
    /// <returns>whether the store was added or replaced</returns>
    public bool ReplaceDefaultStore<TStore>(Func<IServiceProvider, TStore> factory)
            where TStore : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        ServiceDescriptor? existing = Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(TStore));
        if (existing is null)
        {
            Services.AddSingleton(factory);
            return true;
        }
        if (!_marker.IsDefaultStore(existing))
        {
            return false;
        }
        Services.Remove(existing);
        Services.AddSingleton(factory);
        return true;
    }

    /// <summary>
    /// Adds a customizer that is called for every transmitter before it is built.
    /// </summary>
    public SsfReceiverBuilder AddTransmitterCustomizer(Action<SsfTransmitterBuilder> customize)
    {
        ArgumentNullException.ThrowIfNull(customize);
        Services.AddSingleton<ISsfTransmitterCustomizer>(new DelegateCustomizer(customize));
        return this;
    }

    /// <summary>
    /// Adds a customizer that is called for every transmitter before it is built.
    /// </summary>
    public SsfReceiverBuilder AddTransmitterCustomizer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCustomizer>()
            where TCustomizer : class, ISsfTransmitterCustomizer
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ISsfTransmitterCustomizer, TCustomizer>());
        return this;
    }

    private sealed class DelegateCustomizer(Action<SsfTransmitterBuilder> customize) : ISsfTransmitterCustomizer
    {
        public void Customize(SsfTransmitterBuilder builder)
        {
            customize(builder);
        }
    }
}
