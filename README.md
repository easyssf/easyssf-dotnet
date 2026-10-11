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
ISsfEventHandler handler = SsfEventHandler.For("CaepSessionRevoked", (context, cancellationToken) =>
{
    SsfSubject subject = context.SubjectFor("CaepSessionRevoked");
    // end what you hold for subject.SessionId or subject.Subject
    return Task.CompletedTask;
});
```

A few lines wire the parts together, see [Receiving pushed SETs](#receiving-pushed-sets); the
hosting and ASP.NET Core integration that does it for you is on the way.

| | |
|---|---|
| **Status** | Experimental, pre-alpha: the port is in progress, nothing is on NuGet yet |
| **.NET** | 10 |
| **Package prefix** | `EasySsf` |
| **License** | Apache-2.0 |

## State of the port

The receiver is ported slice by slice from the Java library, each slice with the Java tests.

| | |
|---|---|
| Done | The vocabulary (`SsfEventToken`, `SsfSubject`, `SsfEventTypes`, ...), SET verification against the transmitter's JWK Set with the SSF error codes, `jti` de-duplication, the push handler (RFC 8935), event handlers, typed CAEP, RISC and SCIM events with dispatching handlers, metrics, a `TestTransmitter` for the tests of a receiver |
| Next | Hosting and ASP.NET Core: options, DI, `MapSsfPush`, metrics, health |
| Then | Polling and stream management (RFC 8936, SSF stream API); token revocation and session termination; ADO.NET stores; the conformance harness |

A transmitter is not planned; `EasySsf.Core` is written so that one could share it.

## Projects

| Project | | Depends on |
|---|---|---|
| `EasySsf.Core` | The data structures of SSF: SETs, subjects, event types, the typed CAEP, RISC and SCIM events, as immutable records over `System.Text.Json` elements. Trim- and AOT-safe, no reflection-based serialization. | nothing |
| `EasySsf.Receiver` | The receiver, independent of any framework: SET verification, de-duplication, event handlers, push handling, CAEP, RISC and SCIM handlers and metrics today; polling, stream management, revocation and session termination as the port proceeds. | `EasySsf.Core`, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Logging.Abstractions` |
| `EasySsf.Testing` | Test support: a transmitter that signs SETs and serves its JWK Set and metadata through an `HttpMessageHandler`, see [Testing your receiver](#testing-your-receiver). | `EasySsf.Core` |

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
- **Typed events**: `SsfCaepEvent` (CAEP 1.0, the common claims of section 2 and the
  event-specific ones of section 3), `SsfRiscEvent` (RISC 1.0) and `SsfScimEvent` with
  `SsfScimSubject` (SCIM Events, RFC 9967), each read from a SET with `In(eventToken)`, and the
  handler base classes that dispatch them, see [Typed handlers](#typed-handlers).

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
`SsfSubjectIdentifiers`. The token, stream management and poll endpoints of the Java
`TestTransmitter` arrive with the polling slice.

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
