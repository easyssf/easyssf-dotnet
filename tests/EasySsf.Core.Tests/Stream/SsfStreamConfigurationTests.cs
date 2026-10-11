using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Stream;
using EasySsf.Core.Support;

namespace EasySsf.Core.Tests.Stream;

public class SsfStreamConfigurationTests
{
    [Fact]
    public void PushStreamNamesTheEndpointAndTheHeader()
    {
        SsfStreamConfiguration stream = SsfStreamConfiguration.Push(new Uri("https://receiver.example/ssf/push"), "Bearer secret",
            ["CaepSessionRevoked", SsfEventTypes.CaepCredentialChange], "test");
        Assert.Equal(SsfDeliveryMethod.Push.Uri(), stream.DeliveryMethod);
        Assert.Equal(new Uri("https://receiver.example/ssf/push"), stream.DeliveryEndpointUrl);
        Assert.Equal("Bearer secret", stream.Delivery!.Value.GetProperty("authorization_header").GetString());
        Assert.Equal([SsfEventTypes.CaepSessionRevoked, SsfEventTypes.CaepCredentialChange], stream.EventsRequested);
        Assert.Equal("test", stream.Description);
        Assert.Null(stream.StreamId);
        Assert.Null(stream.Issuer);
        Assert.Empty(stream.Audience);
    }

    [Fact]
    public void PollStreamHasNoEndpointUntilTheTransmitterAssignsOne()
    {
        SsfStreamConfiguration stream = SsfStreamConfiguration.Poll(["CaepSessionRevoked"], null);
        Assert.Equal(SsfDeliveryMethod.Poll.Uri(), stream.DeliveryMethod);
        Assert.Null(stream.DeliveryEndpointUrl);
        Assert.Null(stream.Description);
        Assert.False(stream.Claims.TryGetProperty("description", out _));
    }

    [Fact]
    public void ReadsWhatTheTransmitterReturns()
    {
        SsfStreamConfiguration stream = SsfStreamConfiguration.From(new JsonObject
        {
            ["stream_id"] = "s-1",
            ["iss"] = "https://tr.example",
            ["aud"] = new JsonArray("receiver/s-1", "https://receiver.example"),
            ["events_supported"] = new JsonArray(SsfEventTypes.CaepSessionRevoked),
            ["events_delivered"] = new JsonArray(SsfEventTypes.CaepSessionRevoked),
            ["delivery"] = new JsonObject { ["method"] = SsfDeliveryMethod.Poll.Uri(), ["endpoint_url"] = "https://tr.example/poll" },
        });
        Assert.Equal("s-1", stream.StreamId);
        Assert.Equal("https://tr.example", stream.Issuer);
        Assert.Equal(["receiver/s-1", "https://receiver.example"], stream.Audience);
        Assert.Equal([SsfEventTypes.CaepSessionRevoked], stream.EventsSupported);
        Assert.Equal([SsfEventTypes.CaepSessionRevoked], stream.EventsDelivered);
        Assert.Equal(new Uri("https://tr.example/poll"), stream.DeliveryEndpointUrl);
        Assert.Equal(["receiver/s-1"], SsfStreamConfiguration.From(new JsonObject { ["aud"] = "receiver/s-1" }).Audience);
    }

    [Fact]
    public void ClaimsAreCopiedAndKeepNullValues()
    {
        JsonObject claims = new() { ["stream_id"] = "s-1", ["description"] = null };
        SsfStreamConfiguration stream = SsfStreamConfiguration.From(claims);
        claims["stream_id"] = "s-2";
        Assert.Equal("s-1", stream.StreamId);
        Assert.True(stream.Claims.TryGetProperty("description", out _));
        Assert.Null(stream.Description);
        Assert.True(SsfStreamConfiguration.From(null).Claims.IsEmptyObject());
        Assert.Equal(stream, SsfStreamConfiguration.From(stream.ToJsonObject()));
    }

    [Fact]
    public void StatusHasItsConstants()
    {
        SsfStreamStatus status = new("s-1", SsfStreamStatus.Paused, "maintenance");
        Assert.Equal("paused", status.Status);
        Assert.Equal("enabled", SsfStreamStatus.Enabled);
        Assert.Equal("disabled", SsfStreamStatus.Disabled);
    }
}
