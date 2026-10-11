using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;
using EasySsf.Receiver.Event;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Event;

public class SsfEventContextTests
{
    private static readonly DateTimeOffset IssuedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z");

    [Fact]
    public void FindsEventsByAliasAndUri()
    {
        SsfEventContext context = Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = new JsonObject { ["reason"] = "admin" } }, null);
        Assert.True(context.HasEvent("CaepSessionRevoked"));
        Assert.True(context.HasEvent(SsfEventTypes.CaepSessionRevoked));
        Assert.False(context.HasEvent("CaepCredentialChange"));
        Assert.False(context.HasEvent(null));
        Assert.Equal("admin", context.EventFor("CaepSessionRevoked")!.Value.GetProperty("reason").GetString());
        Assert.Null(context.EventFor("CaepCredentialChange"));
        Assert.Null(context.EventFor(null));
        Assert.Equal([SsfEventTypes.CaepSessionRevoked], context.EventTypes);
    }

    [Fact]
    public void EventWhosePayloadIsNotAnObjectHasNoPayload()
    {
        SsfEventContext context = Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = "revoked" }, null);
        Assert.True(context.HasEvent("CaepSessionRevoked"));
        Assert.Null(context.EventFor("CaepSessionRevoked"));
        Assert.Equal(IssuedAt, context.EventTimestamp("CaepSessionRevoked"));
    }

    [Fact]
    public void VendorEventTypesAreAddressedByUri()
    {
        string vendorType = "https://schemas.example.org/event-type/widget-replaced";
        SsfEventContext context = Context(new JsonObject { [vendorType] = new JsonObject() }, null);
        Assert.True(context.HasEvent(vendorType));
        Assert.Equal(vendorType, SsfEventTypes.AliasOf(vendorType));
    }

    [Fact]
    public void IdempotencyKeyIsIssuerAndJti()
    {
        SsfEventContext context = Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = new JsonObject() }, null);
        Assert.Equal("https://idp.example::jti-1", context.IdempotencyKey);
        Assert.Equal(context.EventToken.IdempotencyKey, context.IdempotencyKey);
    }

    [Fact]
    public void SubjectIsTakenFromSubId()
    {
        SsfEventContext context = Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = new JsonObject() }, Opaque("abc"));
        Assert.Equal("abc", context.SubjectFor("CaepSessionRevoked").OpaqueId);
        Assert.Same(context.Subject, context.Subject);
    }

    [Fact]
    public void SubjectFallsBackToEventSubjectOfEarlyDrafts()
    {
        JsonObject payload = new() { ["subject"] = new JsonObject { ["session"] = Opaque("session-1") } };
        SsfEventContext context = Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = payload }, null);
        Assert.True(context.Subject.IsEmpty);
        Assert.Equal("session-1", context.SubjectFor("CaepSessionRevoked").SessionId);
        Assert.True(context.SubjectFor("CaepCredentialChange").IsEmpty);
    }

    [Fact]
    public void EventTimestampInSecondsOrMillisecondsElseIssuedAt()
    {
        DateTimeOffset occurred = DateTimeOffset.Parse("2026-10-02T09:59:00Z");
        Assert.Equal(occurred, Timestamp(new JsonObject { ["event_timestamp"] = occurred.ToUnixTimeSeconds() }));
        Assert.Equal(occurred, Timestamp(new JsonObject { ["event_timestamp"] = occurred.ToUnixTimeMilliseconds() }));
        Assert.Equal(IssuedAt, Timestamp(new JsonObject()));
        Assert.Equal(IssuedAt, Timestamp(new JsonObject { ["event_timestamp"] = "yesterday" }));
    }

    private static DateTimeOffset Timestamp(JsonObject payload)
    {
        return Context(new JsonObject { [SsfEventTypes.CaepSessionRevoked] = payload }, null).EventTimestamp("CaepSessionRevoked");
    }

    private static SsfEventContext Context(JsonObject events, JsonObject? subjectId)
    {
        JsonElement eventsElement = SsfJson.ToElement(events);
        Dictionary<string, JsonElement> payloads = eventsElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        JsonElement? subject = (subjectId is not null) ? SsfJson.ToElement(subjectId) : null;
        return new SsfEventContext(new SsfEventToken("jti-1", "https://idp.example", IssuedAt, null, payloads, subject, null,
            SsfJson.EmptyObject));
    }
}
