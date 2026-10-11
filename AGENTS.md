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
| `src/EasySsf.Core` | The vocabulary: SETs, subjects, event types, the typed CAEP, RISC and SCIM events, stream configuration, transmitter metadata, as immutable records over `JsonElement` | BCL only |
| `src/EasySsf.Testing` | `TestTransmitter`, an in-process transmitter that signs SETs, serves its JWK Set and metadata, issues tokens and emulates stream management and polling through an `HttpMessageHandler` | Core |
| `src/EasySsf.Receiver` | The receiver without a framework: verification, de-duplication, push and poll, stream management, transmitter assembly, handlers (generic and per event family), the revocation store and session termination, metrics | Core, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Logging.Abstractions` |
| `src/EasySsf.Receiver.Hosting` | Options bound from `EasySsf:Receiver`, `AddSsfReceiver()`, `SsfTransmitterFactory`, options validation, the hosted service and the health check | Receiver, `Microsoft.Extensions.Hosting.Abstractions`, `.Http`, `.Options.ConfigurationExtensions`, `.Diagnostics.HealthChecks` |
| `src/EasySsf.Receiver.AspNetCore` | `MapSsfPush()`, the push endpoint; `AddSsfTokenRevocation()` and `AddSsfSessionRevocation()`, the JwtBearer and cookie hooks | Hosting, `Microsoft.AspNetCore.App`, `Microsoft.AspNetCore.Authentication.JwtBearer` |
| `src/EasySsf.Receiver.Data` | The database stores (processed SETs, revocations, poll acknowledgements) in plain SQL over `ISsfDbOperations`, `DbSsfSchema` with the SQL and migrations, `AddDatabaseStores()`, the cleanup hosted service | Hosting, `System.Data.Common` |
| `src/EasySsf.Testing.Conformance` | The conformance harness: the suite in Testcontainers, its API client, the receiver plans, the scenarios a receiver plays (`ConformanceRun`), the expected outcome per module and `ConformancePlanRunner` | Receiver, Testcontainers |
| `tests/EasySsf.Conformance.Receiver` | The receiver under test for the suite, an ASP.NET Core application (`CtsApplication`) with the `/cts/*` control endpoints and `/ssf/push`; also runs by hand against a local suite | Testing.Conformance, ASP.NET Core |
| `examples/` | A resource server and an OIDC client against Keycloak (`keycloak/compose.yaml`, realm `ssf-demo`) and a SCIM provisioning example with the in-process `TestTransmitter`; not published, built with the solution | AspNetCore, Hosting, Testing |
| `tests/EasySsf.*.Tests` | xUnit tests, ported from the Java modules; the AspNetCore tests start Kestrel on a random port, the Data tests run on SQLite files and, opted in, PostgreSQL in Docker | the project under test, Testing |

New protocol behaviour goes into `EasySsf.Receiver`, new vocabulary into `EasySsf.Core`, framework
code only into the Hosting, AspNetCore and Data projects.

## Porting

- The Java module maps to the project, the Java package to the namespace: `org.easyssf.core.event`
  is `EasySsf.Core.Event`, `org.easyssf.receiver.set` is `EasySsf.Receiver.Set`. Folders follow
  namespaces (`dotnet_style_namespace_match_folder`).
- Port a Java type with its tests, the negative tests (the SET that must be rejected) before the
  positive ones, and keep the test names as sentences so that the two suites can be compared.
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
- The tests need no network and no Docker: `TestTransmitter` answers in-process and the Data
  tests use SQLite files. `EASYSSF_DATABASE_TESTS=true dotnet test tests/EasySsf.Receiver.Data.Tests`
  runs them on PostgreSQL as well, in a Testcontainers container (Docker required; CI does this in
  the `database` job). The database tests are `[Theory]`s over `TestDatabase.All`, which lists the
  PostgreSQL case only when the variable is set, so nothing is skipped or filtered. Run them when the
  SQL or the schema changes, and say so.
- The conformance plans (`tests/EasySsf.Conformance.Tests`, the four `...ReceiverConformanceTests`
  classes) are `[ConformanceTheory]`s, skipped unless `EASYSSF_CONFORMANCE_TESTS` is set; with it,
  the suite starts in Docker at `https://localhost:18443` and the modules of a plan are discovered
  from it when the tests are collected. Minutes per plan: run only the plan that covers the behaviour
  you changed (`--filter "FullyQualifiedName~CaepInterop10PushReceiverConformanceTests"`) and report
  the suite's result, not only the xUnit outcome. The suite images are named in one place,
  `ConformanceSettings`, pinned by digest; a module that fails prints the suite's failures and
  warnings, the receiver's log and a link to the suite's log page. Start from the suite log, the
  receiver is usually right. The tests of the harness against the `TestTransmitter`
  (`ConformanceRunLocalTransmitterTests`) need no Docker and run always.
- **Options bind from configuration with the binding source generator**
  (`EnableConfigurationBindingGenerator`), so the Hosting package stays AOT-safe. Binding adds to
  a list instead of replacing it, so a list setting with a default starts empty and an
  `Effective...` property resolves the default (`EffectiveAcceptedAlgorithms`,
  `EffectiveEventsRequested`, `EffectiveUnderstoodSubjectMembers`). Never give a bound list a
  non-empty default. Configuration keys are PascalCase with colons (`EasySsf:Receiver:Poll:Interval`)
  and messages name them that way; `SsfTransmitterFactory.Prefix(name)` builds the prefix.
- **Services registered before `AddSsfReceiver()` win** (`TryAdd`): a store, metrics or ack store
  of the application replaces the default. An integration that brings stores of its own, like the
  Data project, uses `SsfReceiverBuilder.ReplaceDefaultStore`, which replaces only the stores the
  receiver registered by default. The parts of a transmitter are not services; replace them with
  an `ISsfTransmitterCustomizer`.
- **The database stores are plain SQL** over `ISsfDbOperations`: `?` placeholders in the order of
  the arguments, `VARCHAR`/`BIGINT`, timestamps as milliseconds since the epoch, no vendor syntax;
  a result limit goes through `QueryAsync(sql, maxRows, ...)` (the reader stops), never `LIMIT`,
  `TOP` or `FETCH FIRST`. `schema.sql` is the current schema and has to run unchanged on SQLite,
  PostgreSQL and MySQL (the Java library shares the tables, keep them identical). Every schema
  change also ships `migration/V<release>__<topic>.sql`, an entry in `DbSsfSchema.Migrations`
  and, for a new or renamed column, an entry in the table's `*Upgrades` list so that
  `PrepareTableAsync` adds it where it may create tables; `MigrationScriptsLeadToTheCurrentSchema`
  checks that the scripts end at the current schema. `DbSharedReceiversTests` is the place for
  multi-instance scenarios (two pollers, one database).
- **Authentication hooks are options extensions** (`UseSsfTokenRevocation()` on `JwtBearerOptions`,
  `UseSsfSessionRevocation()` on `CookieAuthenticationOptions`) that chain the existing event
  handler, plus `AddSsf...()` service extensions that apply them with `PostConfigure` so the
  application's own configuration runs first. Cookies are stateless: the cookie hook rejects a
  revoked principal when it is presented; `ISsfSessionTerminator` is for server-side sessions.
  Tests that log in several users must use an `HttpClient` with `UseCookies = false`, or the
  cookie container sends the last cookie with every request.
- **Background work is `Task`-based.** Poller and registrar run on `Task.Run` with a
  `CancellationTokenSource`; `Start()` is synchronous, `StopAsync()` cancels and awaits. Tests of
  background behaviour use `Eventually.UntilAsync` / `DuringAsync` (the Awaitility of this repo)
  and keep waits short. A `TimeProvider` paces delays where a test needs to control time.
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
  next, its "Configuration" table lists every setting with its default, and a new option, alias,
  subject format or handler is documented there in the same change. Keep its examples compiling
  against the current API.
- `CHANGELOG.md` does not exist yet; it comes with the first release and follows Keep a Changelog.
  Until then, the "State of the port" table of the README says what is done and what comes next.
- `SECURITY.md` is the vulnerability reporting policy; do not change it as part of a feature.

## Working practice

- `scratch/` is ignored by git: put plans, notes and throwaway scripts there, not into the tree.
- Do not commit, push, tag or publish unless asked. Commits have a short imperative subject, a
  wrapped body that says what and why, and are signed off (`git commit -s`); the CI checks the
  `Signed-off-by` trailer of every pull request commit.
- CI (`.github/workflows/ci.yml`) runs the cheap checks first: sign-off, then a link and spell
  check of the Markdown files for documentation changes, then formatting, build and tests on
  Ubuntu; Windows and macOS follow only when that passes. A change that touches only `.md` files
  runs no build at all. `status` is the required check. `conformance.yml` runs the four conformance
  plans on demand (`workflow_dispatch`), one job per plan.
- When a change touches verification, push handling or de-duplication, add the negative test
  before the positive one, and run the full test suite, not only the new test.
- The Keycloak examples contain no SSF code apart from configuration (and a logging handler). Keep
  them that way: a feature that needs code in an example is a feature the hosting integration is
  missing. The SCIM example's handler is application logic (what to do with a provisioning event),
  that is fine. `tests/EasySsf.Examples.ScimProvisioning.Tests` are the only tests among the
  examples; they start the application against a `TestTransmitter` on a random port and poll every
  200 ms. The realm `ssf-demo` is imported from `examples/keycloak/`, the same files as in the Java
  repository; change the realm export there, not in a running Keycloak. Changing the ports (8080
  Keycloak, 8081 resource server, 8082 OIDC client, 8083 SCIM provisioning) means changing the
  realm export, the `appsettings.json` files and the README together.
- Do not start Docker containers or the examples' Keycloak without checking for an existing
  compose project of the same name (`docker ps -a --filter label=com.docker.compose.project=easyssf-dotnet-examples`);
  the compose file sets `name: easyssf-dotnet-examples` for that reason, never remove the `name`,
  and never run `down -v` or delete volumes you did not create. A conformance run of the Java repository next door binds the
  same ports (suite 18443, receiver 9444); check `docker ps` first and move with `CTS_SUITE_PORT`
  and `CTS_RECEIVER_PORT` rather than stopping containers you did not start.
