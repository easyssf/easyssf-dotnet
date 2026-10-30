# Changelog

The notable changes of every release. The section of a version is the text of its
[GitHub release](https://github.com/easyssf/easyssf-dotnet/releases). The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

The version line starts at 0.6.0, not at 0.1.0: the packages follow the release line of
[easyssf for Java](https://github.com/easyssf/easyssf), of which this library is a port. The port
was written against the Java library as it stood for its 0.6.0 release and carries that state in
full: the same protocol behaviour, the same options and defaults, the same database tables, and
the lessons the port taught the Java library in return went into both at once (the audience a
receiver has to know, the revocation TTL of a day, the renewal of a dedup claim while handlers
run). The same version therefore means the same feature level on both sides, and a reader can hold
the two changelogs side by side. Versions 0.1.0 to 0.5.0 never existed in .NET. Below 1.0 a
minor version may break the API, as in the Java library; between two minor versions, `-alpha.N`
prereleases follow the Java development line (`0.7.0-alpha.N` alongside `0.7.0-SNAPSHOT`).

## [Unreleased]

### Security

- The poll endpoint is held to the URI policy of the transmitter's metadata endpoints before the
  access token is sent to it, whether it was configured (`EasySsf:Receiver:Poll:EndpointUrl`, checked
  at startup) or taken from the stream configuration the transmitter returned: `https`, or `http` on
  loopback addresses only, unless `AllowInsecureHttp` is set. The acknowledgements sent on stop pass
  the same check. New API: `SsfPollerOptions.UriPolicy`.
- Response bodies the receiver reads from the transmitter (metadata, JWK Set, token, stream
  configurations, poll responses) are bounded, 4 MiB by default; a larger response fails the call as
  if the transmitter were unavailable instead of being buffered. The hosting integration sets the
  limit as `MaxResponseContentBufferSize` of its HTTP clients from the new setting
  `EasySsf:Receiver:Http:MaxResponseSize`; a framework-free receiver sets it on its `HttpClient`.

### Changed

- `SsfStreamClient` checks the issuer of every stream configuration the transmitter returns, not only
  of a created one: `GetStreamAsync`, `GetStreamsAsync`, `UpdateStreamAsync` and `ReplaceStreamAsync`
  throw `SsfStreamIssuerMismatchException` as `CreateStreamAsync` did, as the SETs of a stream with
  another issuer would not verify. `SsfStreamRegistrar` deletes a stream it created with the wrong
  issuer, as before, and leaves an existing one alone; it fails without retrying either way.

### Fixed

- A SET the poll response lists under a key other than its `jti` (RFC 8936, section 2.4) is reported
  to the transmitter as `invalid_request` under that key instead of being handled and acknowledged
  under a key that is not its own. New API: `SsfSetClaims.UnverifiedJti`.
- `SsfStreamRegistrar.StopAsync()` and `Start()` no longer leave a stream behind or delete the wrong
  one. A registration that completes after `StopAsync()`, because its `HttpMessageHandler` did not
  observe the cancellation, publishes nothing and deletes a stream it created on the way if the
  registrar deletes on shutdown; if the registrar was started again meanwhile, it leaves that stream
  to the new registration, which adopts it if it finds it and deletes it once it publishes another
  one; a stream whose deletion is under way is not adopted. A publication in progress completes
  before `StopAsync()` goes on, and `StopAsync()` deletes only a stream published since the last
  stop, not again the one a previous stop deleted. `StopAsync()` logs a warning when the
  registration is still running after `StopTimeout` (new, 5 seconds by default).
- The conformance tests start again on GitHub's current Ubuntu runners: their MongoDB is 9.0.4 now,
  pinned by digest. MongoDB 8.0 and newer exit at startup on a Linux kernel from 6.19 up to 7.0.13,
  and before 9.0.4 they took Ubuntu's 7.0 kernels for 7.0.0 (SERVER-121912, SERVER-131779), so the
  suite found no database and the conformance run failed since the runner image of 2026-10-18.

## [0.6.0] - 2026-10-12

The release of 0.6.0-alpha.1 under the version of the Java library it matches; the code is the
same. The README shows the `PackageReference` of every package.

## [0.6.0-alpha.1] - 2026-10-12

The first release: a receiver for the OpenID Shared Signals Framework 1.0 with CAEP, RISC and SCIM
Events, as a framework-free .NET library with hosting, ASP.NET Core and ADO.NET integrations and
an in-process test transmitter. Six packages under the reserved prefix `EasySsf`, targeting
`net10.0`, trim- and AOT-safe except where noted. Experimental: a minor version may break the API.

### Added

- `EasySsf.Core`: the vocabulary of the protocol. Security Event Tokens, the event types of SSF,
  CAEP, RISC and SCIM Events (RFC 9967) by URI and by alias (and your own), subjects with every
  RFC 9493 identifier format, the members of complex subjects and the `scim` format, transmitter
  metadata and stream configurations, and the typed `SsfCaepEvent`, `SsfRiscEvent` and
  `SsfScimEvent`, all as immutable records over `System.Text.Json` elements, with no
  reflection-based serialization.
- `EasySsf.Receiver`: SET verification with `Microsoft.IdentityModel.JsonWebTokens` (`RS256` by
  default, any asymmetric algorithm by configuration, RSA keys of at least 2048 bits), a JWK Set
  cache that refetches on an unknown key, rate limited; push delivery (RFC 8935) with the error
  codes of the specification, polling (RFC 8936) with short and long polling, acknowledgements
  kept until delivered and `Retry-After` backoff; stream management with discovery from
  `.well-known/ssf-configuration` and a background registrar with retries; de-duplication by `jti`
  with a lease that an instance renews while its handlers run, so a handler may take longer than
  the lease; issuer-scoped token revocation and session termination; several transmitters routed
  by issuer; handlers, generic and per event family; metrics with `System.Diagnostics.Metrics`
  under the Java library's names.
- Strict by default: the top-level `sub_id` of SSF 1.0 is required
  (`SubjectCompatibilityMode.Legacy` accepts transmitters following earlier drafts), verification
  events must name the stream, subject members a transmitter declares critical must be understood
  by the application, transmitter issuers and endpoints must use `https` (`AllowInsecureHttp` for
  development), and a receiver has to know the audience of its SETs: without `ExpectedAudience`
  and without a stream of its own it refuses to start unless `SetValidation:AllowAnyAudience` says
  so, and a SET that arrives before the stream is known is left for redelivery.
- `EasySsf.Receiver.Hosting`: options bound from `EasySsf:Receiver` with the configuration binding
  source generator, `AddSsfReceiver()`, `SsfTransmitterFactory`, options validation on startup,
  the hosted service that registers the stream and polls, and a health check.
- `EasySsf.Receiver.AspNetCore`: `MapSsfPush()`, the push endpoint; `AddSsfTokenRevocation()`,
  which rejects the access tokens of a revoked session or user in a JwtBearer scheme, and
  `AddSsfSessionRevocation()`, which signs such a principal out of a cookie scheme on its next
  request. Revocations are remembered for `Revocation:Ttl`, a day by default, and the cookie hook
  refuses a cookie lifetime longer than that.
- `EasySsf.Receiver.Data`: the processed-SET, revocation and poll-acknowledgement stores on
  ADO.NET, plain SQL over `ISsfDbOperations` with the tables of the Java library, migrations, a
  cleanup service; tested on SQLite and PostgreSQL.
- `EasySsf.Testing`: `TestTransmitter`, an in-process transmitter that signs SETs, serves its JWK
  Set and metadata, issues tokens and emulates stream management and polling through an
  `HttpMessageHandler`, for the tests of a receiver.
- Examples against Keycloak (a resource server and an OIDC client) and a SCIM provisioning
  example; the conformance harness and the receiver under test, with which the receiver passes the
  SSF 1.0 and CAEP Interoperability Profile receiver plans of release 5.3.2 of the OpenID
  conformance suite with push and poll delivery. Both are built with the solution and not
  published.

[Unreleased]: https://github.com/easyssf/easyssf-dotnet/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/easyssf/easyssf-dotnet/compare/v0.6.0-alpha.1...v0.6.0
[0.6.0-alpha.1]: https://github.com/easyssf/easyssf-dotnet/releases/tag/v0.6.0-alpha.1
