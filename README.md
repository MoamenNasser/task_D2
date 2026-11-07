# Loyalty Points API

A small ASP.NET Core service that awards loyalty points to wallets. Partner retries are handled safely, including concurrent ones.
All state is in memory, in one process.

See [HANDOFF_NOTES.md](HANDOFF_NOTES.md) for key decisions, the checks that were run, the limitations and the time spent.

## Runtime

| | Version used |
| --- | --- |
| OS | Ubuntu 22.04 (Linux x86_64) |
| .NET SDK | 8.0.133 (`dotnet --info`) |
| Runtime | Microsoft.NETCore.App 8.0.33, Microsoft.AspNetCore.App 8.0.33 |
| Target | `net8.0` |
| Test packages | xUnit 2.9.2, xunit.runner.visualstudio 2.8.2, Microsoft.AspNetCore.Mvc.Testing 8.0.11, Microsoft.NET.Test.Sdk 17.11.1 |
| Docs tool | DocFX 2.81.0, a local dotnet tool (`.config/dotnet-tools.json`) |

Any .NET 8 SDK should work. On Ubuntu 22.04: `sudo apt install dotnet-sdk-8.0`.
The first build restores the NuGet packages, which needs network access. The API itself has no package dependencies.

## Commands

Run from the repository root.

```bash
# build (warnings are errors)
dotnet build

# start the service on http://127.0.0.1:8080 (set in appsettings.json)
dotnet run --project src/LoyaltyPoints.Api

# ...or on another port, if 8080 is taken
dotnet run --project src/LoyaltyPoints.Api -- --urls http://127.0.0.1:18080

# run the automated tests (unit, HTTP end-to-end, concurrency)
dotnet test

# run the curl smoke test against a freshly started service
scripts/smoke.sh http://127.0.0.1:8080

# generate the HTML docs (API reference + walkthrough) into docs/_site/, then view them
dotnet tool restore                                   # installs DocFX 2.81.0 from .config/dotnet-tools.json
dotnet docfx docs/docfx.json
dotnet docfx serve docs/_site --port 18090            # open http://localhost:18090
```

**Reset to the initial state:** stop the process (Ctrl+C) and start it again. State is in memory only.
On start there are two wallets, `W100` with 100 points and `W200` with 50, and no awards.
The tests do not need a running service. Each HTTP test hosts its own fresh copy of the app in memory.

## API

### `POST /awards`

```json
{ "sourceId": "POS_A", "externalTransactionId": "T1", "walletId": "W100", "points": 25 }
```

- **Identifiers:** `sourceId`, `externalTransactionId` and `walletId` are required strings of 1-64 ASCII letters, digits, `_` or `-`.
  They are case-sensitive, and so are the field names.
- **Points:** `points` must be a JSON integer from 1 to 1000. `25.0`, `1e2`, `"25"` and `true` are rejected.
- **Unknown fields** are ignored.
- **Duplicate keys** (for example `points` twice) are rejected with 400, because JSON parsers disagree on which value wins.
- **Invalid UTF-8** in the body is rejected with 400.
- **Content-Type** is not checked. The body is validated strictly either way.
- **Identity:** an award is identified by `(sourceId, externalTransactionId)`.

| Status | When | Body |
| --- | --- | --- |
| 201 | New award accepted, balance credited once | award record |
| 200 | Same identity, same `walletId` and `points` (a retry) | the original award record, same `awardId` |
| 400 | Malformed JSON or invalid UTF-8, non-object body, duplicate key, or any invalid field | error, with per-field `details` for field errors |
| 404 | New identity, wallet does not exist | error |
| 409 | Same identity, but a different `walletId` or `points` | error |
| 413 | Body larger than 64 KiB | error |

Award record:

```json
{ "awardId": "AW-1", "sourceId": "POS_A", "externalTransactionId": "T1", "walletId": "W100", "points": 25 }
```

A wrong method on a known path (for example `PUT /awards`) returns **405** with an `Allow` header.
An unknown route returns **404**. An unhandled exception returns **500**.

Error body:

```json
{ "error": "invalid_request", "message": "One or more fields are invalid.",
  "details": [ { "field": "points", "message": "must be an integer" } ] }
```

Every error the application produces has this shape. `details` only appears for field errors.
One exception: a request that is not valid HTTP (for example a broken request line) is rejected by Kestrel
before it reaches the app, with an empty 400.

**Error precedence**, checked in this order:

1. **400.** The body is checked for UTF-8, JSON syntax, object shape, duplicate keys, field types, formats and ranges
   before any state is read. An invalid request never returns 200, 404 or 409.
2. **200 / 409.** If the identity was already accepted, the request is compared with the stored award.
   A different wallet or different points gives 409, even when the new `walletId` does not exist.
   The identity is the more specific signal: the partner is reusing a transaction id.
3. **404.** A new identity with an unknown wallet stores nothing. The identity is not consumed, so a corrected request can reuse it.
4. **201.** Otherwise, the award is accepted.

Rejected requests (400/404/409/413) never change a balance or the stored awards.

### `GET /wallets/{walletId}`

- `200 {"walletId": "W100", "balance": 125}` for a known wallet.
- `404` with an error body otherwise. A malformed id is also a 404, because no wallet can have that id.

## Project layout

```text
LoyaltyPoints.sln
src/LoyaltyPoints.Api/
  Program.cs                          host setup: JSON options, singleton WalletService
  Validation/AwardRequestValidator.cs pure parsing and validation, no state
  Wallets/WalletService.cs            wallets, awards and the one lock
  Http/ApiEndpoints.cs                routes, status codes, JSON error bodies
tests/LoyaltyPoints.Tests/            xUnit tests
scripts/smoke.sh                      curl checks against a running service
docs/WALKTHROUGH.md                   how the code works and how the tests prove it
docs/docfx.json, index.md, toc.yml    DocFX config and site pages for the generated HTML docs
.config/dotnet-tools.json             pins the DocFX version for `dotnet tool restore`
```

## Design notes

- The body is parsed by hand with `JsonDocument`, not with model binding. The ASP.NET web defaults would read
  `"points": "25"` as a number and match field names case-insensitively, which the brief does not allow.
- `WalletService` holds both dictionaries and one `lock`. The lookup -> compare -> store -> credit sequence runs entirely
  under that lock. That makes concurrent retries of the same award produce exactly one 201, and concurrent distinct awards
  all get applied. A single lock is enough for one in-memory process.
- The award is stored before the wallet is credited. `Dictionary.Add` can throw and the addition cannot,
  so a failure never leaves a credited balance without an award.
- `WalletService` is registered as a singleton, because it is the state. Restarting the process resets it.
- Every type, method and field in `src/` carries an XML doc comment. For public members this is enforced: `GenerateDocumentationFile`
  is on and warnings are errors, so a missing comment fails the build. DocFX turns the comments into the HTML reference.

## Tests

100 test cases (40 test methods) in `tests/LoyaltyPoints.Tests`, run with `dotnet test`:

- `AwardRequestValidatorTests`: field rules, type confusion, boundaries, malformed and non-object bodies, invalid UTF-8, duplicate keys.
- `WalletServiceTests`: award semantics, and 64 threads behind a `Barrier` calling the service directly.
- `ApiTests`: the real app pipeline over HTTP (`WebApplicationFactory`). Each status code (including 405 with `Allow` and 413) and error body.
- `ConcurrencyTests`: concurrent HTTP requests released together:
  - 32 identical requests -> exactly one 201 and 31 200s with one `awardId`;
  - 32 distinct awards -> all reflected in the balance;
  - 25 rounds of the same identity with different points -> exactly one 201 and one 409 in each round.
- `scripts/smoke.sh`: 28 checks of the same scenarios via curl against a running service, including a burst of 20 parallel identical requests.
