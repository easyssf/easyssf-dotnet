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
the two changelogs side by side. Versions 0.1.0 to 0.5.0 never existed in .NET. The first releases
of the line are prereleases (`0.6.0-alpha.N`) while the API settles; `0.6.0` follows.

## [Unreleased]

## [0.6.0-alpha.1] - 2026-10-12

The first release: a receiver for the OpenID Shared Signals Framework 1.0 with CAEP, RISC and SCIM
Events, as a framework-free .NET library with hosting, ASP.NET Core and ADO.NET integrations and
an in-process test transmitter. Six packages under the reserved prefix `EasySsf`, targeting
`net10.0`, trim- and AOT-safe except where noted. Experimental: breaking changes are to be expected
until `0.6.0`.

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

[Unreleased]: https://github.com/easyssf/easyssf-dotnet/compare/v0.6.0-alpha.1...HEAD
[0.6.0-alpha.1]: https://github.com/easyssf/easyssf-dotnet/releases/tag/v0.6.0-alpha.1
