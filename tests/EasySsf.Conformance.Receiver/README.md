# easyssf conformance tests

Runs easyssf for .NET against the [OpenID conformance suite](https://gitlab.com/openid/conformance-suite).
Three projects take part:

- **`src/EasySsf.Testing.Conformance`**, the harness: starts the suite with Testcontainers
  (`ConformanceSuite`), talks to its API (`ConformanceApiClient`), knows the receiver plans
  (`ConformancePlan`), the scenarios the receiver plays (`ConformanceScenario`, `ConformanceRun`,
  `ConformanceRunner`) and the result each module has to end with (`ConformanceModuleExpectation`),
  and runs a plan module by module (`ConformancePlanRunner`). A receiver in another framework runs
  the same plans with it.
- **`tests/EasySsf.Conformance.Receiver`** (this project), the **receiver under test**: a small
  ASP.NET Core application that acts as the SSF receiver the suite tests with its
  `openid-ssf-receiver-caep-test-plan` and `openid-ssf-receiver-test-plan`. The suite emulates a
  transmitter and an authorization server; the application plays the stream operations the tests
  expect and accepts the events they deliver. Every test of the suite emulates a transmitter with
  new signing keys under the same issuer, so every test run gets a fresh receiver assembled from
  `EasySsf.Receiver` (see `ConformanceRun`): token, verifier, de-duplication and stream are per run.
- **`tests/EasySsf.Conformance.Tests`**, the **automated conformance tests**: four classes of a few
  lines that run the plans against that application, plus the tests of the harness itself against
  the `TestTransmitter` of the receiver library, which need no Docker.

## Automated tests

The plan tests are skipped unless `EASYSSF_CONFORMANCE_TESTS` is set; a plan takes a few minutes,
as each module waits for the receiver to deliver, verify and fall idle.

```sh
# all four plans (default and CAEP interop profile, push and poll delivery)
EASYSSF_CONFORMANCE_TESTS=true dotnet test tests/EasySsf.Conformance.Tests
# one plan
EASYSSF_CONFORMANCE_TESTS=true dotnet test tests/EasySsf.Conformance.Tests --filter "FullyQualifiedName~CaepInterop10PushReceiverConformanceTests"
```

| Test class | Plan | Variant | Suite configuration |
|---|---|---|---|
| `Ssf10PushReceiverConformanceTests` | `openid-ssf-receiver-test-plan` | `ssf_delivery_mode=push` | `receiver-ssf1.0-push.json` |
| `Ssf10PollReceiverConformanceTests` | `openid-ssf-receiver-test-plan` | `ssf_delivery_mode=poll` | `receiver-ssf1.0-poll.json` |
| `CaepInterop10PushReceiverConformanceTests` | `openid-ssf-receiver-caep-test-plan` | `ssf_delivery_mode=push` | `receiver-ssf1.0-caepiop1.0-push.json` |
| `CaepInterop10PollReceiverConformanceTests` | `openid-ssf-receiver-caep-test-plan` | `ssf_delivery_mode=poll` | `receiver-ssf1.0-caepiop1.0-poll.json` |

The configurations are in [`src/EasySsf.Testing.Conformance/suite-config`](../../src/EasySsf.Testing.Conformance/suite-config).
All plans use `ssf_auth_mode=dynamic` and `client_auth_type=client_secret_basic`. A test class names
its `ConformancePlan`, the harness creates it in the suite and the class runs one theory case per
module of the plan. The cases are the modules of the pinned suite release
(`ConformancePlans.KnownModules`), so that collecting the tests needs no suite; a further test per
plan fails when the suite in use has other modules, which is the cue to update the list. `ConformanceScenarios.ForModule`
maps a module to the scenario the receiver plays (see [Scenarios](#scenarios)),
`ConformanceModuleExpectation` to the status its run has to end with; a failed module fails with the
failures and warnings the suite logged, the receiver's log and the link to the suite's log page. The
four classes share one xUnit collection, so they run one after another against the one receiver
and the one suite.

### The suite

The suite is started with [Testcontainers](https://testcontainers.com) (MongoDB, the suite and its
nginx, like `docker-compose-prebuilt.yml` of the suite's repository), so Docker is all that is
needed. The suite has to know its own URL before it starts, so nginx is bound to a fixed host port
(`https://localhost:18443`, where the suite's UI shows the plans while the tests run), and the suite
reaches the receiver on the Docker host as `host.docker.internal`, which the container gets mapped
to the host gateway.

| Environment variable | Default | |
|---|---|---|
| `CTS_SUITE_IMAGE` | `registry.gitlab.com/openid/conformance-suite@sha256:…` (`release-v5.3.2`, pinned by digest) | the suite image |
| `CTS_SUITE_NGINX_IMAGE` | `registry.gitlab.com/openid/conformance-suite/nginx@sha256:…` (`release-v5.3.2`) | its nginx image |
| `CTS_SUITE_MONGODB_IMAGE` | `mongo@sha256:…` (`9.0.4`, pinned by digest; the suite itself runs 6.0.13 in production, older 8.x and 9.x releases do not start on current Ubuntu kernels) | its MongoDB image |
| `CTS_SUITE_PORT` | `18443` | host port of the Testcontainers-started suite |
| `CTS_RECEIVER_PORT` | `9444` | port of the receiver under test |
| `CTS_RECEIVER_PUSH_URL` | `https://host.docker.internal:<receiver port>/ssf/push` | the push URL the receiver registers |

**Which suite.** `release-v5.3.2` (2026-10-09) is the first release with the SSF receiver test fixes
made since `release-v5.3.1`, so the tests run that release, pinned by digest in `ConformanceSettings`,
the single place to change the images. To run a build of your own, build the image from a checkout
like the suite's CI does (`.gitlab-ci.yml`, job `build-image`: `mvn package`, then
`docker build -t conformance-suite .`) and name it; Testcontainers uses images of the local Docker
daemon as they are:

```sh
CTS_SUITE_IMAGE=conformance-suite:master-949724883 EASYSSF_CONFORMANCE_TESTS=true dotnet test tests/EasySsf.Conformance.Tests
```

The nginx image is independent of the suite version; the released one does for a locally built suite.

## Manual runs

### Setup (once)

1. **TLS certificate for this application**: the suite only accepts `https` push endpoints.

   ```sh
   cd tests/EasySsf.Conformance.Receiver
   mkcert -cert-file certs/localhost.pem -key-file certs/localhost-key.pem localhost 127.0.0.1 ::1 host.docker.internal
   ```

2. **Certificate of the suite**: a locally running suite (`https://localhost.emobix.co.uk:8443`)
   serves a self-signed certificate. Save it so that the receiver trusts it:

   ```sh
   ./fetch-suite-certificate.sh          # writes certs/conformance-suite.pem
   ```

   The receiver trusts the certificate in that file whatever host name it is presented for (the
   certificate is issued for `localhost`, not `localhost.emobix.co.uk`). Against a suite with a
   valid certificate remove `Cts:Transmitter:CertificateFile`.

3. **Test plan in the suite**: create a plan with the configuration of the variant you want to run
   (`suite-config/` in [`src/EasySsf.Testing.Conformance`](../../src/EasySsf.Testing.Conformance/suite-config)),
   and point the application at the alias of that configuration:

   | Configuration | Plan | Variants | Issuer (`Cts:Transmitter:Issuer`) |
   |---|---|---|---|
   | `receiver-ssf1.0-push.json` | `openid-ssf-receiver-test-plan` | `ssf_delivery_mode=push` | `…/test/a/easyssf-receiver-ssf1_0-push` (the default) |
   | `receiver-ssf1.0-poll.json` | `openid-ssf-receiver-test-plan` | `ssf_delivery_mode=poll` | `…/test/a/easyssf-receiver-ssf1_0-poll` |
   | `receiver-ssf1.0-caepiop1.0-push.json` | `openid-ssf-receiver-caep-test-plan` | `ssf_delivery_mode=push` | `…/test/a/easyssf-receiver-ssf1_0-caepiop1_0-push` |
   | `receiver-ssf1.0-caepiop1.0-poll.json` | `openid-ssf-receiver-caep-test-plan` | `ssf_delivery_mode=poll` | `…/test/a/easyssf-receiver-ssf1_0-caepiop1_0-poll` |

   All of them use `ssf_auth_mode=dynamic` and `client_auth_type=client_secret_basic`, the defaults
   of the application. The alias of a configuration (the suite allows no dots in aliases) makes the
   transmitter issuer `https://localhost.emobix.co.uk:8443/test/a/<alias>`. Instead of configuring
   the issuer, a run can also name it, see [Running a test](#running-a-test). The variants in detail:

   | Variant | Value | `appsettings.json` |
   |---|---|---|
   | SSF Delivery Mode | `push` or `poll` | `Cts:Delivery:Method` |
   | Authentication Variant | `dynamic` (client credentials) or `static` | `Cts:Auth:Mode`, `Cts:Auth:AccessToken` |
   | Client Authentication Type | `client_secret_basic` or `client_secret_post` | `Cts:Auth:ClientAuthenticationMethod` (`Basic`, `Post`) |
   | SSF Profile | `caep_interop` or `default` | see scenarios |

   Every setting is an environment variable as well, with double underscores:
   `Cts__Delivery__Method=Poll`.

### Running a test

```sh
dotnet run --project tests/EasySsf.Conformance.Receiver
# or: Cts__Delivery__Method=Poll dotnet run --project tests/EasySsf.Conformance.Receiver
```

1. Start the test module in the suite; it waits for the receiver.
2. Start the run the module expects: `curl -sk -X POST https://localhost:9443/cts/runs/auto`. The
   application asks the suite (`/api/runner/running`, `/api/info`) which module waits for the
   receiver, and starts the scenario of that module (see [Scenarios](#scenarios)) against the
   module's test instance with the delivery method of its variant. Modules running under the
   configured alias come first; without one, any waiting module of the suite is taken. The
   response names the module and the run; with no waiting module it is a `404`, with several a
   `409` that lists them (`?issuer=https://localhost.emobix.co.uk:8443/test/a/<alias>` picks one).
   `curl -sk https://localhost:9443/cts/suite/modules | jq` shows what runs in the suite and the
   scenario each module would get.

   To choose the scenario yourself:
   `curl -sk -X POST 'https://localhost:9443/cts/runs?scenario=caep-interop'`; with
   `&issuer=https://localhost.emobix.co.uk:8443/test/a/<alias>&delivery=poll` the run targets another
   test instance or delivery method than configured.
3. Watch it: `curl -sk https://localhost:9443/cts/runs/current | jq`. The run logs every step and
   every SET, and ends by deleting the stream once no SET arrived for `Cts:Run:IdleTimeout`
   (30 seconds, long enough for the pauses the tests make).

Starting a run stops the previous one. `POST /cts/runs/current/stop` ends a run early.

### Running a plan module by module

[`run-plan.sh`](run-plan.sh) does steps 1 to 3 for every module of a plan, waiting for a key press
before each (Enter runs the module, `s` skips it, `q` quits, `a` continues automatically from here
on with a pause of a few seconds between the modules, during which a key press still counts): it
starts the module in the suite, calls `POST /cts/runs/auto` on the receiver, waits for the module
to end and prints its result with the failures and warnings the suite logged, and a summary at the
end. It needs `curl` and `jq`.

```sh
./run-plan.sh                                   # the plan of the module running in the suite
./run-plan.sh sqqbZNqAZRQbv                     # a plan id, or its plan-detail URL
./run-plan.sh -c ../../src/EasySsf.Testing.Conformance/suite-config/receiver-ssf1.0-caepiop1.0-push.json   # a new plan from a configuration
./run-plan.sh -a 10 <plan>                      # continue automatically after 10 seconds, unless a key is pressed
./run-plan.sh -m verification -y <plan>         # only matching modules, without any pause
```

The suite defaults to `https://localhost.emobix.co.uk:8443` and the receiver to
`https://localhost:9443` (`-s`, `-r`, or `CTS_SUITE_URL`, `CTS_RECEIVER_URL`).

### Scenarios

| Scenario | What the receiver does | Test modules |
|---|---|---|
| `caep-interop` (default) | create stream, check its issuer, read configuration and status, verify (rejecting a wrong state or stream and requesting again), accept events, delete | all modules of the CAEP interop plan; in the default plan everything except the three below |
| `create-delete` | create and delete only | `stream-create-delete` (faster than `caep-interop`, which also passes) |
| `stream-management` | as `caep-interop`, plus update and replace the stream | `happypath` (default plan) |
| `status-update` | as `caep-interop`, plus pause and enable the stream after the verification | `stream-status-update` (default plan) |
| `remove-subject` | as `caep-interop`, plus remove `Cts:Stream:SubjectToRemove` after the verification | `removed-subject-event` (default plan) |

The `stream-issuer-mismatch` module passes with any scenario: the receiver refuses the stream the
suite created with a foreign `iss`, deletes it and ends the run as `REFUSED_STREAM`. Which module
gets which scenario is defined in `ConformanceScenarios.ForModule`, used by `POST /cts/runs/auto` and
by the automated tests alike.

## Endpoints

| | |
|---|---|
| `POST /cts/runs/auto?issuer=…` | starts the run the test module waiting in the suite expects; `issuer` names a test instance of the suite, default `Cts:Transmitter:Issuer` |
| `GET /cts/suite/modules?issuer=…` | the test modules running in the suite of that test instance, with the scenario the receiver plays for each |
| `POST /cts/runs?scenario=…&issuer=…&delivery=…` | starts a run; `issuer` and `delivery` default to `Cts:Transmitter:Issuer` and `Cts:Delivery:Method` |
| `GET /cts/runs/current` | the current run: status, stream, log, received SETs |
| `POST /cts/runs/current/stop` | stops the current run and deletes its stream |
| `POST /ssf/push` | the push endpoint the suite delivers to; each run expects its own `Authorization` header, registered with the stream |

## Settings

| Setting | Default | |
|---|---|---|
| `Cts:Server:Url` | `https://localhost:9443` | Where the application listens. |
| `Cts:Server:CertificateFile` / `KeyFile` | `certs/localhost.pem` / `certs/localhost-key.pem` | TLS certificate and key for an `https` URL. |
| `Cts:Transmitter:Issuer` | `https://localhost.emobix.co.uk:8443/test/a/easyssf-receiver-ssf1_0-push` | The test instance of the suite. |
| `Cts:Transmitter:CertificateFile` | `certs/conformance-suite.pem` | The certificate of a suite with a self-signed certificate, trusted for any host name. |
| `Cts:Transmitter:TrustAllCertificates` | `false` | Trust any certificate instead; only for a local suite. |
| `Cts:Auth:Mode` | `Dynamic` | `Dynamic` (client credentials) or `Static` (`Cts:Auth:AccessToken`). |
| `Cts:Auth:ClientId` / `ClientSecret` | `easyssf-receiver` / `easyssf-receiver-secret` | As configured in the plan. |
| `Cts:Auth:Scopes` | `ssf.read`, `ssf.manage` | |
| `Cts:Auth:ClientAuthenticationMethod` | `Basic` | `Basic` or `Post`. |
| `Cts:Delivery:Method` | `Push` | `Push` or `Poll`. |
| `Cts:Delivery:PushUrl` | `https://localhost:9443/ssf/push` | How the suite reaches the push endpoint. |
| `Cts:Delivery:PollInterval` | `00:00:01` | |
| `Cts:Stream:EventsRequested` | `CaepSessionRevoked`, `CaepCredentialChange`, `CaepDeviceComplianceChange` | |
| `Cts:Stream:SubjectToRemove` | `format: email`, `email: foo@example.com` | The subject the `remove-subject` scenario removes. |
| `Cts:Run:VerificationTimeout` | `00:00:30` | How long to wait for the verification event. |
| `Cts:Run:IdleTimeout` | `00:00:30` | The stream is deleted once no SET arrived for this long. |
| `Cts:Run:MaxDuration` | `00:05:00` | A run ends at the latest after this time. |
