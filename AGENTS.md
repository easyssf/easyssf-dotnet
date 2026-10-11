# easyssf-dotnet for coding agents

Read this first. This file holds what is not obvious from the code: what the repository is, how
it relates to the Java original, the conventions it follows, and the commands that matter. A
project with rules of its own has an `AGENTS.md` of its own; the closest one applies.

## What this is

.NET building blocks for the OpenID Shared Signals Framework (SSF 1.0) with CAEP, RISC and SCIM
Events (RFC 9967): a framework independent **receiver** library, with hosting and ASP.NET Core
integration to follow. A receiver verifies Security Event Tokens (SETs, RFC 8417) a transmitter
pushes (RFC 8935) or it polls (RFC 8936), de-duplicates them, manages its stream at the
transmitter and hands the events to handlers. The code sits in the authentication path of its
users, so correctness of verification beats convenience everywhere. Receiver only, no
transmitter. Apache-2.0. Status: experimental, pre-0.1, breaking changes are acceptable.

This is a port of [easyssf for Java](https://github.com/easyssf/easyssf). The Java code is the
reference for protocol behaviour, names and tests; a local checkout is expected at `../easyssf`.
Its `AGENTS.md` conventions apply here unless this file says otherwise. Do not use the source of
`Abblix.SharedSignals` as a reference: its license forbids using the source to build a competing
product. Work from the Java code and the specifications.

SDK 10.0.401 (`global.json`), target `net10.0`, solution `EasySsf.slnx`. Projects, in dependency
order:

| Project | Holds | Depends on |
|---|---|---|
| `src/EasySsf.Core` | The vocabulary: SETs, subjects, event types, the typed CAEP, RISC and SCIM events, as immutable records over `JsonElement` | BCL only |
| `src/EasySsf.Testing` | `TestTransmitter`, an in-process transmitter that signs SETs and serves its JWK Set through an `HttpMessageHandler` | Core |
| `src/EasySsf.Receiver` | The receiver without a framework: verification, de-duplication, push, handlers (generic and per event family) and metrics; later poll, stream management, revocation and session termination | Core, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Logging.Abstractions` |
| `tests/EasySsf.Core.Tests`, `tests/EasySsf.Receiver.Tests` | xUnit tests, ported from the Java modules | the project under test, Testing |

Planned, in this order: `EasySsf.Receiver.Hosting` (options, DI, background services, health),
`EasySsf.Receiver.AspNetCore` (push endpoint, JwtBearer and cookie integration),
`EasySsf.Receiver.Data` (ADO.NET stores, same tables as the Java JDBC module),
`tests/EasySsf.Conformance`. New protocol behaviour goes into `EasySsf.Receiver`, new vocabulary
into `EasySsf.Core`, framework code only into the Hosting and AspNetCore projects.

## Porting

- The Java module maps to the project, the Java package to the namespace: `org.easyssf.core.event`
  is `EasySsf.Core.Event`, `org.easyssf.receiver.set` is `EasySsf.Receiver.Set`. Folders follow
  namespaces (`dotnet_style_namespace_match_folder`).
- Port a Java type with its tests, the negative tests (the SET that must be rejected) before the
  positive ones, and keep the test names as sentences so that the two suites can be compared.
- `scratch/porting.md`, when present, is the handoff document: the state of the port, the
  decisions taken and the order of the remaining slices. Update it when a slice is finished or a
  decision is made. `scratch/` is ignored by git, so the file is local to the machine.
- When the Java code and this file disagree on a .NET matter (naming, async, JSON model), this
  file wins; on a protocol matter, the Java code and the specification win.

## Build and test

```sh
dotnet build                                    # warnings are errors in src/, AOT analysis on
dotnet test                                     # all tests
dotnet test tests/EasySsf.Receiver.Tests        # one project
dotnet test --filter "FullyQualifiedName~JwtSsfSetVerifierTests"
dotnet format --verify-no-changes               # formatting per .editorconfig
```

- `src/Directory.Build.props` sets `TreatWarningsAsErrors`, `IsAotCompatible` and XML
  documentation for the libraries. An `IL2026`/`IL3050` error means a reflection-based or
  dynamic-code path crept in; fix the code, do not suppress the warning.
- The tests need no network and no Docker: `TestTransmitter` answers in-process. Tests that will
  need Docker (ADO.NET stores, the conformance harness) are to be kept behind a trait so that the
  normal `dotnet test` stays self-contained.
- Formatting follows `.editorconfig` (4 spaces, file-scoped namespaces, braces on their own line,
  `System` usings first, usings grouped). Run `dotnet format` rather than formatting by hand.

## Code conventions

- **Names.** Public types keep the `Ssf` prefix of the Java code (`SsfSubject`, `SsfEventToken`),
  interfaces get `I` (`ISsfSetVerifier`, `ISsfEventHandler`). Vendor names of the Java code are
  replaced by what the type does (`NimbusSsfSetVerifier` is `JwtSsfSetVerifier`). Tests are
  `<Type>Tests`, methods are sentences in PascalCase (`RejectsSetSignedWithUnknownKey`).
- **Value objects are `sealed record`s**, immutable. Raw claims, event payloads and subjects are
  `JsonElement`s, cloned so that they outlive their document; typed accessors sit on top.
  Collections are copied and exposed read-only, in the order received. Equality compares
  `JsonElement`s with `JsonElement.DeepEquals`. Builders (`SsfSubjectIdentifiers`) return
  `JsonObject`s and deep-clone nodes they compose, because a node has one parent.
- **No reflection-based serialization.** `System.Text.Json` through `JsonDocument`,
  `Utf8JsonWriter` and `JsonNode` only; no `JsonSerializer.Serialize<T>` over user types. The
  libraries must stay trim- and AOT-safe.
- **Async everywhere.** Verifier, handlers, stores and HTTP are `...Async(..., CancellationToken)`.
  `TimeProvider` replaces `Clock`; tests drive it by hand.
- **Configuration objects, not setters.** Java setters become options types
  (`SsfSetVerifierOptions`), read when the work is done, so a change takes effect for the next
  SET.
- **HTTP** takes an `HttpClient`; tests swap the `HttpMessageHandler`. Logging is `ILogger`,
  metrics are `System.Diagnostics.Metrics` with the Java names (`easyssf.receiver.sets`,
  `.events`, `.poll`).
- **Nullable reference types are on.** A lenient Java `from(...)` that returns `null` returns a
  nullable here; argument checks use the BCL throw helpers (`ArgumentNullException.ThrowIfNull`,
  `ArgumentException.ThrowIfNullOrWhiteSpace`).
- **Protocol facts carry their source.** XML docs and comments cite the specification and section
  (`SSF 1.0 8.1.4.1`, `RFC 9967, section 2.4`) and name the real-world deviation they tolerate
  (`// CAEP defines seconds since epoch, some transmitters send milliseconds`). A reader must be
  able to check a rule against the spec.
- **Event types are URIs; aliases are a convenience.** `SsfEventTypes` maps aliases such as
  `CaepSessionRevoked` to URIs and back; everything that takes an event type accepts both and
  resolves with `SsfEventTypes.Resolve`. A new event family gets constants, aliases registered in
  the static constructor, and a mention in the class documentation. A new event of CAEP or RISC
  also gets a member of `SsfCaepEventKind`/`SsfRiscEventKind` (before `Other`), a row in the
  `...Kinds` static class, an `On...Async` method of the handler, and a line in the two
  `DispatchesEveryKindToItsMethod` tests.
- **Enums carry no data.** Where a Java enum has fields (`SsfCaepEventKind.eventType()`), the C#
  enum is plain and a static class of the plural name (`SsfCaepEventKinds`) holds the extension
  methods and the `Of(...)` lookup.
- **Strict by default, lenient by configuration.** The receiver rejects what the specification
  forbids (`invalid_request`) and tolerates older drafts or known transmitter quirks only behind
  an explicit mode (`SubjectCompatibilityMode.Legacy`), each documented. The SSF error codes
  (`invalid_request`, `invalid_key`, `invalid_issuer`, `invalid_audience`) are decided by our
  code, not by the JWT library; a key that is unknown is `invalid_key`, keys that cannot be
  fetched are `SsfTransmitterUnavailableException`.
- **Handlers are idempotent** and a SET that any handler fails on is delivered again. State that
  must survive a restart belongs in a store interface with an in-memory and an ADO.NET
  implementation.
- Every public type and member of `src/` has an XML documentation comment that says what it is
  for, not what it is named. No new package references in `EasySsf.Core` and `EasySsf.Receiver`
  without discussion.

## Documentation that changes with the code

- `README.md` is the user document: its "State of the port" table says what is done and what comes
  next, and a new option, alias, subject format or handler is documented there in the same change.
  Keep its examples compiling against the current API.
- `CHANGELOG.md` does not exist yet; it comes with the first release and follows Keep a Changelog.
  Until then, `scratch/porting.md` records state and decisions (see Porting).
- `SECURITY.md` is the vulnerability reporting policy; do not change it as part of a feature.

## Working practice

- `scratch/` is ignored by git: put plans, notes and throwaway scripts there, not into the tree.
- Do not commit, push, tag or publish unless asked. Commits have a short imperative subject, a
  wrapped body that says what and why, and are signed off (`git commit -s`); the CI checks the
  `Signed-off-by` trailer of every pull request commit.
- CI (`.github/workflows/ci.yml`) runs the cheap checks first: sign-off, then a link and spell
  check of the Markdown files for documentation changes, then formatting, build and tests on
  Ubuntu; Windows and macOS follow only when that passes. A change that touches only `.md` files
  runs no build at all. `status` is the required check.
- When a change touches verification, push handling or de-duplication, add the negative test
  before the positive one, and run the full test suite, not only the new test.
- Do not start Docker containers or the examples' Keycloak without checking for an existing
  compose project of the same name.
