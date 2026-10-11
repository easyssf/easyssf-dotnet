using System.Text.Json.Nodes;

using EasySsf.Core.Event;
using EasySsf.Core.Scim;

using static EasySsf.Core.Event.SsfSubjectIdentifiers;

namespace EasySsf.Core.Tests.Scim;

public class SsfScimSubjectTests
{
    private const string UserUri = "/Users/2b2f880af6674ac284bae9381673d462";

    [Fact]
    public void ResolvesResourceTypeAndIdFromTheUri()
    {
        SsfScimSubject subject = SsfScimSubject.From(SsfSubject.From(SsfSubjectIdentifiers.Scim(UserUri, "jdoe")))!;
        Assert.Equal(UserUri, subject.Uri);
        Assert.Equal("Users", subject.ResourceType);
        Assert.Equal("2b2f880af6674ac284bae9381673d462", subject.Id);
        Assert.Equal("jdoe", subject.ExternalId);
        Assert.Equal("scim", subject.Claims.GetProperty("format").GetString());
        Assert.Equal(UserUri, subject.Claims.GetProperty("uri").GetString());
    }

    [Fact]
    public void ToleratesAbsoluteUrisAndQueries()
    {
        Assert.Equal("abc", ScimSubject(new JsonObject { ["uri"] = "https://scim.example.com/v2/Groups/abc?attributes=members" }).Id);
        Assert.Equal("Groups", ScimSubject(new JsonObject { ["uri"] = "https://scim.example.com/v2/Groups/abc" }).ResourceType);
        Assert.Null(ScimSubject(new JsonObject { ["uri"] = "abc" }).ResourceType);
        Assert.Equal("abc", ScimSubject(new JsonObject { ["uri"] = "abc" }).Id);
    }

    [Fact]
    public void IdAttributeWinsOverTheUri()
    {
        Assert.Equal("legacy-id", ScimSubject(new JsonObject { ["uri"] = UserUri, ["id"] = "legacy-id" }).Id);
    }

    [Fact]
    public void FurtherAttributesAreAvailableByName()
    {
        SsfScimSubject subject = ScimSubject(new JsonObject { ["uri"] = UserUri, ["userName"] = "jdoe", ["emails"] = new JsonObject() });
        Assert.Equal("jdoe", subject.Attribute("userName"));
        Assert.Null(subject.Attribute("emails"));
        Assert.Null(subject.Attribute("externalId"));
    }

    [Fact]
    public void SubjectWithoutUriHasNoResource()
    {
        SsfScimSubject subject = ScimSubject(new JsonObject { ["externalId"] = "jdoe" });
        Assert.Null(subject.Uri);
        Assert.Null(subject.ResourceType);
        Assert.Null(subject.Id);
        Assert.Equal("jdoe", subject.ExternalId);
    }

    [Fact]
    public void OnlyScimIdentifiersAreScimSubjects()
    {
        Assert.Null(SsfScimSubject.From(SsfSubject.From(Opaque("abc"))));
        Assert.Null(SsfScimSubject.From(SsfSubject.Empty));
        Assert.Null(SsfScimSubject.From((SsfSubject?)null));
        Assert.Null(SsfScimSubject.From((SsfSubjectIdentifier?)null));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => new SsfScimSubject(SsfSubjectIdentifier.From(Opaque("abc"))!));
        Assert.Contains("'scim'", ex.Message);
    }

    [Fact]
    public void UserOfAComplexSubjectMayBeAScimResource()
    {
        SsfSubject subject = SsfSubject.From(Complex(SsfSubjectIdentifiers.Scim(UserUri), Opaque("session-1")));
        Assert.Equal("Users", SsfScimSubject.From(subject)!.ResourceType);
        Assert.True(subject.UserIdentifier!.IsScim);
        Assert.Equal(UserUri, subject.UserIdentifier.Value);
    }

    private static SsfScimSubject ScimSubject(JsonObject attributes)
    {
        JsonObject identifier = new() { ["format"] = "scim" };
        foreach ((string name, JsonNode? value) in attributes)
        {
            identifier[name] = value?.DeepClone();
        }
        return SsfScimSubject.From(SsfSubjectIdentifier.From(identifier))!;
    }
}
