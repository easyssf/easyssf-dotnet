using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Risc;
using EasySsf.Core.Support;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Core.Tests.Risc;

public class SsfRiscEventTests
{
    private const string UnknownRiscEvent = "https://schemas.openid.net/secevent/risc/event-type/account-merged";

    [Fact]
    public void EveryRiscEventTypeHasAKindAndAnAlias()
    {
        foreach (SsfRiscEventKind kind in Enum.GetValues<SsfRiscEventKind>())
        {
            if (kind == SsfRiscEventKind.Other)
            {
                Assert.Null(kind.EventType());
                continue;
            }
            string eventType = kind.EventType()!;
            Assert.StartsWith("https://schemas.openid.net/secevent/risc/event-type/", eventType);
            Assert.Equal(kind, SsfRiscEventKinds.Of(eventType));
            Assert.StartsWith("Risc", SsfEventTypes.AliasOf(eventType));
            Assert.Equal(kind, SsfRiscEventKinds.Of(SsfEventTypes.AliasOf(eventType)));
        }
        Assert.Equal(15, Enum.GetValues<SsfRiscEventKind>().Length);
        Assert.Equal(SsfRiscEventKind.SessionsRevoked, SsfRiscEventKinds.Of("RiscSessionsRevoked"));
    }

    [Fact]
    public void UnknownEventTypeInTheRiscNamespaceIsOtherAndAnythingElseIsNotRisc()
    {
        Assert.Equal(SsfRiscEventKind.Other, SsfRiscEventKinds.Of(UnknownRiscEvent));
        Assert.Null(SsfRiscEventKinds.Of(SsfEventTypes.CaepSessionRevoked));
        Assert.Null(SsfRiscEventKinds.Of("ScimProvDelete"));
        Assert.Null(SsfRiscEventKinds.Of(null));
    }

    [Fact]
    public void OptInAndOptOutEventsReportTheParticipation()
    {
        Assert.Equal(
            [SsfRiscEventKind.OptIn, SsfRiscEventKind.OptOutInitiated, SsfRiscEventKind.OptOutCancelled, SsfRiscEventKind.OptOutEffective],
            Enum.GetValues<SsfRiscEventKind>().Where(kind => kind.IsOptOutState()));
    }

    [Fact]
    public void AccountDisabledMayCarryAReason()
    {
        SsfRiscEvent riscEvent = Single(SsfEventTypes.RiscAccountDisabled, new JsonObject { ["reason"] = "hijacking" });
        Assert.Equal(SsfRiscEventKind.AccountDisabled, riscEvent.Kind);
        Assert.Equal("hijacking", riscEvent.Reason);
        Assert.Null(Single(SsfEventTypes.RiscAccountDisabled, new JsonObject()).Reason);
    }

    [Fact]
    public void IdentifierChangedMayCarryTheNewValue()
    {
        SsfRiscEvent riscEvent = Single(SsfEventTypes.RiscIdentifierChanged, new JsonObject { ["new-value"] = "john.roe@example.com" });
        Assert.Equal(SsfRiscEventKind.IdentifierChanged, riscEvent.Kind);
        Assert.Equal("john.roe@example.com", riscEvent.NewValue);
        Assert.Null(Single(SsfEventTypes.RiscIdentifierRecycled, new JsonObject()).NewValue);
    }

    [Fact]
    public void CredentialCompromiseNamesTheCredentialAndWhenItWasFound()
    {
        SsfRiscEvent riscEvent = Single(SsfEventTypes.RiscCredentialCompromise, new JsonObject
        {
            ["credential_type"] = "password",
            ["event_timestamp"] = 1508184845,
            ["reason_admin"] = "Found in a breach dump",
            ["reason_user"] = new JsonObject { ["en"] = "Your password was found in a data breach" },
        });
        Assert.Equal(SsfRiscEventKind.CredentialCompromise, riscEvent.Kind);
        Assert.Equal("password", riscEvent.CredentialType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1508184845), riscEvent.EventTimestamp);
        Assert.Equal("Found in a breach dump", riscEvent.ReasonAdmin);
        Assert.Contains("Your password was found in a data breach", riscEvent.ReasonUser);
        SsfRiscEvent minimal = Single(SsfEventTypes.RiscCredentialCompromise, new JsonObject { ["credential_type"] = "password" });
        Assert.Null(minimal.EventTimestamp);
        Assert.Null(minimal.ReasonAdmin);
        Assert.Null(minimal.ReasonUser);
    }

    [Fact]
    public void EventsWithoutAttributesHaveAnEmptyPayload()
    {
        Assert.True(Single(SsfEventTypes.RiscAccountPurged, new JsonObject()).Payload.IsEmptyObject());
        Assert.True(Single(SsfEventTypes.RiscOptOutEffective, null).Payload.IsEmptyObject());
    }

    [Fact]
    public void FindsAllRiscEventsOfASetInOrderAndIgnoresOthers()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject(),
            [SsfEventTypes.RiscAccountDisabled] = new JsonObject { ["reason"] = "bulk-account" },
            [UnknownRiscEvent] = new JsonObject { ["into"] = "other-account" },
        };
        IReadOnlyList<SsfRiscEvent> riscEvents = SsfRiscEvent.In(Token(events));
        Assert.Equal([SsfRiscEventKind.AccountDisabled, SsfRiscEventKind.Other], riscEvents.Select(e => e.Kind));
        Assert.Equal(UnknownRiscEvent, riscEvents[1].EventType);
        Assert.Equal("other-account", riscEvents[1].Payload.GetProperty("into").GetString());
        Assert.Empty(SsfRiscEvent.In(Token(Tokens.Events(SsfEventTypes.CaepSessionRevoked, new JsonObject()))));
    }

    [Fact]
    public void OfResolvesAliasesAndRejectsOtherEventTypes()
    {
        SsfRiscEvent riscEvent = SsfRiscEvent.Of("RiscAccountPurged", null)!;
        Assert.Equal(SsfEventTypes.RiscAccountPurged, riscEvent.EventType);
        Assert.Equal(SsfRiscEventKind.AccountPurged, riscEvent.Kind);
        Assert.Null(SsfRiscEvent.Of("CaepSessionRevoked", SsfJson.EmptyObject));
    }

    private static SsfRiscEvent Single(string eventType, JsonNode? payload)
    {
        return Assert.Single(SsfRiscEvent.In(Token(Tokens.Events(eventType, payload))));
    }

    private static SsfEventToken Token(JsonObject events)
    {
        return Tokens.Of(events, Email("john.doe@example.com"));
    }
}
