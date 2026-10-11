using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

namespace EasySsf.Core.Tests;

/// <summary>
/// The value objects of the protocol copy what they are given and cannot be changed.
/// </summary>
public class SsfValueObjectsTests
{
    [Fact]
    public void EventTokenCopiesItsCollections()
    {
        List<string> aud = ["receiver"];
        List<KeyValuePair<string, JsonElement>> events = [new("urn:example:event", SsfJson.EmptyObject)];
        JsonObject claims = new() { ["jti"] = "1" };
        SsfEventToken token = new("1", "https://tr.example", DateTimeOffset.UnixEpoch, aud, events, null, null,
            SsfJson.ToElement(claims));
        aud.Clear();
        events.Clear();
        claims["x"] = "y";
        Assert.Equal(["receiver"], token.Aud);
        Assert.Equal(["urn:example:event"], token.Events.Keys);
        Assert.Equal(["jti"], token.Claims.EnumerateObject().Select(p => p.Name));
        Assert.Null(token.SubjectId);
        Assert.Equal("https://tr.example::1", token.IdempotencyKey);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, JsonElement>)token.Events).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)token.Aud).Clear());
    }

    [Fact]
    public void EventTokenRequiresItsIdentity()
    {
        Assert.Throws<ArgumentException>(() => new SsfEventToken("", "https://tr.example", DateTimeOffset.UnixEpoch,
            null, [], null, null, SsfJson.EmptyObject));
        Assert.Throws<ArgumentException>(() => new SsfEventToken("1", "https://tr.example", DateTimeOffset.UnixEpoch,
            null, [], null, null, SsfJson.Parse("[]")));
    }

    [Fact]
    public void NestedObjectsAndArraysAreCopiedToo()
    {
        JsonObject user = new() { ["format"] = "email", ["email"] = "alice@example.com" };
        JsonArray roles = ["admin", new JsonObject { ["name"] = "ops" }];
        JsonObject subjectId = new() { ["format"] = "complex", ["user"] = user, ["roles"] = roles };
        SsfSubject subject = SsfSubject.From(subjectId);
        user["email"] = "mallory@example.com";
        roles.Clear();
        Assert.Equal("alice@example.com", subject.User!.Value);
        Assert.Equal(2, subject.Raw.GetProperty("roles").GetArrayLength());
        Assert.Equal("alice@example.com", subject.Raw.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public void EventTimestampsAreSecondsOrClearlyMilliseconds()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), SsfEventTimestamps.From(SsfJson.Parse("1700000000")));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            SsfEventTimestamps.From(SsfJson.Parse("1700000000000")));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), SsfEventTimestamps.From(SsfJson.Parse("1700000000.5")));
        Assert.Null(SsfEventTimestamps.From(SsfJson.Parse("\"1700000000\"")));
        Assert.Null(SsfEventTimestamps.From(null));
    }

    [Fact]
    public void DeliveryMethodsAreIdentifiedByTheirUris()
    {
        Assert.Equal("urn:ietf:rfc:8935", SsfDeliveryMethod.Push.Uri());
        Assert.Equal("urn:ietf:rfc:8936", SsfDeliveryMethod.Poll.Uri());
        Assert.Equal(SsfDeliveryMethod.Poll, SsfDeliveryMethods.FromUri("urn:ietf:rfc:8936"));
        Assert.Null(SsfDeliveryMethods.FromUri("urn:example:carrier-pigeon"));
    }
}
