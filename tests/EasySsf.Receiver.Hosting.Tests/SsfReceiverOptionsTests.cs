using EasySsf.Core;
using EasySsf.Core.Event;
using EasySsf.Receiver.Hosting;
using EasySsf.Receiver.Transmitter;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting.Tests;

public class SsfReceiverOptionsTests
{
    [Fact]
    public void BindsEveryKindOfSetting()
    {
        Dictionary<string, string?> settings = new()
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = "https://idp.example/realms/test",
            ["EasySsf:Receiver:TransmitterJwksUrl"] = "https://idp.example/realms/test/jwks",
            ["EasySsf:Receiver:DeliveryMethod"] = "Poll",
            ["EasySsf:Receiver:ExpectedAudience"] = "https://app.example",
            ["EasySsf:Receiver:EventAliases:AcmeLogin"] = "https://events.acme.example/login",
            ["EasySsf:Receiver:UnderstoodSubjectMembers:0"] = "user",
            ["EasySsf:Receiver:UnderstoodSubjectMembers:1"] = "custom",
            ["EasySsf:Receiver:Http:Timeout"] = "00:00:10",
            ["EasySsf:Receiver:Http:UserAgent"] = "my-receiver/1.0",
            ["EasySsf:Receiver:Dedup:Capacity"] = "500",
            ["EasySsf:Receiver:Dedup:Lease"] = "00:02:00",
            ["EasySsf:Receiver:Metrics:Enabled"] = "false",
            ["EasySsf:Receiver:OAuth2:TokenUri"] = "https://idp.example/token",
            ["EasySsf:Receiver:OAuth2:ClientId"] = "receiver",
            ["EasySsf:Receiver:OAuth2:ClientSecret"] = "secret",
            ["EasySsf:Receiver:OAuth2:Scopes:0"] = "ssf.read",
            ["EasySsf:Receiver:OAuth2:ClientAuthenticationMethod"] = "Post",
            ["EasySsf:Receiver:OAuth2:AdditionalParameters:audience"] = "https://tr.example",
            ["EasySsf:Receiver:Stream:Management"] = "Receiver",
            ["EasySsf:Receiver:Stream:EventsRequested:0"] = "AcmeLogin",
            ["EasySsf:Receiver:Stream:Description"] = "test",
            ["EasySsf:Receiver:Poll:Interval"] = "00:00:05",
            ["EasySsf:Receiver:Poll:LongPolling"] = "true",
            ["EasySsf:Receiver:Poll:RateLimit:FallbackBackoff"] = "00:01:00",
            ["EasySsf:Receiver:SetValidation:AcceptedAlgorithms:0"] = "RS256",
            ["EasySsf:Receiver:SetValidation:AcceptedAlgorithms:1"] = "ES256",
            ["EasySsf:Receiver:SetValidation:SubjectCompatibility"] = "Legacy",
            ["EasySsf:Receiver:Push:ExpectedAuthHeader"] = "Bearer secret",
            ["EasySsf:Receiver:Push:EndpointPath"] = "/events",
            ["EasySsf:Receiver:Transmitters:okta:TransmitterIssuer"] = "https://okta.example",
            ["EasySsf:Receiver:Transmitters:okta:Poll:MaxEvents"] = "7",
        };
        SsfReceiverOptions options = Bind(settings);
        Assert.Equal("https://idp.example/realms/test", options.TransmitterIssuer);
        Assert.Equal(new Uri("https://idp.example/realms/test/jwks"), options.TransmitterJwksUrl);
        Assert.Equal(SsfDeliveryMethod.Poll, options.DeliveryMethod);
        Assert.Equal("https://app.example", options.ExpectedAudience);
        Assert.Equal("https://events.acme.example/login", options.EventAliases["AcmeLogin"]);
        Assert.Equal(["user", "custom"], options.UnderstoodSubjectMembers);
        Assert.Equal(["user", "custom"], options.EffectiveUnderstoodSubjectMembers);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Http.Timeout);
        Assert.Equal("my-receiver/1.0", options.Http.UserAgent);
        Assert.Equal(500, options.Dedup.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Dedup.Lease);
        Assert.False(options.Metrics.Enabled);
        Assert.Equal(new Uri("https://idp.example/token"), options.OAuth2.TokenUri);
        Assert.Equal(["ssf.read"], options.OAuth2.Scopes);
        Assert.Equal(SsfClientAuthenticationMethod.Post, options.OAuth2.ClientAuthenticationMethod);
        Assert.Equal("https://tr.example", options.OAuth2.AdditionalParameters["audience"]);
        Assert.Equal(SsfStreamManagement.Receiver, options.Stream.Management);
        Assert.Equal(["AcmeLogin"], options.Stream.EventsRequested);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Poll.Interval);
        Assert.True(options.Poll.LongPolling);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Poll.RateLimit.FallbackBackoff);
        Assert.Equal(["RS256", "ES256"], options.SetValidation.AcceptedAlgorithms);
        Assert.Equal(SubjectCompatibilityMode.Legacy, options.SetValidation.SubjectCompatibility);
        Assert.Equal("Bearer secret", options.Push.ExpectedAuthHeader);
        Assert.Equal("/events", options.Push.EndpointPath);
        Assert.Equal(7, options.Transmitters["okta"].Poll.MaxEvents);
        Assert.Equal(["default", "okta"], options.ConfiguredTransmitters().Keys);
        Assert.Same(options, options.ConfiguredTransmitters()[SsfTransmitter.DefaultName]);
    }

    [Fact]
    public void DefaultsMatchTheJavaStarter()
    {
        SsfReceiverOptions options = new();
        Assert.True(options.Enabled);
        Assert.Equal(SsfDeliveryMethod.Push, options.DeliveryMethod);
        Assert.Empty(options.Stream.EventsRequested);
        Assert.Equal(["CaepSessionRevoked", "CaepCredentialChange"], options.Stream.EffectiveEventsRequested);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Poll.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Poll.InitialDelay);
        Assert.Equal(100, options.Poll.MaxEvents);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Poll.RateLimit.MaxBackoff);
        Assert.Empty(options.SetValidation.AcceptedAlgorithms);
        Assert.Equal(["RS256"], options.SetValidation.EffectiveAcceptedAlgorithms);
        Assert.Equal(2048, options.SetValidation.MinRsaKeySize);
        Assert.Equal("/ssf/push", options.Push.EndpointPath);
        Assert.Equal(10_000, options.Dedup.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Dedup.Lease);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Http.Timeout);
        Assert.Empty(options.UnderstoodSubjectMembers);
        Assert.Equal(SsfSubject.Members, options.EffectiveUnderstoodSubjectMembers);
        Assert.False(options.HasDefaultTransmitter);
        Assert.Empty(options.ConfiguredTransmitters());
    }

    [Fact]
    public void NamedTransmittersWorkWithoutTheDefaultOne()
    {
        SsfReceiverOptions options = Bind(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:Transmitters:a:TransmitterIssuer"] = "https://a.example",
            ["EasySsf:Receiver:Transmitters:b:TransmitterIssuer"] = "https://b.example",
        });
        Assert.False(options.HasDefaultTransmitter);
        Assert.Equal(["a", "b"], options.ConfiguredTransmitters().Keys);
    }

    [Fact]
    public void RejectsNamedTransmitterCalledDefaultNextToTheDefaultOne()
    {
        SsfReceiverOptions options = Bind(new Dictionary<string, string?>
        {
            ["EasySsf:Receiver:TransmitterIssuer"] = "https://idp.example",
            ["EasySsf:Receiver:Transmitters:default:TransmitterIssuer"] = "https://other.example",
        });
        SsfReceiverConfigurationException ex = Assert.Throws<SsfReceiverConfigurationException>(() => options.ConfiguredTransmitters());
        Assert.Contains("EasySsf:Receiver:Transmitters", ex.Message);
        Assert.Equal("default", ex.Value);
    }

    private static SsfReceiverOptions Bind(Dictionary<string, string?> settings)
    {
        ServiceCollection services = new();
        services.AddSsfReceiver(ReceiverServices.Configuration(settings).GetSection(SsfReceiverDefaults.ConfigurationSection));
        using ServiceProvider provider = services.BuildServiceProvider();
        // the options are bound without validation here, the validator has tests of its own
        SsfReceiverOptions options = new();
        foreach (IConfigureOptions<SsfReceiverOptions> configure in provider.GetServices<IConfigureOptions<SsfReceiverOptions>>())
        {
            configure.Configure(options);
        }
        return options;
    }
}
