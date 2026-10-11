# Security policy

easyssf-dotnet sits in the authentication path of the applications that use it: it decides
whether a security event is genuine and, in the ASP.NET Core integrations, whether an access token
or a session is still valid. Reports about anything that weakens that are welcome.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability.

- Write to **oss@easyssf.org**. If you need to send something confidential, ask for a key first and
  you will get one by return mail.
- Or use [GitHub's private vulnerability reporting](https://github.com/easyssf/easyssf-dotnet/security/advisories/new)
  on the repository, once it is enabled there.

Please include the version or commit, the project (for example `EasySsf.Receiver` or the ASP.NET
Core integration), what an attacker can achieve, and the steps or a test that reproduces it. A
signed SET that is wrongly accepted, or a push request that is wrongly acknowledged, is the most
useful form.

You will get an acknowledgement, usually within a few days, and we will keep you informed while the
report is being handled. We ask for the time to prepare a fix and a release before details are
published, and we credit reporters in the advisory unless they prefer not to be named.

## Verifying releases

No release has been published yet. Releases will be NuGet packages on nuget.org, built by the
release workflow of this repository; this section will then say how a package is tied to the
commit it was built from and how to check it. Until then, build from source.

## Supported versions

easyssf-dotnet is pre-1.0 and its API may still change. Until 1.0, only the latest release
receives fixes; earlier releases are not patched.

## What is in scope

- Verification of Security Event Tokens: signatures, `typ`, `iss`, `aud`, `jti`, `iat`, key
  retrieval and the accepted algorithms.
- Authentication of the push endpoint and the handling of malformed requests.
- De-duplication, token revocation and session termination logic, including the database-backed
  stores.
- Calls to the transmitter: token handling, metadata discovery, stream management.

Vulnerabilities in dependencies such as `Microsoft.IdentityModel.JsonWebTokens` or ASP.NET Core
should be reported to those projects; a report here is still useful if easyssf-dotnet uses them in
a way that makes the problem worse.
