using System.Text.Json.Nodes;

using EasySsf.Core.Caep;
using EasySsf.Core.Event;
using EasySsf.Core.Support;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Core.Tests.Caep;

public class SsfCaepEventTests
{
    private const string UnknownCaepEvent = "https://schemas.openid.net/secevent/caep/event-type/geo-fence-exit";

    [Fact]
    public void EveryCaepEventTypeHasAKindAndAnAlias()
    {
        foreach (SsfCaepEventKind kind in Enum.GetValues<SsfCaepEventKind>())
        {
            if (kind == SsfCaepEventKind.Other)
            {
                Assert.Null(kind.EventType());
                continue;
            }
            string eventType = kind.EventType()!;
            Assert.StartsWith("https://schemas.openid.net/secevent/caep/event-type/", eventType);
            Assert.Equal(kind, SsfCaepEventKinds.Of(eventType));
            Assert.StartsWith("Caep", SsfEventTypes.AliasOf(eventType));
            Assert.Equal(kind, SsfCaepEventKinds.Of(SsfEventTypes.AliasOf(eventType)));
        }
        Assert.Equal(9, Enum.GetValues<SsfCaepEventKind>().Length);
    }

    [Fact]
    public void UnknownEventTypeInTheCaepNamespaceIsOtherAndAnythingElseIsNotCaep()
    {
        Assert.Equal(SsfCaepEventKind.Other, SsfCaepEventKinds.Of(UnknownCaepEvent));
        Assert.Null(SsfCaepEventKinds.Of(SsfEventTypes.RiscAccountDisabled));
        Assert.Null(SsfCaepEventKinds.Of("ScimProvDelete"));
        Assert.Null(SsfCaepEventKinds.Of("urn:example:acme:login"));
        Assert.Null(SsfCaepEventKinds.Of(null));
    }

    [Fact]
    public void CommonClaimsOfSection2AreTypedForEveryKind()
    {
        JsonObject payload = new()
        {
            ["event_timestamp"] = 1615304991,
            ["initiating_entity"] = "policy",
            ["reason_admin"] = new JsonObject { ["en"] = "Landspeed Policy Violation: C076E82F" },
            ["reason_user"] = new JsonObject
            {
                ["en"] = "Access attempt from multiple regions.",
                ["es-410"] = "Intento de acceso desde varias regiones.",
            },
        };
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepSessionRevoked, payload);
        Assert.Equal(SsfCaepEventKind.SessionRevoked, caepEvent.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1615304991), caepEvent.EventTimestamp);
        Assert.Equal("policy", caepEvent.InitiatingEntity);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Landspeed Policy Violation: C076E82F" }, caepEvent.ReasonAdmin);
        Assert.Equal(["en", "es-410"], caepEvent.ReasonUser.Keys);
        // the same claims on an event this version does not know
        SsfCaepEvent other = Single(UnknownCaepEvent, payload);
        Assert.Equal(SsfCaepEventKind.Other, other.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1615304991), other.EventTimestamp);
        Assert.Equal("policy", other.InitiatingEntity);
        Assert.True(other.ReasonUser.ContainsKey("es-410"));
        Assert.True(System.Text.Json.JsonElement.DeepEquals(SsfJson.ToElement(payload), other.Payload));
    }

    [Fact]
    public void AbsentCommonClaimsAreNullOrEmptyAndMillisecondsAreTolerated()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepSessionRevoked, new JsonObject());
        Assert.Null(caepEvent.EventTimestamp);
        Assert.Null(caepEvent.InitiatingEntity);
        Assert.Empty(caepEvent.ReasonAdmin);
        Assert.Empty(caepEvent.ReasonUser);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1615304991),
            Single(SsfEventTypes.CaepSessionRevoked, new JsonObject { ["event_timestamp"] = 1615304991000L }).EventTimestamp);
        Assert.Null(Single(SsfEventTypes.CaepSessionRevoked, new JsonObject { ["event_timestamp"] = "yesterday" }).EventTimestamp);
    }

    [Fact]
    public void PlainStringReasonIsKeptUnderTheEmptyLanguageTag()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepSessionRevoked, new JsonObject { ["reason_admin"] = "Policy violation" });
        Assert.Equal(new Dictionary<string, string> { [""] = "Policy violation" }, caepEvent.ReasonAdmin);
    }

    [Fact]
    public void TokenClaimsChangeCarriesTheNewClaims()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepTokenClaimsChange,
            new JsonObject { ["event_timestamp"] = 1615304991, ["claims"] = new JsonObject { ["role"] = "ro-admin" } });
        Assert.Equal(SsfCaepEventKind.TokenClaimsChange, caepEvent.Kind);
        Assert.Equal("ro-admin", caepEvent.Claims!.Value.GetProperty("role").GetString());
        Assert.Null(Single(SsfEventTypes.CaepSessionRevoked, new JsonObject()).Claims);
    }

    [Fact]
    public void CredentialChangeNamesTheCredential()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepCredentialChange, new JsonObject
        {
            ["credential_type"] = "fido2-roaming",
            ["change_type"] = "create",
            ["fido2_aaguid"] = "accced6a-63f5-490a-9eea-e59bc1896cfc",
            ["friendly_name"] = "Jane's USB authenticator",
            ["initiating_entity"] = "user",
            ["reason_admin"] = new JsonObject { ["en"] = "User self-enrollment" },
        });
        Assert.Equal(SsfCaepEventKind.CredentialChange, caepEvent.Kind);
        Assert.Equal("fido2-roaming", caepEvent.CredentialType);
        Assert.Equal("create", caepEvent.ChangeType);
        Assert.Equal("accced6a-63f5-490a-9eea-e59bc1896cfc", caepEvent.Fido2Aaguid);
        Assert.Equal("Jane's USB authenticator", caepEvent.FriendlyName);
        Assert.Null(caepEvent.X509Issuer);
        Assert.Null(caepEvent.X509Serial);
        SsfCaepEvent x509 = Single(SsfEventTypes.CaepCredentialChange, new JsonObject
        {
            ["credential_type"] = "x509",
            ["change_type"] = "revoke",
            ["x509_issuer"] = "CN=Example CA",
            ["x509_serial"] = "0A1B2C",
        });
        Assert.Equal("CN=Example CA", x509.X509Issuer);
        Assert.Equal("0A1B2C", x509.X509Serial);
    }

    [Fact]
    public void AssuranceLevelChangeHasNamespaceLevelsAndDirection()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepAssuranceLevelChange, new JsonObject
        {
            ["namespace"] = "NIST-AAL",
            ["current_level"] = "nist-aal2",
            ["previous_level"] = "nist-aal1",
            ["change_direction"] = "increase",
        });
        Assert.Equal(SsfCaepEventKind.AssuranceLevelChange, caepEvent.Kind);
        Assert.Equal("NIST-AAL", caepEvent.Namespace);
        Assert.Equal("nist-aal2", caepEvent.CurrentLevel);
        Assert.Equal("nist-aal1", caepEvent.PreviousLevel);
        Assert.Equal("increase", caepEvent.ChangeDirection);
        SsfCaepEvent unknownPrevious = Single(SsfEventTypes.CaepAssuranceLevelChange,
            new JsonObject { ["namespace"] = "RFC8176", ["current_level"] = 1 });
        Assert.Equal("1", unknownPrevious.CurrentLevel);
        Assert.Null(unknownPrevious.PreviousLevel);
        Assert.Null(unknownPrevious.ChangeDirection);
    }

    [Fact]
    public void DeviceComplianceChangeHasBothStatuses()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepDeviceComplianceChange, new JsonObject
        {
            ["previous_status"] = "compliant",
            ["current_status"] = "not-compliant",
            ["initiating_entity"] = "policy",
        });
        Assert.Equal(SsfCaepEventKind.DeviceComplianceChange, caepEvent.Kind);
        Assert.Equal("compliant", caepEvent.PreviousStatus);
        Assert.Equal("not-compliant", caepEvent.CurrentStatus);
    }

    [Fact]
    public void SessionEventsDescribeTheSession()
    {
        SsfCaepEvent established = Single(SsfEventTypes.CaepSessionEstablished, new JsonObject
        {
            ["fp_ua"] = "abb0b6e7da81a42233f8f2b1a8ddb1b9a4c81611",
            ["acr"] = "AAL2",
            ["amr"] = new JsonArray("otp", 42),
            ["ext_id"] = "12345",
            ["event_timestamp"] = 1615304991,
        });
        Assert.Equal(SsfCaepEventKind.SessionEstablished, established.Kind);
        Assert.Equal("abb0b6e7da81a42233f8f2b1a8ddb1b9a4c81611", established.FpUa);
        Assert.Equal("AAL2", established.Acr);
        Assert.Equal(["otp"], established.Amr);
        Assert.Equal("12345", established.ExtId);
        SsfCaepEvent presented = Single(SsfEventTypes.CaepSessionPresented,
            new JsonObject { ["fp_ua"] = "abb0b6e7da81a42233f8f2b1a8ddb1b9a4c81611", ["ext_id"] = "12345" });
        Assert.Equal(SsfCaepEventKind.SessionPresented, presented.Kind);
        Assert.Null(presented.Acr);
        Assert.Empty(presented.Amr);
    }

    [Fact]
    public void RiskLevelChangeNamesThePrincipalAndTheLevels()
    {
        SsfCaepEvent caepEvent = Single(SsfEventTypes.CaepRiskLevelChange, new JsonObject
        {
            ["principal"] = "USER",
            ["current_level"] = "LOW",
            ["previous_level"] = "HIGH",
            ["risk_reason"] = "PASSWORD_FOUND_IN_DATA_BREACH",
        });
        Assert.Equal(SsfCaepEventKind.RiskLevelChange, caepEvent.Kind);
        Assert.Equal("USER", caepEvent.Principal);
        Assert.Equal("LOW", caepEvent.CurrentLevel);
        Assert.Equal("HIGH", caepEvent.PreviousLevel);
        Assert.Equal("PASSWORD_FOUND_IN_DATA_BREACH", caepEvent.RiskReason);
    }

    [Fact]
    public void FindsAllCaepEventsOfASetInOrderAndIgnoresOthers()
    {
        JsonObject events = new()
        {
            [SsfEventTypes.RiscAccountDisabled] = new JsonObject { ["reason"] = "hijacking" },
            [SsfEventTypes.CaepSessionRevoked] = new JsonObject { ["event_timestamp"] = 1615304991 },
            [UnknownCaepEvent] = "not an object",
            [SsfEventTypes.ScimProvDelete] = new JsonObject(),
        };
        IReadOnlyList<SsfCaepEvent> caepEvents = SsfCaepEvent.In(Token(events));
        Assert.Equal([SsfCaepEventKind.SessionRevoked, SsfCaepEventKind.Other], caepEvents.Select(e => e.Kind));
        Assert.Equal(UnknownCaepEvent, caepEvents[1].EventType);
        Assert.True(caepEvents[1].Payload.IsEmptyObject());
        Assert.Empty(SsfCaepEvent.In(Token(Tokens.Events(SsfEventTypes.RiscAccountPurged, new JsonObject()))));
    }

    [Fact]
    public void OfResolvesAliasesAndRejectsOtherEventTypes()
    {
        SsfCaepEvent caepEvent = SsfCaepEvent.Of("CaepSessionRevoked", null)!;
        Assert.Equal(SsfEventTypes.CaepSessionRevoked, caepEvent.EventType);
        Assert.Equal(SsfCaepEventKind.SessionRevoked, caepEvent.Kind);
        Assert.True(caepEvent.Payload.IsEmptyObject());
        Assert.Null(SsfCaepEvent.Of("RiscAccountDisabled", SsfJson.EmptyObject));
        Assert.Equal(caepEvent, SsfCaepEvent.Of(SsfEventTypes.CaepSessionRevoked, SsfJson.EmptyObject));
    }

    private static SsfCaepEvent Single(string eventType, JsonNode? payload)
    {
        IReadOnlyList<SsfCaepEvent> caepEvents = SsfCaepEvent.In(Token(Tokens.Events(eventType, payload)));
        return Assert.Single(caepEvents);
    }

    private static Core.Event.SsfEventToken Token(JsonObject events)
    {
        return Tokens.Of(events, Email("someuser@somedomain.com"), "8675309");
    }
}
