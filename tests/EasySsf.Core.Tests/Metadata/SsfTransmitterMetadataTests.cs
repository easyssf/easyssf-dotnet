using System.Text.Json;
using System.Text.Json.Nodes;

using EasySsf.Core.Metadata;
using EasySsf.Core.Support;

namespace EasySsf.Core.Tests.Metadata;

public class SsfTransmitterMetadataTests
{
    [Fact]
    public void ExposesTheMembersOfTheFinalSpecification()
    {
        JsonObject claims = new()
        {
            ["issuer"] = "https://tr.example",
            ["spec_version"] = "1_0",
            ["delivery_methods_supported"] = new JsonArray(SsfDeliveryMethod.Push.Uri()),
            ["critical_subject_members"] = new JsonArray("user", "tenant"),
            ["authorization_schemes"] = new JsonArray(new JsonObject { ["spec_urn"] = "urn:ietf:rfc:6749" }),
            ["default_subjects"] = "ALL",
        };
        SsfTransmitterMetadata metadata = new("https://tr.example", null, SsfJson.ToElement(claims));
        Assert.Equal(SsfTransmitterMetadata.SpecVersion10, metadata.SpecVersion);
        Assert.Equal([SsfDeliveryMethod.Push.Uri()], metadata.DeliveryMethodsSupported);
        Assert.True(metadata.SupportsDeliveryMethod(SsfDeliveryMethod.Push));
        Assert.False(metadata.SupportsDeliveryMethod(SsfDeliveryMethod.Poll));
        Assert.Equal(["user", "tenant"], metadata.CriticalSubjectMembers);
        JsonElement scheme = Assert.Single(metadata.AuthorizationSchemes);
        Assert.Equal("urn:ietf:rfc:6749", scheme.GetProperty("spec_urn").GetString());
        Assert.Equal("ALL", metadata.DefaultSubjects);
    }

    [Fact]
    public void AbsentMembersAreEmptyAndEveryDeliveryMethodIsAssumedToBeSupported()
    {
        SsfTransmitterMetadata metadata = new("https://tr.example", new Uri("https://tr.example/jwks"),
            SsfJson.ToElement(new JsonObject { ["issuer"] = "https://tr.example" }));
        Assert.Null(metadata.SpecVersion);
        Assert.Empty(metadata.DeliveryMethodsSupported);
        Assert.True(metadata.SupportsDeliveryMethod(SsfDeliveryMethod.Poll));
        Assert.Empty(metadata.CriticalSubjectMembers);
        Assert.Empty(metadata.AuthorizationSchemes);
        Assert.Null(metadata.DefaultSubjects);
        Assert.Null(metadata.ConfigurationEndpoint);
        Assert.Equal(new Uri("https://tr.example/jwks"), metadata.JwksUri);
    }

    [Fact]
    public void EndpointsAreUris()
    {
        SsfTransmitterMetadata metadata = new("https://tr.example", null, SsfJson.ToElement(new JsonObject
        {
            ["configuration_endpoint"] = "https://tr.example/streams",
            ["status_endpoint"] = "https://tr.example/status",
            ["verification_endpoint"] = "https://tr.example/verify",
            ["add_subject_endpoint"] = "https://tr.example/subjects:add",
            ["remove_subject_endpoint"] = "https://tr.example/subjects:remove",
        }));
        Assert.Equal(new Uri("https://tr.example/streams"), metadata.ConfigurationEndpoint);
        Assert.Equal(new Uri("https://tr.example/status"), metadata.StatusEndpoint);
        Assert.Equal(new Uri("https://tr.example/verify"), metadata.VerificationEndpoint);
        Assert.Equal(new Uri("https://tr.example/subjects:add"), metadata.AddSubjectEndpoint);
        Assert.Equal(new Uri("https://tr.example/subjects:remove"), metadata.RemoveSubjectEndpoint);
    }

    [Fact]
    public void ClaimsAreCopied()
    {
        JsonObject claims = new() { ["issuer"] = "https://tr.example" };
        SsfTransmitterMetadata metadata = new("https://tr.example", null, SsfJson.ToElement(claims));
        claims["spec_version"] = "1_0";
        Assert.Null(metadata.SpecVersion);
        Assert.True(new SsfTransmitterMetadata("https://tr.example", null, null).Claims.IsEmptyObject());
        Assert.Throws<ArgumentException>(() => new SsfTransmitterMetadata("https://tr.example", null, SsfJson.Parse("[]")));
    }
}
