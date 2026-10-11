using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Support;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Core.Tests.Event;

public class SsfSubjectTests
{
    [Fact]
    public void AbsentSubjectIsEmpty()
    {
        Assert.True(SsfSubject.From((JsonElement?)null).IsEmpty);
        Assert.True(SsfSubject.From((JsonNode?)null).IsEmpty);
        Assert.True(SsfSubject.From(new JsonObject()).IsEmpty);
        Assert.True(SsfSubject.From(SsfJson.Parse("null")).IsEmpty);
        Assert.True(SsfSubject.From(SsfJson.Parse("\"alice\"")).IsEmpty);
        Assert.Same(SsfSubject.Empty, SsfSubject.From(SsfJson.EmptyObject));
    }

    [Fact]
    public void ResolvesIssSub()
    {
        SsfSubject subject = SsfSubject.From(IssSub("https://idp.example", "alice"));
        Assert.Equal("https://idp.example", subject.Issuer);
        Assert.Equal("alice", subject.Subject);
        Assert.Null(subject.SessionId);
        Assert.True(subject.HasUser);
        Assert.False(subject.IsComplex);
    }

    [Fact]
    public void ResolvesEmail()
    {
        Assert.Equal("alice@example.com", SsfSubject.From(Email("alice@example.com")).Email);
    }

    [Fact]
    public void SimpleOpaqueSubjectIsNeitherUserNorSession()
    {
        SsfSubject subject = SsfSubject.From(Opaque("abc"));
        Assert.Equal("abc", subject.OpaqueId);
        Assert.Null(subject.Subject);
        Assert.Null(subject.SessionId);
        Assert.False(subject.HasUser);
    }

    [Fact]
    public void ResolvesComplexSubjectWithUserAndSession()
    {
        SsfSubject subject = SsfSubject.From(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1")));
        Assert.Equal("https://idp.example", subject.Issuer);
        Assert.Equal("alice", subject.Subject);
        Assert.Equal("session-1", subject.SessionId);
        Assert.Null(subject.OpaqueId);
        Assert.True(subject.IsComplex);
    }

    [Fact]
    public void KeycloakAllSessionsMarkerNamesOnlyTheUser()
    {
        JsonObject subjectId = Complex(IssSub("https://idp.example", "alice"), Opaque("ALL"));
        SsfSubject subject = SsfSubject.From(subjectId);
        Assert.Equal("alice", subject.Subject);
        Assert.Null(subject.SessionId);
        Assert.Null(subject.Session);
        Assert.True(JsonElement.DeepEquals(SsfJson.ToElement(subjectId), subject.Raw));
        Assert.Equal(["user", "session"], subject.MemberNames());
    }

    [Fact]
    public void ResolvesComplexSubjectWithOpaqueUser()
    {
        SsfSubject subject = SsfSubject.From(Complex(Opaque("alice"), Opaque("session-1")));
        Assert.Equal("alice", subject.Subject);
        Assert.Equal("session-1", subject.SessionId);
    }

    [Fact]
    public void ResolvesComplexSubjectWithoutFormatOfEarlyDrafts()
    {
        SsfSubject subject = SsfSubject.From(new JsonObject { ["session"] = Opaque("session-1") });
        Assert.Equal("session-1", subject.SessionId);
        Assert.False(subject.HasUser);
        Assert.True(subject.IsComplex);
    }

    [Fact]
    public void OtherFormatsOfRfc9493NameTheUser()
    {
        SsfSubject phone = SsfSubject.From(PhoneNumber("+12065550100"));
        Assert.False(phone.IsEmpty);
        Assert.Equal(SsfSubjectIdentifier.PhoneNumber, phone.UserIdentifier!.Format);
        Assert.Equal("+12065550100", phone.UserIdentifier.Value);
        Assert.Null(phone.Subject);
        Assert.True(JsonElement.DeepEquals(SsfJson.ToElement(PhoneNumber("+12065550100")), phone.Raw));
        Assert.Equal("acct:alice@example.com", SsfSubject.From(Account("acct:alice@example.com")).UserIdentifier!.Value);
        Assert.Equal("did:example:123", SsfSubject.From(Did("did:example:123")).UserIdentifier!.Value);
        Assert.Equal("https://example.com/alice", SsfSubject.From(Uri("https://example.com/alice")).UserIdentifier!.Value);
    }

    [Fact]
    public void ScimResourceOfRfc9967NamesTheUserByItsUri()
    {
        SsfSubject subject = SsfSubject.From(SsfSubjectIdentifiers.Scim("/Users/2b2f880a", "jdoe"));
        Assert.False(subject.IsEmpty);
        Assert.True(subject.HasUser);
        Assert.True(subject.UserIdentifier!.IsScim);
        Assert.Equal("/Users/2b2f880a", subject.UserIdentifier.Value);
        Assert.Equal("jdoe", subject.UserIdentifier.Claim("externalId"));
        Assert.Null(subject.Subject);
        Assert.False(SsfSubjectIdentifiers.Scim("/Users/2b2f880a").ContainsKey("externalId"));
        Assert.False(SsfSubjectIdentifiers.Scim("/Users/2b2f880a", null).ContainsKey("externalId"));
    }

    [Fact]
    public void AliasesCarryEveryIdentifier()
    {
        SsfSubject subject = SsfSubject.From(Aliases([IssSub("https://idp.example", "alice"), Email("alice@example.com")]));
        Assert.True(subject.UserIdentifier!.Is(SsfSubjectIdentifier.Aliases));
        Assert.Null(subject.UserIdentifier.Value);
        Assert.Equal(["alice", "alice@example.com"], subject.UserIdentifier.AliasIdentifiers().Select(a => a.Value));
        Assert.Null(subject.Subject);
    }

    [Fact]
    public void UnknownFormatIsKeptWithoutValue()
    {
        JsonObject custom = new() { ["format"] = "x-badge", ["badge"] = "4711" };
        SsfSubject subject = SsfSubject.From(custom);
        Assert.False(subject.IsEmpty);
        Assert.Equal("x-badge", subject.UserIdentifier!.Format);
        Assert.Null(subject.UserIdentifier.Value);
        Assert.Equal("4711", subject.UserIdentifier.Claim("badge"));
        Assert.True(JsonElement.DeepEquals(SsfJson.ToElement(custom), subject.Raw));
    }

    [Fact]
    public void IdentifierWithoutFormatIsEmpty()
    {
        SsfSubject subject = SsfSubject.From(new JsonObject { ["sub"] = "alice" });
        Assert.True(subject.IsEmpty);
        Assert.Equal("alice", subject.Raw.GetProperty("sub").GetString());
    }

    [Fact]
    public void ComplexSubjectExposesAllMembers()
    {
        SsfSubject subject = SsfSubject.From(Complex(new Dictionary<string, JsonObject?>
        {
            ["user"] = IssSub("https://idp.example", "alice"),
            ["device"] = Opaque("device-1"),
            ["tenant"] = Uri("https://tenant.example"),
            ["group"] = null,
        }));
        Assert.True(subject.IsComplex);
        Assert.Equal("alice", subject.User!.Value);
        Assert.Equal("device-1", subject.Device!.Value);
        Assert.Equal("https://tenant.example", subject.Tenant!.Value);
        Assert.Same(subject.Tenant, subject.Member("tenant"));
        Assert.Null(subject.Session);
        Assert.Null(subject.Group);
        Assert.Equal(["user", "device", "tenant"], subject.MemberIdentifiers.Keys);
    }

    [Fact]
    public void SubjectIsImmutable()
    {
        JsonObject subjectId = Complex(IssSub("https://idp.example", "alice"), Opaque("s-1"));
        SsfSubject subject = SsfSubject.From(subjectId);
        subjectId.Clear();
        Assert.Equal("alice", subject.Subject);
        Assert.NotEmpty(subject.Raw.EnumerateObject());
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, SsfSubjectIdentifier>)subject.MemberIdentifiers).Clear());
    }

    [Fact]
    public void WithoutSessionWidensToUser()
    {
        SsfSubject subject = SsfSubject.From(Complex(IssSub("https://idp.example", "alice"), Opaque("session-1")))
            .WithoutSession();
        Assert.Null(subject.SessionId);
        Assert.Equal("alice", subject.Subject);
        Assert.Same(subject, subject.WithoutSession());
    }

    [Fact]
    public void SubjectsWithTheSameClaimsAreEqual()
    {
        JsonObject subjectId = Complex(IssSub("https://idp.example", "alice"), Opaque("session-1"));
        Assert.Equal(SsfSubject.From(subjectId), SsfSubject.From(subjectId.DeepClone()));
        Assert.NotEqual(SsfSubject.From(subjectId), SsfSubject.From(subjectId).WithoutSession());
        Assert.Equal(SsfSubjectIdentifier.From(Email("alice@example.com")), SsfSubjectIdentifier.From(Email("alice@example.com")));
        Assert.NotEqual(SsfSubjectIdentifier.From(Email("alice@example.com")), SsfSubjectIdentifier.From(Email("bob@example.com")));
    }
}
