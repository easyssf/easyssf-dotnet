using System.Security.Claims;
using System.Text.Json.Nodes;

using EasySsf.Examples.ResourceServer;
using EasySsf.Receiver.AspNetCore;
using EasySsf.Receiver.Hosting;

using Microsoft.AspNetCore.Authentication.JwtBearer;

// A resource server: every request to the API needs an access token issued by Keycloak. The SSF
// receiver adds the push endpoint and rejects the access tokens of sessions that were revoked in
// Keycloak. Keycloak is both the issuer of the access tokens and the SSF transmitter.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string issuer = builder.Configuration["KEYCLOAK_ISSUER"] ?? builder.Configuration["Keycloak:Issuer"] ?? "http://localhost:8080/realms/ssf-demo";
string? clientSecret = builder.Configuration[builder.Environment.IsEnvironment("Poll") ? "KEYCLOAK_POLL_CLIENT_SECRET" : "KEYCLOAK_CLIENT_SECRET"];
string? pushUrl = builder.Configuration["SSF_PUSH_URL"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = issuer;
    options.RequireHttpsMetadata = issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    // keep the claim names of the token (sub, sid, iss, iat), which the revocation check reads
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "preferred_username";
    // the access tokens of Keycloak carry the audience 'account' unless a mapper adds one
    options.TokenValidationParameters.ValidateAudience = false;
});
builder.Services.AddAuthorization();

// the receiver: options from EasySsf:Receiver of appsettings.json, the issuer shared with JwtBearer
builder.AddSsfReceiver()
    .Configure(options =>
    {
        options.TransmitterIssuer = issuer;
        options.OAuth2.TokenUri = new Uri(issuer.TrimEnd('/') + "/protocol/openid-connect/token");
        if (!string.IsNullOrEmpty(clientSecret))
        {
            options.OAuth2.ClientSecret = clientSecret;
        }
        if (!string.IsNullOrEmpty(pushUrl))
        {
            options.Push.DeliveryEndpointUrl = new Uri(pushUrl);
        }
    })
    .AddEventHandler<AuditingSsfEventHandler>();
// rejects access tokens of revoked sessions and users
builder.Services.AddSsfTokenRevocation();

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
// the push endpoint Keycloak delivers to, at EasySsf:Receiver:Push:EndpointPath
app.MapSsfPush();
app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    JsonObject me = new()
    {
        ["username"] = user.FindFirstValue("preferred_username"),
        ["subject"] = user.FindFirstValue("sub"),
        ["sessionId"] = user.FindFirstValue("sid"),
        ["tokenIssuedAt"] = Time(user.FindFirstValue("iat")),
        ["tokenExpiresAt"] = Time(user.FindFirstValue("exp")),
    };
    return Results.Content(me.ToJsonString(), "application/json");
}).RequireAuthorization();
app.Run();

static string? Time(string? epochSeconds)
{
    return long.TryParse(epochSeconds, out long seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("O") : null;
}
