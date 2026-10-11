# easyssf-dotnet

Building blocks for the
[OpenID Shared Signals Framework (SSF)](https://openid.net/specs/openid-sharedsignals-framework-1_0.html)
in .NET, for applications that receive security events: a framework independent **receiver**
library, with ASP.NET Core integration to follow. A port of
[easyssf for Java](https://github.com/easyssf/easyssf), which is the reference for protocol
behaviour and is tested against the OpenID conformance suite.

**Turn a .NET application into an SSF receiver without implementing SET validation, metadata
discovery, PUSH and POLL delivery, stream management and replay protection yourself.** The goal is
that reacting to a security event is a handler:

```csharp
builder.AddSsfReceiver().AddEventHandler("CaepSessionRevoked", (context, cancellationToken) =>
{
    SsfSubject subject = context.SubjectFor("CaepSessionRevoked");
    // end what you hold for subject.SessionId or subject.Subject
    return Task.CompletedTask;
});
app.MapSsfPush();
```

With the hosting package the transmitter is configuration, see
[Getting started](#getting-started); without a framework a few lines wire the parts together, see
[Receiving pushed SETs](#receiving-pushed-sets).

| | |
|---|---|
| **Status** | Experimental, pre-alpha: the receiver and its hosting are ported, nothing is on NuGet yet |
| **.NET** | 10 |
| **Package prefix** | `EasySsf` |
| **License** | Apache-2.0 |

## State of the port

The receiver is ported slice by slice from the Java library, each slice with the Java tests.

| | |
|---|---|
| Done | The vocabulary (`SsfEventToken`, `SsfSubject`, `SsfEventTypes`, ...), SET verification against the transmitter's JWK Set with the SSF error codes, `jti` de-duplication, the push handler (RFC 8935), event handlers, typed CAEP, RISC and SCIM events with dispatching handlers, metadata discovery, client credentials, the stream management API, stream registration, polling (RFC 8936), metrics, a `TestTransmitter` for the tests of a receiver, hosting (options, DI, hosted service, health check), the ASP.NET Core push endpoint, and the built-in reactions: access token revocation for JwtBearer and cookie session revocation |
| Next | ADO.NET stores for processed SETs, revocations and poll acknowledgements |
| Then | The conformance harness; a Keycloak example |

A transmitter is not planned; `EasySsf.Core` is written so that one could share it.

## Projects

| Project | | Depends on |
|---|---|---|
| `EasySsf.Core` | The data structures of SSF: SETs, subjects, event types, the typed CAEP, RISC and SCIM events, stream configuration, transmitter metadata, as immutable records over `System.Text.Json` elements. Trim- and AOT-safe, no reflection-based serialization. | nothing |
| `EasySsf.Receiver` | The receiver, independent of any framework: SET verification, de-duplication, event handlers, push handling, polling, stream management, CAEP, RISC and SCIM handlers and metrics today; revocation and session termination as the port proceeds. | `EasySsf.Core`, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Logging.Abstractions` |
| `EasySsf.Receiver.Hosting` | The receiver for any `Microsoft.Extensions.Hosting` application: `SsfReceiverOptions` bound from `EasySsf:Receiver`, `AddSsfReceiver()`, the `SsfTransmitterFactory` that builds the parts from the options, validation on start, the hosted service that registers streams and polls, and a health check. | `EasySsf.Receiver`, `Microsoft.Extensions.Hosting.Abstractions`, `.Http`, `.Options.ConfigurationExtensions`, `.Diagnostics.HealthChecks` |
| `EasySsf.Receiver.AspNetCore` | `MapSsfPush()`, the endpoint the transmitter pushes SETs to, and the revocation hooks for JwtBearer (`AddSsfTokenRevocation()`) and cookie authentication (`AddSsfSessionRevocation()`). | `EasySsf.Receiver.Hosting`, ASP.NET Core, `Microsoft.AspNetCore.Authentication.JwtBearer` |
| `EasySsf.Testing` | Test support: a transmitter that signs SETs, serves its JWK Set and metadata, hands out access tokens and emulates the stream management and poll endpoints, all through an `HttpMessageHandler`, see [Testing your receiver](#testing-your-receiver). | `EasySsf.Core` |

## What it does today

- **Verifies a SET** (RFC 8417): signature against the transmitter's JWK Set, `typ`, `iss`, `aud`,
  `jti`, `iat`, `events` and the top-level `sub_id` SSF 1.0 requires. Rejections carry the error
  codes of RFC 8935 (`invalid_request`, `invalid_key`, `invalid_issuer`, `invalid_audience`), so
  a push endpoint can answer with them. A transmitter whose keys cannot be fetched is reported
  separately, as a temporary condition.
- **Caches the JWK Set** and fetches it again when a SET names a key it does not know, rate
  limited so that bogus SETs cannot make the receiver flood the transmitter.
- Accepts only asymmetric signature algorithms (`RS256` by default) and RSA keys of at least 2048
  bits, both configurable.
- Resolves **subjects** (RFC 9493): simple identifiers of every format, complex subjects with
  `user`, `session`, `device`, `tenant` and other members, aliases, the `scim` format of SCIM
  Events, and the conventions of early SSF drafts and of Keycloak behind explicit modes.
- Knows the **event types** of SSF, CAEP, RISC and SCIM Events (RFC 9967) by URI and by alias
  (`CaepSessionRevoked`), and lets you register aliases for vendor specific types.
- **Receives pushed SETs** (RFC 8935): checks the transmitter's `Authorization` header in constant
  time, verifies, **skips SETs it has already processed** (`jti` de-duplication with a lease, so
  that a SET another instance is handling is left alone and one abandoned by a crash is taken
  over), hands verified events to your `ISsfEventHandler`s, and answers `202`, `400` with an RFC
  8935 error document, `401`, `413`, `500` or `503` as the case requires.
- Rejects a SET whose subject has a member the transmitter declared **critical** and the
  application does not interpret, as SSF requires.
- Records **metrics** with `System.Diagnostics.Metrics` under the Java library's names
  (`easyssf.receiver.sets`, `.events`, `.poll`).
- **Discovers the transmitter** from its `.well-known/ssf-configuration` (SSF 1.0, section 7), at
  the location the specification defines and at the one OpenID providers tend to use, and holds
  the transmitter's issuer and endpoints to `https` unless a loopback address or an explicit
  insecure policy says otherwise.
- **Manages its stream** at the transmitter: looks a stream up by identifier, or reuses, updates
  or creates the stream the receiver wants, in the background with retries, and offers an
  `SsfStreamClient` for the stream management API (streams, status, subjects, verification).
- **Polls** the transmitter (RFC 8936) with short or long polling, acknowledges handled SETs and
  reports invalid ones on the next request, keeps acknowledgements in an `ISsfPollAckStore` until
  a request carried them, backs off on `Retry-After`, and authenticates with the client
  credentials grant, renewing the token ahead of its expiry and on a 401.
- **Revokes access tokens and cookie sessions**: a `session-revoked` event records the session, or
  the user and the time, in an `ISsfTokenRevocationStore`; a JwtBearer scheme then rejects tokens
  with that `sid`, or that `sub` issued up to the event, and a cookie scheme signs such a principal
  out on its next request. An `ISsfSessionTerminator` of the application ends server-side
  sessions when the event arrives, see [Reacting to revocations](#reacting-to-revocations).
- **Typed events**: `SsfCaepEvent` (CAEP 1.0, the common claims of section 2 and the
  event-specific ones of section 3), `SsfRiscEvent` (RISC 1.0) and `SsfScimEvent` with
  `SsfScimSubject` (SCIM Events, RFC 9967), each read from a SET with `In(eventToken)`, and the
  handler base classes that dispatch them, see [Typed handlers](#typed-handlers).

## Getting started

Reference `EasySsf.Receiver.Hosting` and, for push delivery, `EasySsf.Receiver.AspNetCore`.
Configure the transmitter, register the receiver and your handlers, and map the push endpoint:

```json
{
  "EasySsf": {
    "Receiver": {
      "TransmitterIssuer": "https://idp.example/realms/demo",
      "ExpectedAudience": "https://my-app.example",
      "Push": { "ExpectedAuthHeader": "Bearer s3cr3t" }
    }
  }
}
```

```csharp
using EasySsf.Receiver.AspNetCore;
using EasySsf.Receiver.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddSsfReceiver()                       // binds EasySsf:Receiver, validates on start
    .AddEventHandler<SessionRevokedHandler>()  // any ISsfEventHandler, SsfCaepEventHandler, ...
    .AddEventHandler("CaepCredentialChange", (context, ct) => credentials.ChangedAsync(context.Subject, ct));
builder.Services.AddHealthChecks().AddSsfReceiver();

var app = builder.Build();
app.MapSsfPush();                              // POST /ssf/push, anonymous: the SET authenticates itself
app.MapHealthChecks("/healthz");
app.Run();
```

The transmitter's metadata is discovered from its issuer on first use, so the application starts
even while the transmitter is down. To poll instead of being pushed to, and to let the receiver
register its own stream:

```json
{
  "EasySsf": {
    "Receiver": {
      "TransmitterIssuer": "https://idp.example/realms/demo",
      "DeliveryMethod": "Poll",
      "Stream": { "Management": "Receiver", "EventsRequested": ["CaepSessionRevoked", "CaepCredentialChange"] },
      "Poll": { "LongPolling": true },
      "OAuth2": {
        "TokenUri": "https://idp.example/realms/demo/protocol/openid-connect/token",
        "ClientId": "my-app", "ClientSecret": "..."
      }
    }
  }
}
```

The hosted service registers the stream and starts polling when the host starts, and acknowledges
what it owes the transmitter when the host stops. The audience of the stream is learned from the
registration, so `ExpectedAudience` is not needed.

Several transmitters go under `EasySsf:Receiver:Transmitters:<name>`, each with the same settings;
the one at `EasySsf:Receiver` is named `default`. SETs are verified by the transmitter that issued
them, and each transmitter authenticates at the push endpoint with its own header. A customizer
replaces or adds parts of any transmitter before it is built:

```csharp
builder.AddSsfReceiver().AddTransmitterCustomizer(transmitter =>
{
    if (transmitter.Name == "okta")
    {
        transmitter.TokenProvider = new MyTokenProvider();
    }
});
```

Your own `ISsfJtiDedupStore`, `ISsfPollAckStore` or `ISsfReceiverMetrics` registered before
`AddSsfReceiver()` replace the in-memory store, the in-memory acknowledgements and the
`System.Diagnostics.Metrics` meter.

### Reacting to revocations

The receiver records what `session-revoked` events (and the `Revocation:UserEventTypes` you add,
typically `CaepCredentialChange`) revoke, scoped to the issuer of the tokens. Two lines hook that
into the authentication of the application:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => ...);
builder.Services.AddSsfTokenRevocation();      // rejects access tokens of revoked sessions and users

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
builder.Services.AddSsfSessionRevocation();    // signs revoked cookie sessions out on their next request
```

| Event | Subject of the event | Effect |
|---|---|---|
| `session-revoked` | names a session (`complex` subject with a `session` member) | access tokens with that `sid`, and cookies of that session, are rejected |
| `session-revoked` | names only a user (`iss_sub`) | access tokens and cookies of that `sub` issued up to the time of the event are rejected |
| a `Revocation:UserEventTypes` event, e.g. `credential-change` | names a user, with or without a session | as above, for the user |

The check reads `iss`, `sub`, `sid` and `iat` from the principal, by their JWT names or the claim
types ASP.NET Core maps them to; a cookie is compared by the time it was issued. The revocations
live in the `ISsfTokenRevocationStore`, in memory for `Revocation:Ttl` by default, which must cover
the longest token or cookie lifetime; register your own store to share them between instances. An
application that keeps sessions server-side registers an `ISsfSessionTerminator` instead or in
addition; the receiver then ends the sessions of the event's subject when the event arrives, with
`SsfSubjectClaimsMatcher` deciding which sessions belong to the subject.

### Configuration

Every setting of `EasySsf:Receiver`, with its default. The settings of a transmitter, from
`TransmitterIssuer` to `SetValidation`, repeat under `Transmitters:<name>`. Lists
(`EventsRequested`, `AcceptedAlgorithms`, `UnderstoodSubjectMembers`, `Scopes`) start empty and
mean their default while empty, because configuration binding adds to a list rather than replacing
it.

| Setting | Default | |
|---|---|---|
| `Enabled` | `true` | Switches the receiver off entirely when `false`. |
| `TransmitterIssuer` | | Issuer of the transmitter, must match `iss` of every SET. An `https` URL without query or fragment; `http` only on loopback addresses or with `AllowInsecureHttp`. Required unless transmitters are configured by name. |
| `TransmitterMetadataUrl` | derived | By default `<host>/.well-known/ssf-configuration<issuer-path>` (SSF 1.0, section 7.2), falling back to `<issuer>/.well-known/ssf-configuration`. |
| `TransmitterJwksUrl` | from metadata | Skips metadata discovery for the keys when set. |
| `AllowInsecureHttp` | `false` | Accepts `http` for the transmitter issuer and the endpoints it publishes on any host, with a warning on startup. For development only. |
| `ExpectedAudience` | | When set, every SET must contain it in `aud`. Without it, the audience of a registered stream; without that, any. |
| `DeliveryMethod` | `Push` | `Push` or `Poll`. |
| `UnderstoodSubjectMembers` | `user, session, device, application, tenant, org_unit, group` | The members of a complex subject the application interprets; a SET with a member the transmitter declared critical that is not listed is rejected. |
| `EventAliases:<alias>` | | Aliases for event type URIs, e.g. `"AcmeLogin": "https://events.acme.example/login"`, usable wherever an event type is named. |
| `Http:Timeout` | `00:00:05` | Timeout for calls to the transmitter; a long poll waits its hold time plus a margin regardless. |
| `Http:UserAgent` | | `User-Agent` header for calls to the transmitter. |
| `TransmitterAccessToken` | | Static access token for stream management and polling. |
| `OAuth2:TokenUri` | | Token endpoint for the client credentials grant. |
| `OAuth2:ClientId` / `ClientSecret` | | |
| `OAuth2:Scopes` | | |
| `OAuth2:ClientAuthenticationMethod` | `Basic` | `Basic` or `Post`. |
| `OAuth2:ExpirySafetyWindow` | `00:00:30` | How long before its expiry an access token is renewed, at most a quarter of its lifetime. |
| `OAuth2:AdditionalParameters:<name>` | | Form parameters sent with the token request in addition to the grant. |
| `Stream:Management` | `Transmitter` | `Receiver` makes the application create or update its stream on startup. |
| `Stream:Id` | | Stream created at the transmitter, looked up on startup. |
| `Stream:EventsRequested` | `CaepSessionRevoked`, `CaepCredentialChange` | For a stream managed by the receiver. |
| `Stream:Description` | | For a stream managed by the receiver. |
| `Stream:DeleteOnShutdown` | `false` | For a stream managed by the receiver. |
| `Poll:EndpointUrl` | from the stream | |
| `Poll:AutoStartup` | `true` | Otherwise the application polls by calling `SsfPoller.PollNowAsync()`. |
| `Poll:Interval` | `00:00:30` | |
| `Poll:InitialDelay` | `00:00:01` | |
| `Poll:MaxEvents` | `100` | SETs per request. |
| `Poll:LongPolling` | `false` | Keep one request outstanding that the transmitter holds until SETs are available, instead of polling every `Interval`. |
| `Poll:LongPollingHold` | `00:00:30` | How long the transmitter holds a long poll request, agreed with it; the request waits that long plus a margin. |
| `Poll:RateLimit:FallbackBackoff` | | Pause after a `429` without `Retry-After`; unset, the next poll comes at the regular interval. |
| `Poll:RateLimit:MaxBackoff` | `00:05:00` | Longest pause a `Retry-After` header or the fallback can cause. |
| `Metrics:Enabled` | `true` | Record `easyssf.receiver.*` meters with `System.Diagnostics.Metrics`. |
| `SetValidation:AcceptedAlgorithms` | `RS256` | JWS algorithms accepted for SETs. |
| `SetValidation:MinRsaKeySize` | `2048` | `0` disables the check. |
| `SetValidation:RequireTypeHeader` | `true` | Requires `typ: secevent+jwt`. |
| `SetValidation:SubjectCompatibility` | `Strict` | Requires the top-level `sub_id` claim of SSF 1.0. `Legacy` accepts SETs of transmitters following earlier drafts, which put the subject into the event. |
| `SetValidation:ClockSkew` | `00:01:00` | Tolerance for `iat` in the future. |
| `Push:EndpointPath` | `/ssf/push` | Where `MapSsfPush()` maps the endpoint; a pattern given to `MapSsfPush` wins. |
| `Push:ExpectedAuthHeader` | | Exact `Authorization` header value the transmitter must send. |
| `Push:DeliveryEndpointUrl` | | How the transmitter reaches the push endpoint, for a stream managed by the receiver. |
| `Dedup:Enabled` | `true` | |
| `Dedup:Capacity` | `10000` | Size of the in-memory `jti` store. |
| `Dedup:Lease` | `00:01:00` | How long a SET counts as being handled by the instance that claimed it; longer than the longest handler. |
| `Revocation:Enabled` | `true` | Record revocations and reject tokens of revoked sessions and users. |
| `Revocation:SessionEventTypes` | `CaepSessionRevoked` | Events that revoke the session they name, or the user if they name none. |
| `Revocation:UserEventTypes` | | Events that revoke the user they name, session or not; `CaepCredentialChange` for cookie sessions. |
| `Revocation:Ttl` | `01:00:00` | How long the in-memory store remembers a revocation; at least the longest token or cookie lifetime. |
| `SessionTermination:Enabled` | `true` | End server-side sessions through the registered `ISsfSessionTerminator`. |
| `SessionTermination:SessionEventTypes` | `CaepSessionRevoked` | Events that terminate the session (or user sessions) of their subject. |
| `SessionTermination:UserEventTypes` | `CaepCredentialChange` | Events that terminate all sessions of their subject's user. |
| `SessionTermination:ScimAttributeClaims:<attribute>` | `externalId: sub`, `id: sub`, `userName: preferred_username` | Attributes of the `scim` subject of a SCIM Event and the claim of the logged-in user each is compared with. |

### Health

`services.AddHealthChecks().AddSsfReceiver()` adds the check `easyssf`. It reports from what the
receiver already knows and never calls the transmitter: `Healthy` once the transmitter was reached
(its metadata retrieved for push, the last poll succeeded for poll) and its stream, if looked up or
managed, is registered; `Unhealthy` when the last poll failed or the stream cannot be used;
`Degraded` while there was no contact yet, which is how the application starts. The data of the
result lists transmitter, delivery, metadata, stream, registration state, last polls and pending
acknowledgements, under the transmitter's name when there are several.

### Metrics

The meter `EasySsf.Receiver` records, under the names of the Java library:

| Instrument | Type | Tags |
|---|---|---|
| `easyssf.receiver.sets` | counter | `transmitter` (the name of the transmitter, `default` for the one at `EasySsf:Receiver`), `delivery` (`push`, `poll`), `outcome` (`handled`, `duplicate`, `invalid`, `unauthenticated`, `unavailable`, `failed`, `in_progress`) |
| `easyssf.receiver.events` | counter | `transmitter`, `delivery`, `event` (for example `CaepSessionRevoked`) |
| `easyssf.receiver.poll` | histogram, seconds | `transmitter`, `outcome` (`success`, `failure`) |
| `easyssf.receiver.poll.pending-acks` | gauge | `transmitter`; acknowledgements and error reports waiting for the next poll request |

## Verifying a SET

```csharp
using EasySsf.Core.Event;
using EasySsf.Receiver.Set;

SsfSetVerifierOptions options = new();
options.ExpectedAudiences.Add("https://my-app.example");

ISsfSetVerifier verifier = new JwtSsfSetVerifier(
    issuer: "https://idp.example/realms/demo",
    jwkSetUri: new Uri("https://idp.example/realms/demo/protocol/openid-connect/certs"),
    httpClient: new HttpClient(),
    options);

try
{
    SsfEventToken set = await verifier.VerifyAsync(encodedSet, cancellationToken);
    SsfSubject subject = SsfSubject.From(set.SubjectId);
    if (set.Events.ContainsKey(SsfEventTypes.CaepSessionRevoked))
    {
        // end what you hold for subject.SessionId or subject.Subject
    }
}
catch (SsfSetVerificationException ex)
{
    // ex.ErrorCode is invalid_request, invalid_key, invalid_issuer or invalid_audience (RFC 8935, section 2.4)
}
catch (SsfTransmitterUnavailableException)
{
    // the JWK Set could not be fetched: answer 503, the transmitter will deliver the SET again
}
```

The JWK Set location can also be supplied lazily, as a `Func<CancellationToken, ValueTask<Uri>>`,
so that it is discovered from the transmitter's metadata on first use. `SsfSetVerifierOptions`
holds the accepted algorithms, the minimum RSA key size, the clock skew, the subject compatibility
mode (`Strict` for SSF 1.0, `Legacy` for transmitters that put the subject into the event payload)
and the `TimeProvider`. For several transmitters, `IssuerRoutingSsfSetVerifier` picks the verifier
by the `iss` claim.

## Receiving pushed SETs

`SsfPushHandler` does everything but the HTTP: give it the `Authorization` header and the body of
the request the transmitter posted, and send back the status and body it returns.

```csharp
using EasySsf.Receiver.Event;
using EasySsf.Receiver.Push;
using EasySsf.Receiver.Set;

ISsfEventHandler handler = SsfEventHandler.For("CaepSessionRevoked", (context, cancellationToken) =>
{
    SsfSubject subject = context.SubjectFor("CaepSessionRevoked");
    return sessions.EndAsync(subject.SessionId, subject.Subject, context.IdempotencyKey, cancellationToken);
});
SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(capacity: 10_000), [handler]);
SsfPushHandler pushHandler = new(processor, expectedAuthorizationHeader: "Bearer " + pushSecret);

// in the endpoint the transmitter posts to, for example a minimal API:
app.MapPost("/ssf/push", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    using MemoryStream body = new();
    await request.Body.CopyToAsync(body, cancellationToken);
    SsfPushResponse response = await pushHandler.HandleAsync(request.Headers.Authorization, body.ToArray(), cancellationToken);
    return response.Body is null ? Results.StatusCode(response.Status)
        : Results.Text(response.Body, SsfPushResponse.ContentType, statusCode: response.Status);
});
```

Handlers are called for every SET and must be idempotent: a SET is handled at least once, not
exactly once. Throwing from a handler makes the push handler answer `500`, so the transmitter
delivers the SET again; the other handlers still run. `SsfEventHandler.Of` handles every SET,
`SsfEventHandler.For` only those with a given event; `context.EventFor` gives the payload as a
`JsonElement`, `context.EventTimestamp` the `event_timestamp` or the issue time. For a
`processor.ProcessAsync` call of your own, the outcome tells whether the SET was handled or a
duplicate, and `SsfSetProcessorOptions` takes the metrics, the critical subject members a
transmitter declares and the stream verification hook.

## Polling and stream management

For a receiver that polls, `SsfTransmitterBuilder` assembles the parts for one transmitter and
`SsfTransmitter.Start()` runs them in the background: the registrar registers the stream, the
poller fetches as soon as the stream is known. An `HttpClient` with a long enough timeout serves
them all.

```csharp
using EasySsf.Core.Stream;
using EasySsf.Receiver.Poll;
using EasySsf.Receiver.Set;
using EasySsf.Receiver.Stream;
using EasySsf.Receiver.Transmitter;

HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
string issuer = "https://idp.example/realms/demo";
SsfTransmitterMetadataResolver metadata = new(issuer, null, http);
ClientCredentialsSsfTransmitterTokenProvider tokens = new(http,
    new Uri("https://idp.example/realms/demo/protocol/openid-connect/token"), clientId, clientSecret);
SsfReceiverStream receiverStream = new();
SsfStreamClient streamClient = new(http, tokens, metadata);
SsfStreamRegistrar registrar = SsfStreamRegistrar.ForManagedStream(streamClient, receiverStream,
    SsfStreamConfiguration.Poll(["CaepSessionRevoked", "CaepCredentialChange"], "my-app"));

SsfSetVerifierOptions verifierOptions = new() { ExpectedAudiencesProvider = () => receiverStream.Audience };
JwtSsfSetVerifier verifier = new(issuer, async ct => (await metadata.ResolveAsync(ct)).JwksUri!, http, verifierOptions);
SsfSetProcessor processor = new(verifier, new InMemorySsfJtiDedupStore(10_000), [handler],
    new SsfSetProcessorOptions { CriticalSubjectMembers = _ => metadata.ResolvedMetadata?.CriticalSubjectMembers });
SsfPoller poller = new(http, tokens, () => receiverStream.Configuration?.DeliveryEndpointUrl, processor,
    new SsfPollerOptions { Transmitter = issuer, LongPollingHold = TimeSpan.FromSeconds(30) });

SsfTransmitter transmitter = new SsfTransmitterBuilder(SsfTransmitter.DefaultName, issuer)
{
    MetadataResolver = metadata, Verifier = verifier, TokenProvider = tokens, StreamClient = streamClient,
    ReceiverStream = receiverStream, StreamRegistrar = registrar, Poller = poller,
}.Build();
transmitter.Start();
// ... on shutdown:
await transmitter.StopAsync();
```

For push delivery, register with `SsfStreamConfiguration.Push(endpointUrl, authorizationHeader,
events, description)` and leave the poller out; `transmitter.PushAuthorizationHeader` is what
the `SsfPushHandler` then expects. Several transmitters go into an `SsfTransmitters`, whose
`Verifier` routes SETs by issuer and whose `PushAuthorizationHeader` looks the header up per
transmitter. `SsfTransmitterFactory` in the hosting package builds all of this from an
`SsfTransmitterOptions`, which is what `AddSsfReceiver()` does for every configured transmitter.

## Typed handlers

For CAEP, RISC and SCIM Events, derive from the handler of the family and override the methods for
the events the application acts on. Each method gets the typed event and the context of the SET;
the subject is `eventContext.Subject` for CAEP and RISC, `scimEvent.Subject` for SCIM.

```csharp
using EasySsf.Core.Caep;
using EasySsf.Core.Scim;
using EasySsf.Receiver.Caep;
using EasySsf.Receiver.Scim;

class StepUpHandler(IStepUp stepUp) : SsfCaepEventHandler
{
    protected override Task OnAssuranceLevelChangeAsync(SsfCaepEvent caepEvent, SsfEventContext eventContext,
            CancellationToken cancellationToken)
    {
        return (caepEvent.ChangeDirection == "decrease")
            ? stepUp.RequireAsync(eventContext.Subject.Subject, caepEvent.CurrentLevel, cancellationToken)
            : Task.CompletedTask;
    }
}

class ProvisioningHandler(IUsers users) : SsfScimEventHandler
{
    protected override Task OnCreateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext,
            CancellationToken cancellationToken)
    {
        return (scimEvent.IsFull && scimEvent.Subject?.ResourceType == "Users")
            ? users.CreateAsync(scimEvent.Subject.Id, scimEvent.Data, cancellationToken)
            : Task.CompletedTask;
    }

    protected override Task OnDeactivateAsync(SsfScimEvent scimEvent, SsfEventContext eventContext,
            CancellationToken cancellationToken)
    {
        return users.DisableAsync(scimEvent.Subject?.Id, cancellationToken);
    }
}
```

An event type in the CAEP or RISC namespace that this version does not know arrives as
`SsfCaepEventKind.Other` or `SsfRiscEventKind.Other` in `OnOtherCaepEventAsync` and
`OnOtherRiscEventAsync`, with the common claims typed and the rest in `Payload`. The enums grow as
the specifications do, so do not switch over them exhaustively. `SsfScimOperation` folds the
`notice` and `full` variants of a provisioning event into one operation; `IsFull` and `IsNotice`
tell which arrived.

## Testing your receiver

`EasySsf.Testing` contains `TestTransmitter`, a transmitter that signs SETs and serves its JWK
Set and metadata to an `HttpClient` in-process, without a socket. The tests of easyssf-dotnet use
it.

```csharp
using EasySsf.Testing;
using static EasySsf.Core.Event.SsfSubjectIdentifiers;

using TestTransmitter transmitter = new();
using HttpClient http = transmitter.CreateHttpClient();
JwtSsfSetVerifier verifier = new(transmitter.Issuer, new Uri(transmitter.JwksUri), http);

JsonObject claims = transmitter.SetClaims("CaepSessionRevoked",
    Complex(IssSub(transmitter.Issuer, "alice"), Opaque("session-1")));
claims["aud"] = "https://my-app.example";
string encodedSet = transmitter.SignSet(claims);
// hand encodedSet to the receiver under test
```

The claims are a `JsonObject`, so a test removes or changes what it wants rejected
(`claims.Remove("jti")`). `TestTransmitter.Sign` signs with any key, algorithm and `typ`,
`TestTransmitter.Unsigned` produces an unsecured JWT, `TestSigningKey.Generate(1024, "weak")`
a key the receiver has to refuse. The subject helpers are the static methods of
`SsfSubjectIdentifiers`. For a polling receiver, `transmitter.QueueSet(encodedSet)` makes the SET
available at the poll endpoint, `transmitter.Available = false` simulates an outage,
`transmitter.AcknowledgedSets` and `transmitter.ReportedErrors` show what the receiver
acknowledged, `transmitter.Streams` the streams it registered. A test with several transmitters
gives each its own host with `TestTransmitter.WithMetadata(location, "okta.example")`.

## Build

```sh
dotnet build
dotnet test
dotnet format        # formatting per .editorconfig; CI fails on unformatted sources
```

.NET SDK 10.0.401 or a later feature band (`global.json`). The libraries build with warnings as
errors and the AOT analysers on. Conventions for contributors and coding agents are in
[AGENTS.md](AGENTS.md), the security policy in [SECURITY.md](SECURITY.md).

## Relation to the Java library

The Java code is the reference: protocol behaviour, names and tests are carried over, so that a
reader can hold the two side by side. The .NET conventions differ where the platform does: types
keep the `Ssf` prefix and interfaces get an `I`, value objects are `sealed record`s over
`JsonElement`, everything that waits is `async` with a `CancellationToken`, the Java setters are
options types, and `TimeProvider` replaces `Clock`. Vendor names are replaced by what a type does
(`NimbusSsfSetVerifier` is `JwtSsfSetVerifier`).
