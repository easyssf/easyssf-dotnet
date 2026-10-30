using EasySsf.Core;
using EasySsf.Receiver.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EasySsf.Receiver.Hosting.Tests;

public class SsfReceiverOptionsValidatorTests
{
    private readonly SsfReceiverOptionsValidator _validator = new();

    [Fact]
    public void AcceptsAPushReceiverWithAnIssuer()
    {
        Assert.True(Validate(options => options.TransmitterIssuer = "https://idp.example/realms/test").Succeeded);
    }

    [Fact]
    public void AcceptsADisabledReceiverWhateverElseIsSet()
    {
        Assert.True(Validate(options => options.Enabled = false).Succeeded);
    }

    [Fact]
    public void AudienceMustBeKnownUnlessAnyIsAllowed()
    {
        // neither an expected audience nor a stream the receiver looks up: the receiver would accept
        // SETs the transmitter issued for other receivers
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.ExpectedAudience = null;
        }, "EasySsf:Receiver:ExpectedAudience", "Stream:Management=Receiver", "SetValidation:AllowAnyAudience=true");
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.ExpectedAudience = null;
            options.SetValidation.AllowAnyAudience = true;
        }).Succeeded);
        // a stream that is looked up tells the audience
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.ExpectedAudience = null;
            options.Stream.Id = "stream-1";
        }).Succeeded);
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.ExpectedAudience = null;
            options.Stream.Management = SsfStreamManagement.Receiver;
            options.Push.DeliveryEndpointUrl = new Uri("https://app.example/ssf/push");
        }).Succeeded);
    }

    [Fact]
    public void FailsWithoutTransmitterIssuer()
    {
        AssertFails(_ => { }, "EasySsf:Receiver:TransmitterIssuer", "Enabled=false");
    }

    [Fact]
    public void FailsWithInsecureIssuerUnlessAllowed()
    {
        AssertFails(options => options.TransmitterIssuer = "http://idp.example", "must use https");
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "http://idp.example";
            options.AllowInsecureHttp = true;
        }).Succeeded);
        Assert.True(Validate(options => options.TransmitterIssuer = "http://localhost:8080/realms/test").Succeeded);
    }

    [Fact]
    public void FailsWithAMaxResponseSizeHttpClientCannotTake()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Http.MaxResponseSize = 0;
        }, "EasySsf:Receiver:Http:MaxResponseSize");
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Http.MaxResponseSize = int.MaxValue + 1L;
        }, "EasySsf:Receiver:Http:MaxResponseSize");
    }

    [Fact]
    public void FailsWithTwoTransmittersWithTheSameIssuer()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Transmitters["twin"] = new SsfTransmitterOptions { TransmitterIssuer = "https://idp.example" };
        }, "same issuer");
    }

    [Fact]
    public void FailsWithNamedTransmitterCalledDefault()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Transmitters["default"] = new SsfTransmitterOptions { TransmitterIssuer = "https://other.example" };
        }, "EasySsf:Receiver:Transmitters");
    }

    [Fact]
    public void RegistersEventAliasesAndFailsOnOnesThatRedefineABuiltIn()
    {
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.EventAliases["AcmeLoginValidated"] = "https://events.acme.example/login";
        }).Succeeded);
        Assert.Equal("https://events.acme.example/login", Core.Event.SsfEventTypes.Resolve("AcmeLoginValidated"));
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.EventAliases["CaepSessionRevoked"] = "https://events.acme.example/x";
        }, "EasySsf:Receiver:EventAliases:CaepSessionRevoked");
    }

    [Fact]
    public void PollDeliveryNeedsThePollEndpointOrARegisteredStream()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
        }, "EasySsf:Receiver:Poll:EndpointUrl");
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
            options.Stream.Id = "stream-1";
        }).Succeeded);
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
            options.Stream.Management = SsfStreamManagement.Receiver;
        }).Succeeded);
    }

    [Fact]
    public void PollEndpointWithoutTlsIsRefusedUnlessInsecureHttpIsAllowed()
    {
        // the access token is sent to the poll endpoint
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
            options.Poll.EndpointUrl = new Uri("http://idp.example/realms/test/poll");
        }, "EasySsf:Receiver:Poll:EndpointUrl", "must use https");
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
            options.Poll.EndpointUrl = new Uri("http://idp.example/realms/test/poll");
            options.AllowInsecureHttp = true;
        }).Succeeded);
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.DeliveryMethod = SsfDeliveryMethod.Poll;
            options.Poll.EndpointUrl = new Uri("http://localhost:8080/realms/test/poll");
        }).Succeeded);
    }

    [Fact]
    public void PushStreamManagedByReceiverNeedsDeliveryEndpointUrl()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Stream.Management = SsfStreamManagement.Receiver;
        }, "EasySsf:Receiver:Push:DeliveryEndpointUrl");
        // without events of its own the stream requests the defaults
        Assert.True(Validate(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Stream.Management = SsfStreamManagement.Receiver;
            options.Push.DeliveryEndpointUrl = new Uri("https://app.example/ssf/push");
        }).Succeeded);
    }

    [Fact]
    public void OAuth2TokenUriNeedsClientCredentials()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.OAuth2.TokenUri = new Uri("https://idp.example/token");
        }, "EasySsf:Receiver:OAuth2:ClientId");
    }

    [Fact]
    public void FailsWithUnsupportedAlgorithmOrBadEndpointPath()
    {
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.SetValidation.AcceptedAlgorithms.Add("HS256");
        }, "EasySsf:Receiver:SetValidation:AcceptedAlgorithms");
        AssertFails(options =>
        {
            options.TransmitterIssuer = "https://idp.example";
            options.Push.EndpointPath = "ssf/push";
        }, "EasySsf:Receiver:Push:EndpointPath");
    }

    [Fact]
    public void NamedTransmitterFailuresNameTheTransmitter()
    {
        AssertFails(options =>
        {
            options.Transmitters["okta"] = new SsfTransmitterOptions { TransmitterIssuer = "https://okta.example", DeliveryMethod = SsfDeliveryMethod.Poll };
        }, "EasySsf:Receiver:Transmitters:okta:Poll:EndpointUrl");
    }

    [Fact]
    public void OptionsResolvedFromTheContainerAreValidated()
    {
        ServiceCollection services = new();
        services.AddSsfReceiver(options => options.TransmitterIssuer = "http://idp.example");
        using ServiceProvider provider = services.BuildServiceProvider();
        OptionsValidationException ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SsfReceiverOptions>>().Value);
        Assert.Contains("must use https", ex.Message);
    }

    private ValidateOptionsResult Validate(Action<SsfReceiverOptions> configure)
    {
        // the audience is required unless a stream tells it, see AudienceMustBeKnownUnlessAnyIsAllowed
        SsfReceiverOptions options = new() { ExpectedAudience = "https://app.example" };
        configure(options);
        return _validator.Validate(Options.DefaultName, options);
    }

    private void AssertFails(Action<SsfReceiverOptions> configure, params string[] messageParts)
    {
        ValidateOptionsResult result = Validate(configure);
        Assert.True(result.Failed, "expected the options to be invalid");
        string message = string.Join("\n", result.Failures ?? []);
        foreach (string part in messageParts)
        {
            Assert.Contains(part, message);
        }
    }
}
