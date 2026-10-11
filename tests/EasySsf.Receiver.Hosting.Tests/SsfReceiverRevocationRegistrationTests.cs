using System.Text;

using EasySsf.Core.Event;
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Revocation;
using EasySsf.Receiver.Session;
using EasySsf.Testing;

using Microsoft.Extensions.DependencyInjection;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Hosting.Tests;

/// <summary>
/// The revocation store and the handlers that fill it, registered from the options.
/// </summary>
public sealed class SsfReceiverRevocationRegistrationTests : IDisposable
{
    private readonly TestTransmitter _transmitter = new();

    public void Dispose()
    {
        _transmitter.Dispose();
    }

    [Fact]
    public async Task SessionRevokedEventRevokesTheSessionInTheStore()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter);
        using ServiceProvider provider = services.BuildServiceProvider();
        ISsfTokenRevocationStore store = provider.GetRequiredService<ISsfTokenRevocationStore>();
        Assert.IsType<InMemorySsfTokenRevocationStore>(store);
        Assert.Equal(TimeSpan.FromHours(1), ((InMemorySsfTokenRevocationStore)store).Ttl);

        string set = _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        Assert.Equal(202, (await provider.GetRequiredService<SsfPushHandler>().HandleAsync(null, Encoding.UTF8.GetBytes(set))).Status);
        Assert.True(await store.IsRevokedAsync(_transmitter.Issuer, "session-1", "alice", DateTimeOffset.UtcNow));
        Assert.False(await store.IsRevokedAsync(_transmitter.Issuer, "session-2", "alice", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task UserEventTypesAndTtlAreConfigurable()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter,
            ("EasySsf:Receiver:Revocation:UserEventTypes:0", "CaepCredentialChange"),
            ("EasySsf:Receiver:Revocation:Ttl", "00:30:00")), _transmitter);
        using ServiceProvider provider = services.BuildServiceProvider();
        ISsfTokenRevocationStore store = provider.GetRequiredService<ISsfTokenRevocationStore>();
        Assert.Equal(TimeSpan.FromMinutes(30), ((InMemorySsfTokenRevocationStore)store).Ttl);
        string set = _transmitter.Set("CaepCredentialChange", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        Assert.Equal(202, (await provider.GetRequiredService<SsfPushHandler>().HandleAsync(null, Encoding.UTF8.GetBytes(set))).Status);
        // the user is revoked, not the session the event names
        Assert.True(await store.IsRevokedAsync(_transmitter.Issuer, null, "alice", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.False(await store.IsSessionRevokedAsync(_transmitter.Issuer, "session-1"));
    }

    [Fact]
    public async Task RevocationCanBeSwitchedOff()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter, ("EasySsf:Receiver:Revocation:Enabled", "false")), _transmitter);
        using ServiceProvider provider = services.BuildServiceProvider();
        string set = _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        Assert.Equal(202, (await provider.GetRequiredService<SsfPushHandler>().HandleAsync(null, Encoding.UTF8.GetBytes(set))).Status);
        Assert.False(await provider.GetRequiredService<ISsfTokenRevocationStore>().IsSessionRevokedAsync(_transmitter.Issuer, "session-1"));
        Assert.DoesNotContain(provider.GetServices<ISsfEventHandler>(), handler => handler is SsfTokenRevocationEventHandler);
    }

    [Fact]
    public async Task SessionsAreTerminatedThroughTheRegisteredTerminator()
    {
        ServiceCollection services = new();
        RecordingTerminator terminator = new();
        services.AddSingleton<ISsfSessionTerminator>(terminator);
        services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter);
        using ServiceProvider provider = services.BuildServiceProvider();
        SsfPushHandler pushHandler = provider.GetRequiredService<SsfPushHandler>();
        string revoked = _transmitter.Set("CaepSessionRevoked", Complex(IssSub(_transmitter.Issuer, "alice"), Opaque("session-1")));
        string changed = _transmitter.Set("CaepCredentialChange", Complex(IssSub(_transmitter.Issuer, "bob"), Opaque("session-2")));
        Assert.Equal(202, (await pushHandler.HandleAsync(null, Encoding.UTF8.GetBytes(revoked))).Status);
        Assert.Equal(202, (await pushHandler.HandleAsync(null, Encoding.UTF8.GetBytes(changed))).Status);
        Assert.Equal(["session-1", null], terminator.Subjects.Select(s => s.SessionId));
        Assert.Equal(["alice", "bob"], terminator.Subjects.Select(s => s.Subject));
    }

    [Fact]
    public void WithoutATerminatorNoTerminationHandlerIsActive()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.DoesNotContain(provider.GetServices<ISsfEventHandler>(), handler => handler is SsfSessionTerminationEventHandler);
        Assert.Contains(provider.GetServices<ISsfEventHandler>(), handler => handler is SsfTokenRevocationEventHandler);
    }

    [Fact]
    public void AddingTheReceiverTwiceRegistersNothingTwice()
    {
        ServiceCollection services = new();
        services.AddReceiver(ReceiverServices.Settings(_transmitter), _transmitter);
        services.AddSsfReceiver();
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ISsfEventHandler>().OfType<SsfTokenRevocationEventHandler>());
    }

    private sealed class RecordingTerminator : ISsfSessionTerminator
    {
        public List<SsfSubject> Subjects { get; } = [];

        public ValueTask<int> TerminateSessionsAsync(SsfSubject subject, CancellationToken cancellationToken = default)
        {
            Subjects.Add(subject);
            return ValueTask.FromResult(1);
        }
    }
}
