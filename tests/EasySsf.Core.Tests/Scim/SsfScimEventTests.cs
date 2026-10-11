using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Scim;
using EasySsf.Core.Support;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Core.Tests.Scim;

public class SsfScimEventTests
{
    private const string UserUri = "/Users/44f6142df96bd6ab61e7521d9";

    [Fact]
    public void EveryScimEventTypeHasAnOperationAndAnAlias()
    {
        foreach (SsfScimOperation operation in Enum.GetValues<SsfScimOperation>())
        {
            foreach (string eventType in operation.EventTypes())
            {
                Assert.StartsWith("urn:ietf:params:scim:event:", eventType);
                Assert.Equal(operation, SsfScimOperations.Of(eventType));
                string alias = SsfEventTypes.AliasOf(eventType);
                Assert.StartsWith("Scim", alias);
                Assert.DoesNotContain(":", alias);
                Assert.Equal(operation, SsfScimOperations.Of(alias));
            }
        }
        Assert.Null(SsfScimOperations.Of(SsfEventTypes.CaepSessionRevoked));
        Assert.Null(SsfScimOperations.Of(null));
        Assert.True(SsfScimOperations.IsScimEvent("ScimProvDelete"));
        Assert.False(SsfScimOperations.IsScimEvent("CaepSessionRevoked"));
    }

    [Fact]
    public void ProvisioningOperationsAreTheOnesOfSection2Dot4()
    {
        Assert.Equal(
            [SsfScimOperation.Create, SsfScimOperation.Patch, SsfScimOperation.Put, SsfScimOperation.Delete, SsfScimOperation.Activate, SsfScimOperation.Deactivate],
            Enum.GetValues<SsfScimOperation>().Where(operation => operation.IsProvisioning()));
        Assert.Equal([SsfEventTypes.ScimProvCreateNotice, SsfEventTypes.ScimProvCreateFull], SsfScimOperation.Create.EventTypes());
    }

    [Fact]
    public void FullEventCarriesTheResourceAsData()
    {
        JsonObject data = new()
        {
            ["schemas"] = new JsonArray("urn:ietf:params:scim:schemas:core:2.0:User"),
            ["userName"] = "jdoe",
        };
        SsfScimEvent scimEvent = Single(SsfEventTypes.ScimProvCreateFull, new JsonObject { ["data"] = data, ["version"] = "v1" });
        Assert.Equal(SsfScimOperation.Create, scimEvent.Operation);
        Assert.True(scimEvent.IsFull);
        Assert.False(scimEvent.IsNotice);
        Assert.True(JsonElement.DeepEquals(SsfJson.ToElement(data.DeepClone()), scimEvent.Data!.Value));
        Assert.Empty(scimEvent.Attributes);
        Assert.Equal("v1", scimEvent.Version);
        Assert.Equal(UserUri, scimEvent.Subject!.Uri);
        Assert.Equal("jdoe", scimEvent.Subject.ExternalId);
    }

    [Fact]
    public void NoticeEventListsTheModifiedAttributes()
    {
        SsfScimEvent scimEvent = Single(SsfEventTypes.ScimProvPatchNotice,
            new JsonObject { ["attributes"] = new JsonArray("members", 42, "name.familyName") });
        Assert.Equal(SsfScimOperation.Patch, scimEvent.Operation);
        Assert.True(scimEvent.IsNotice);
        Assert.False(scimEvent.IsFull);
        Assert.Equal(["members", "name.familyName"], scimEvent.Attributes);
        Assert.Null(scimEvent.Data);
        Assert.Null(scimEvent.Version);
    }

    [Fact]
    public void EventsWithoutPayloadAttributesAreNeitherFullNorNotice()
    {
        SsfScimEvent scimEvent = Single(SsfEventTypes.ScimProvDelete, new JsonObject());
        Assert.False(scimEvent.IsFull);
        Assert.False(scimEvent.IsNotice);
        Assert.True(scimEvent.Payload.IsEmptyObject());
        Assert.True(Single(SsfEventTypes.ScimFeedAdd, null).Payload.IsEmptyObject());
    }

    [Fact]
    public void AsyncResponseCarriesTheOutcomeOfTheRequest()
    {
        JsonObject response = new() { ["scimType"] = "invalidSyntax", ["detail"] = "Request is unparsable" };
        SsfScimEvent scimEvent = Single(SsfEventTypes.ScimMiscAsyncResponse,
            new JsonObject { ["method"] = "PUT", ["status"] = 400, ["response"] = response });
        Assert.Equal(SsfScimOperation.AsyncResponse, scimEvent.Operation);
        Assert.Equal("PUT", scimEvent.Method);
        Assert.Equal("400", scimEvent.Status);
        Assert.Equal("invalidSyntax", scimEvent.Response!.Value.GetProperty("scimType").GetString());
        Assert.Null(Single(SsfEventTypes.ScimProvDelete, new JsonObject()).Response);
    }

    [Fact]
    public void FindsAllScimEventsOfASetInOrderAndIgnoresOthers()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject(),
            [SsfEventTypes.ScimProvPutFull] = new JsonObject { ["data"] = new JsonObject { ["userName"] = "jdoe" } },
            [SsfEventTypes.ScimFeedAdd] = "not an object",
        };
        IReadOnlyList<SsfScimEvent> scimEvents = SsfScimEvent.In(Tokens.Of(events, SsfSubjectIdentifiers.Scim(UserUri), "txn-1", "https://scim.example.com"));
        Assert.Equal([SsfScimOperation.Put, SsfScimOperation.FeedAdd], scimEvents.Select(e => e.Operation));
        Assert.True(scimEvents[1].Payload.IsEmptyObject());
        Assert.Empty(SsfScimEvent.In(Tokens.Of(Tokens.Events(SsfEventTypes.CaepSessionRevoked, new JsonObject()), Opaque("abc"))));
    }

    [Fact]
    public void SubjectIsNullUnlessTheSetHasAScimSubject()
    {
        SsfScimEvent scimEvent = SsfScimEvent.In(Tokens.Of(Tokens.Events(SsfEventTypes.ScimProvDelete, new JsonObject()), Opaque("abc")))[0];
        Assert.Null(scimEvent.Subject);
        Assert.Null(SsfScimEvent.In(Tokens.Of(Tokens.Events(SsfEventTypes.ScimProvDelete, new JsonObject()), null))[0].Subject);
    }

    [Fact]
    public void OfResolvesAliasesAndRejectsOtherEventTypes()
    {
        SsfScimEvent scimEvent = SsfScimEvent.Of("ScimProvDeactivate", null, null)!;
        Assert.Equal(SsfEventTypes.ScimProvDeactivate, scimEvent.EventType);
        Assert.Equal(SsfScimOperation.Deactivate, scimEvent.Operation);
        Assert.Null(scimEvent.Subject);
        Assert.Null(SsfScimEvent.Of("CaepSessionRevoked", null, SsfJson.EmptyObject));
    }

    private static SsfScimEvent Single(string eventType, JsonNode? payload)
    {
        return Assert.Single(SsfScimEvent.In(Tokens.Of(Tokens.Events(eventType, payload), SsfSubjectIdentifiers.Scim(UserUri, "jdoe"), "txn-1", "https://scim.example.com")));
    }
}
