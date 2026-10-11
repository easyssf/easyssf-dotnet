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
// the shape the port is heading for; the handler interface arrives with the next slice
ISsfEventHandler handler = SsfEventHandler.For("CaepSessionRevoked", (context, cancellationToken) =>
{
    SsfSubject subject = context.SubjectFor("CaepSessionRevoked");
    // end what you hold for subject.SessionId or subject.Subject
    return Task.CompletedTask;
});
```

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
| Done | The vocabulary (`SsfEventToken`, `SsfSubject`, `SsfEventTypes`, ...), SET verification against the transmitter's JWK Set with the SSF error codes, a `TestTransmitter` for the tests of a receiver |
| Next | SET processing: `jti` de-duplication, the push handler (RFC 8935), the event context and handler interface |
| Then | Typed CAEP, RISC and SCIM events and handlers; hosting and ASP.NET Core (options, DI, `MapSsfPush`, metrics, health); polling and stream management (RFC 8936, SSF stream API); token revocation and session termination; ADO.NET stores; the conformance harness |

A transmitter is not planned; `EasySsf.Core` is written so that one could share it.

## Projects

| Project | | Depends on |
|---|---|---|
| `EasySsf.Core` | The data structures of SSF: SETs, subjects, event types, as immutable records over `System.Text.Json` elements. Trim- and AOT-safe, no reflection-based serialization. | nothing |
| `EasySsf.Receiver` | The receiver, independent of any framework: SET verification today; de-duplication, event handlers, push handling, polling, stream management, revocation and session termination as the port proceeds. | `EasySsf.Core`, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Logging.Abstractions` |
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
