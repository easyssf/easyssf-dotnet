using System.Security.Claims;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Receiver.Session;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Receiver.Tests.Session;

public class SsfSubjectClaimsMatcherTests
{
    private static readonly Dictionary<string, string?> Claims = new()
    {
        ["iss"] = "https://idp.example",
        ["sub"] = "alice",
        ["sid"] = "session-1",
        ["email"] = "alice@example.com",
    };

    [Fact]
    public void SessionSubjectMatchesThatSessionOnly()
    {
        Assert.True(Matches(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))));
        Assert.False(Matches(Complex(IssSub("https://idp.example", "alice"), Opaque("session-2"))));
    }

    [Fact]
    public void SessionSubjectMatchesOnlyAtTheIssuerOfItsUser()
    {
        Assert.False(Matches(Complex(IssSub("https://other.example", "alice"), Opaque("session-1"))));
        // no issuer named: the session identifier alone decides
        Assert.True(Matches(Complex(Opaque("alice"), Opaque("session-1"))));
        Assert.True(Matches(new JsonObject { ["session"] = Opaque("session-1") }));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))),
            new Dictionary<string, string?> { ["sid"] = "session-1" }));
    }

    [Fact]
    public void UserSubjectMatchesSubjectOfThatIssuer()
    {
        Assert.True(Matches(IssSub("https://idp.example", "alice")));
        Assert.False(Matches(IssSub("https://idp.example", "bob")));
        Assert.False(Matches(IssSub("https://other.example", "alice")));
    }

    [Fact]
    public void EmailSubjectMatchesIgnoringCase()
    {
        Assert.True(Matches(Email("ALICE@example.com")));
        Assert.False(Matches(Email("bob@example.com")));
    }

    [Fact]
    public void OpaqueSubjectMatchesSessionOrUser()
    {
        Assert.True(Matches(Opaque("session-1")));
        Assert.True(Matches(Opaque("alice")));
        Assert.False(Matches(Opaque("other")));
    }

    [Fact]
    public void NameIsUsedWhenClaimsHaveNoSubject()
    {
        SsfSubject subject = SsfSubject.From(IssSub("https://idp.example", "alice"));
        Dictionary<string, string?> issuerOnly = new() { ["iss"] = "https://idp.example" };
        Assert.True(SsfSubjectClaimsMatcher.Matches(subject, issuerOnly, "alice"));
        Assert.False(SsfSubjectClaimsMatcher.Matches(subject, issuerOnly, "bob"));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.Empty, Claims, "alice"));
    }

    [Fact]
    public void IssSubRequiresTheIssuerOfTheClaims()
    {
        SsfSubject subject = SsfSubject.From(IssSub("https://idp.example", "alice"));
        Assert.False(SsfSubjectClaimsMatcher.Matches(subject, new Dictionary<string, string?> { ["sub"] = "alice" }));
        Assert.False(SsfSubjectClaimsMatcher.Matches(subject, new Dictionary<string, string?>(), "alice"));
        Assert.False(SsfSubjectClaimsMatcher.Matches(subject, new Dictionary<string, string?> { ["iss"] = "https://other.example", ["sub"] = "alice" }));
    }

    [Fact]
    public void PhoneNumberAndOtherFormatsMatchTheClaimOfTheSameName()
    {
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(PhoneNumber("+12065550100")),
            new Dictionary<string, string?> { ["phone_number"] = "+12065550100" }));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(PhoneNumber("+12065550100")),
            new Dictionary<string, string?> { ["phone_number"] = "+12065550199" }));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(Did("did:example:123")), new Dictionary<string, string?> { ["did"] = "did:example:123" }));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(Did("did:example:123")), Claims));
    }

    [Fact]
    public void AliasesMatchIfAnyIdentifierDoes()
    {
        Assert.True(Matches(Aliases([IssSub("https://other.example", "alice"), Email("alice@example.com")])));
        Assert.False(Matches(Aliases([IssSub("https://other.example", "alice"), Email("bob@example.com")])));
    }

    [Fact]
    public void ScimResourceMatchesByExternalIdOrIdAgainstTheSubject()
    {
        Assert.True(Matches(SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "alice")));
        Assert.True(Matches(SsfSubjectIdentifiers.Scim("/Users/alice")));
        Assert.False(Matches(SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "bob")));
        Assert.False(Matches(SsfSubjectIdentifiers.Scim("/Users/2b2f880a")));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "alice")),
            new Dictionary<string, string?> { ["iss"] = "https://idp.example" }, "alice"));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(SsfSubjectIdentifiers.Scim("/Users/2b2f880a")), new Dictionary<string, string?>()));
    }

    [Fact]
    public void ScimResourceMatchesByUserNameAgainstThePreferredUsername()
    {
        JsonObject resource = SsfSubjectIdentifiers.Scim("/Users/2b2f880a");
        resource["userName"] = "alice.example";
        Dictionary<string, string?> claims = new() { ["sub"] = "2b2f880a-other", ["preferred_username"] = "alice.example" };
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), claims));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims));
    }

    [Fact]
    public void ScimAttributeClaimsAreConfigurable()
    {
        JsonObject resource = SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "alice@example.com");
        resource["userName"] = "alice";
        Dictionary<string, string> byExternalIdAsEmail = new() { ["externalId"] = "email" };
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, byExternalIdAsEmail));
        // the email claim is compared ignoring case, like an email identifier
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), new Dictionary<string, string?> { ["email"] = "Alice@Example.com" },
            null, byExternalIdAsEmail));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, new Dictionary<string, string> { ["userName"] = "sub" }));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, new Dictionary<string, string>()));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null,
            new Dictionary<string, string> { ["id"] = "sub", ["externalId"] = "sub" }));
        // the sub falls back to the name, as for the other formats
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), new Dictionary<string, string?>(), "alice",
            new Dictionary<string, string> { ["userName"] = "sub" }));
    }

    [Fact]
    public void MultiValuedScimAttributesMatchByAnyValue()
    {
        JsonObject resource = SsfSubjectIdentifiers.Scim("/Users/2b2f880a");
        resource["emails"] = new JsonArray(
            new JsonObject { ["type"] = "work", ["value"] = "ALICE@example.com", ["primary"] = true },
            new JsonObject { ["type"] = "home", ["value"] = "alice@home.example" },
            "alice@plain.example", 42);
        Dictionary<string, string> byEmails = new() { ["emails"] = "email" };
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, byEmails));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), new Dictionary<string, string?> { ["email"] = "alice@plain.example" }, null, byEmails));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), new Dictionary<string, string?> { ["email"] = "bob@example.com" }, null, byEmails));
        // an attribute of another type, or absent, matches nothing
        resource["emails"] = new JsonObject { ["value"] = "alice@example.com" };
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, byEmails));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(resource), Claims, null, new Dictionary<string, string> { ["phoneNumbers"] = "phone_number" }));
    }

    [Fact]
    public void ComplexSubjectWithOtherMembersOnlyDoesNotMatch()
    {
        Assert.False(Matches(Complex(new Dictionary<string, JsonObject?> { ["device"] = Opaque("device-1") })));
    }

    [Fact]
    public void PrincipalIsReadByJwtNamesAndMappedClaimTypes()
    {
        ClaimsPrincipal mapped = new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "alice", ClaimValueTypes.String, "https://idp.example"),
            new Claim(ClaimTypes.Email, "alice@example.com", ClaimValueTypes.String, "https://idp.example"),
            new Claim("sid", "session-1", ClaimValueTypes.String, "https://idp.example"),
        ], "oidc"));
        Assert.Equal("alice", SsfSubjectClaimsMatcher.ClaimValue(mapped, "sub"));
        Assert.Equal("https://idp.example", SsfSubjectClaimsMatcher.ClaimValue(mapped, "iss"));
        Assert.Null(SsfSubjectClaimsMatcher.ClaimValue(mapped, "preferred_username"));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(IssSub("https://idp.example", "alice")), mapped));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"))), mapped));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(Email("ALICE@example.com")), mapped));
        Assert.False(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(IssSub("https://other.example", "alice")), mapped));

        ClaimsPrincipal unmapped = new(new ClaimsIdentity([new Claim("iss", "https://idp.example"), new Claim("sub", "alice")], "jwt"));
        Assert.Equal("https://idp.example", SsfSubjectClaimsMatcher.ClaimValue(unmapped, "iss"));
        Assert.True(SsfSubjectClaimsMatcher.Matches(SsfSubject.From(IssSub("https://idp.example", "alice")), unmapped));
        // a locally issued claim has no issuer
        ClaimsPrincipal local = new(new ClaimsIdentity([new Claim("sub", "alice")], "cookie"));
        Assert.Null(SsfSubjectClaimsMatcher.ClaimValue(local, "iss"));
    }

    private static bool Matches(JsonObject subjectId)
    {
        return SsfSubjectClaimsMatcher.Matches(SsfSubject.From(subjectId), Claims);
    }
}
