# Code Walkthrough

How the service is built, why it is built that way, and how each requirement is proven.
Read it top to bottom once. After that, the sections work as a reference.

## 1. The big picture

The code is three layers. The HTTP layer uses the other two. The validator and the service know nothing about HTTP,
and they share only the `AwardRequest` record (the validator produces it, the service consumes it).

```text
                 HTTP request
                      |
                      v
 +-------------------------------------------+
 | Http/ApiEndpoints.cs  (HTTP layer)        |  routes, status codes, JSON in/out
 |   PostAwardAsync / GetWallet              |
 +-------------------------------------------+
        | raw body bytes           | AwardRequest
        v                          v
 +------------------------+  +--------------------------+
 | AwardRequestValidator  |  | WalletService            |
 | (pure, static)         |  | (state + one lock)       |
 | bytes -> AwardRequest  |  | AwardPoints(), GetWallet |
 +------------------------+  +--------------------------+

 Program.cs: JSON options, register WalletService as a singleton with W100/W200, map the endpoints, run Kestrel
```

| File | Responsibility | Knows about HTTP? | Has state? |
| --- | --- | :---: | :---: |
| `Validation/AwardRequestValidator.cs` | Turn a raw body into an `AwardRequest`, or explain what is wrong | no | no |
| `Wallets/WalletService.cs` | Business rules: idempotency, conflicts, balances | no | yes |
| `Http/ApiEndpoints.cs` | Map HTTP to the two layers above, and their results back to HTTP | yes | no |
| `Program.cs` | Wire it together and start the host | yes | no |

Why split it like this:

- Each piece can be tested on its own. Validator tests don't need a server, and service tests don't need JSON.
- A requirement change usually touches one file. A new field rule goes in the validator. A new business rule goes in
  the service. A new status code goes in the HTTP layer.

## 2. The life of a `POST /awards` request

1. **Kestrel accepts the connection** and runs the request on a thread-pool thread, so many requests can be in flight at once.
2. **Routing** matches `POST /awards` to `PostAwardAsync`. ASP.NET injects `HttpRequest` and the singleton `WalletService`.
3. **`ReadBodyAsync`** reads the body into a byte array. If `Content-Length` says more than 64 KiB, or the bytes read pass
   64 KiB, it stops and the handler returns **413**.
4. **`AwardRequestValidator.Parse`** works through these checks:
   1. `Utf8.IsValid(body)`. If the bytes aren't valid UTF-8, the answer is _"Request body is not valid JSON."_ (see section 5).
   2. `JsonDocument.Parse(body)`. A `JsonException` gives the same answer.
   3. If the root isn't an object, the answer is _"Request body must be a JSON object."_
   4. `FindDuplicateKey` walks the whole document. Each object gets its own `HashSet<string>` of seen names.
      A repeated name in the same object rejects the body.
   5. `ReadIdentifier` runs three times, once each for sourceId, externalTransactionId and walletId. It checks that the
      field is present, is a string, and matches the format.
   6. `ReadPoints` checks that the field is present, is a JSON integer, and is in the range 1..1000.
   7. All field errors are collected, so the client sees every problem at once. If there are any, the handler returns
      **400** with a `details` array.
5. **`WalletService.AwardPoints`** runs entirely inside `lock (_gate)`:
   1. It builds the key `(SourceId, ExternalTransactionId)`.
   2. **If the key exists** and the wallet and points are the same, the result is `Replayed` (**200**, with the original
      record). If either differs, the result is `Conflict` (**409**).
   3. **If the key is new** and the wallet doesn't exist, the result is `WalletNotFound` (**404**). Nothing is stored, so
      the key stays free.
   4. Otherwise it creates `AW-<n>`, stores the award, then adds the points, and returns `Created` (**201**).
      Storing comes first because `Dictionary.Add` can throw and the addition cannot, so a failure never leaves
      a credited balance without an award.
6. **The `switch` on `outcome.Status`** in `PostAwardAsync` turns the status into an HTTP code and a JSON body with `Results.Json`.

`GET /wallets/{walletId}` is simpler. `GetWallet` returns 404 if the id is malformed or unknown, and otherwise
returns `200 {"walletId", "balance"}`. `WalletService.GetWallet` takes the same lock, so a read never sees a half-applied award.

## 3. Concurrency: why one lock is enough, and why it's needed

Here is what goes wrong without the lock. Two identical retries, A and B, arrive at the same moment:

```text
thread A: _awards.TryGetValue(key) -> not found
thread B: _awards.TryGetValue(key) -> not found   <- both think the award is new
thread A: store award AW-1, balance += 25       -> 201
thread B: store award AW-2, balance += 25       -> 201   WRONG: credited twice
```

The fix is to make "check then act" a single step. Holding `_gate` across the whole of `AwardPoints` means B can only
start its lookup after A has stored the award. B then sees it and returns `Replayed`.

The other two concurrency requirements follow from the same lock:

- **Distinct awards to one wallet:** `_balances[id] = balance + points` never overlaps with another write, so no update is lost.
- **Same identity with different points:** whichever request takes the lock first is `Created`. The other one finds the key
  and gets `Conflict`.

This was checked, not assumed. With the `lock` line commented out:

- The HTTP concurrency tests failed in 10 out of 10 runs. The failures were lost updates (W200 expected 274, got 162),
  `[Created, Created]` instead of `[Created, Conflict]`, and distinct awards that did not get 201.
- The service-level thread tests crashed the test host. `Dictionary.Add` threw "same key already added" and
  "concurrent update ... corrupted its state".

With the lock restored, everything passed again. .NET has no ThreadSanitizer, so this check plus the 20-request burst in the
smoke script is the concurrency evidence.

Why not something smarter:

- **`ConcurrentDictionary.GetOrAdd`** looks tempting, but its value factory can run more than once under contention, and
  the balance credit would still be a separate step. The pair "store award + credit wallet" must be atomic, and
  `ConcurrentDictionary` only makes single operations atomic.
- **Per-wallet locks alone** would protect the balance but not the identity check. Two requests with the same identity
  can name _different_ wallets.
- **`SemaphoreSlim`** is for waiting asynchronously. Nothing inside the critical section awaits, so a plain `lock` is
  simpler and cheaper.
- **Trade-off:** all awards are serialized. That is noted under limitations in the handoff.

## 4. Error precedence, with reasons

| Order | Check | Result | Why it's in this position |
| :---: | --- | --- | --- |
| 1 | Body/fields invalid | 400 | The brief says to validate before looking at awards or wallets |
| 2 | Identity exists, same wallet and points | 200 | It's a retry, so return the original |
| 3 | Identity exists, anything different | 409 | Reusing a transaction id is the real problem, even if the new wallet is also unknown |
| 4 | Identity new, wallet unknown | 404 | Nothing is stored, so the partner can fix it and resend |
| 5 | Otherwise | 201 | Accepted |

## 5. Small details worth knowing

- **No model binding.** A minimal API handler could take an `AwardRequest` parameter and let ASP.NET bind it. The web JSON
  defaults would then accept `"points": "25"` (numbers from strings) and `"SourceId"` (case-insensitive names). The brief
  asks for type validation, so the body is read as bytes and parsed with `JsonDocument`. `TryGetProperty` is an ordinal,
  case-sensitive match.
- **Strict integers.** `JsonDocument` reports `25`, `25.0` and `1e2` all as `JsonValueKind.Number`. `ReadPoints` also
  rejects raw text containing `.`, `e` or `E`. Then `TryGetInt64` and the range check run, so a huge integer like
  `99999999999999999999` fails `TryGetInt64` and is reported as out of range. `-0` parses as 0 and is out of range too.
- **Invalid UTF-8.** `JsonDocument.Parse` does not reject invalid UTF-8 bytes inside a string. The error only appears when
  the value is read: `GetString()` throws `InvalidOperationException`, which became a 500. The first test run caught this
  (`RejectsInvalidUtf8`). `Utf8.IsValid` now checks the whole body before parsing. JSON must be UTF-8 (RFC 8259),
  so the message stays "not valid JSON".
- **Duplicate keys are rejected.** `JsonDocument` keeps every repeated key, and `TryGetProperty` returns one of them.
  Other parsers keep the first or the last. For `{"points":5,"points":500}`, a proxy and this service could disagree
  about the amount, so the body is rejected. The same name in a nested object is fine.
- **ASCII-only identifiers.** `IsIdChar` compares character ranges directly. It doesn't use `char.IsLetterOrDigit` or
  regex `\w`, which accept Unicode letters and digits such as `é` or `٣`.
- **Award key is a tuple, not a string.** If the key were the concatenation `sourceId + externalTransactionId`, then
  `("A_", "B")` and `("A", "_B")` would both become `A_B`. `ValueTuple` equality compares strings ordinally.
- **404 and 405 bodies.** Routing answers an unknown path with an empty 404, and a known path with the wrong method with an
  empty 405 plus an `Allow` header. `UseStatusCodePages` fills in a JSON body for those. It only acts when the response
  has no body yet, so our own 404/409 messages are left alone.
- **500 body.** `UseExceptionHandler` writes a JSON 500 in every environment. The UTF-8 bug above showed it working:
  the failing API test got a JSON 500 before the fix.
- **413 by hand.** Kestrel has its own body size limit, but the in-memory test server ignores it. `ReadBodyAsync` caps
  the read at 64 KiB, so the limit behaves the same in tests and in production.
- **`details` only for field errors.** `Program.cs` sets `JsonIgnoreCondition.WhenWritingNull`, so a null `Details`
  is left out of the body.
- **Singleton service.** `WalletService` is the state, so there must be exactly one per process. A scoped or transient
  registration would give each request fresh wallets.
- **Malformed HTTP.** A request Kestrel can't parse (for example a broken request line) never reaches the app. Kestrel
  answers with an empty 400 itself. So "every error is JSON" is true for application errors, not for broken HTTP.
- **`public partial class Program`.** Top-level statements generate an internal `Program` class. Declaring it public
  lets the test project use `WebApplicationFactory<Program>`.

## 6. How the tests prove the requirements

| Test class | What it covers | How |
| --- | --- | --- |
| `AwardRequestValidatorTests` | Every field rule, boundary values (0, 1, 1000, 1001, 64/65 chars), type confusion, malformed and non-object bodies, invalid UTF-8, duplicate keys | Calls `AwardRequestValidator.Parse` directly |
| `WalletServiceTests` | Created / Replayed / Conflict / WalletNotFound, and that the identity is reusable after a 404 | Calls `WalletService` directly, plus 64 threads behind a `Barrier` |
| `ApiTests` | Every status code (including 405 + `Allow` and 413) and JSON error body, and the precedence rules | `WebApplicationFactory<Program>`: the real pipeline in memory |
| `ConcurrencyTests` | The three concurrency scenarios over HTTP | `WebApplicationFactory`, requests released together |
| `scripts/smoke.sh` | The same flows against a running Kestrel server (28 checks), including 20 parallel curls | Bash + curl; exits non-zero on the first wrong answer |

That is 40 test methods and 100 test cases (theory rows count separately).

How a concurrency test works (`ConcurrentIdenticalRequestsProduceOne201`):

1. `RunTogetherAsync` starts 32 tasks with `Task.Run`. Each one first awaits the same `TaskCompletionSource`.
2. The test calls `SetResult()`, which releases them all at once.
3. They all POST the same award.
4. The test asserts **exactly** one 201, 31 200s, a single distinct `awardId`, and a balance of 125.

A test that sends the requests one after another would pass even without the lock. The lock-removal check in section 3
shows these tests really overlap: without the lock they fail.

xUnit creates a new instance of the test class for every test, and each instance creates its own `WebApplicationFactory`.
So every HTTP test gets a fresh app and fresh wallets, and no test depends on another test's state.

### Requirement -> test map

| # | Requirement | Covered by |
| --- | --- | --- |
| V1 | ids required, string, 1-64 of `[A-Za-z0-9_-]`, case-sensitive | Validator: RejectsMissingFields, IdentifierBoundaries, RejectsBadIdentifierValues, IdentifiersAreCaseSensitiveAndKeptAsIs, FieldNamesAreCaseSensitive |
| V2 | points integer 1..1000 (reject float, string, bool, 0, 1001) | Validator: PointsRangeAcceptsBoundaries, PointsRangeRejectsOutOfRange, PointsMustBeAJsonInteger |
| V3 | malformed JSON -> 400 JSON error | Validator: RejectsMalformedJson, RejectsInvalidUtf8; Api: InvalidBodiesReturn400WithJsonError, InvalidUtf8BodyReturns400Not500 |
| V4 | non-object body -> 400 JSON error | Validator: RejectsNonObjectBodies; Api: InvalidBodiesReturn400WithJsonError |
| V5 | validation happens before award/wallet checks | Api: ValidationRunsBeforeIdentityAndWalletChecks |
| A1 | same txn id, different source = different award | Service: SameTransactionIdFromOtherSourceIsSeparateAward; Api: SameTransactionIdDifferentSourceIsNewAward |
| A2 | new award -> 201, full record, balance +once | Api: NewAwardReturns201AndCreditsWallet; Service: NewAwardCreditsOnce |
| A3 | matching retry -> 200, same record + awardId, balance unchanged | Api: MatchingRetryReturns200WithOriginalRecord; Service: MatchingRetryReturnsOriginalWithoutCrediting |
| A4 | conflicting retry (wallet or points) -> 409, nothing changes | Api: ConflictingRetryReturns409AndChangesNothing; Service: ConflictingRetryChangesNothing |
| A5 | unknown wallet -> 404, identity NOT consumed, corrected retry 201 | Api: UnknownWalletReturns404AndIdentityStaysUsable; Service: UnknownWalletDoesNotConsumeIdentity |
| C1 | N identical concurrent -> exactly one 201, N-1 x 200, same awardId | Concurrency: ConcurrentIdenticalRequestsProduceOne201; Service: ConcurrencyIdenticalRequestsProduceOneAward |
| C2 | N distinct concurrent to same wallet -> balance = sum | Concurrency: ConcurrentDistinctAwardsAllReflectedInBalance; Service: ConcurrencyDistinctAwardsAllApplied |
| C3 | same identity, different points, concurrent -> one 201 + one 409 | Concurrency: ConcurrentSameIdentityDifferentPointsGivesOneAcceptAndOneConflict |
| G1 | GET wallet -> 200 {walletId, balance}; unknown -> 404 | Api: InitialState, UnknownWalletLookupIs404 |
| G2 | GET reflects completed POSTs | Api: NewAward..., MatchingRetry..., ConflictingRetry... (balance read after POST) |
| S1 | initial state W100=100, W200=50, no awards | Api: InitialState; Service: StartsWithInitialWallets |
| E1 | duplicate JSON key -> 400 (beyond the brief) | Validator: RejectsDuplicateKeys; Api: DuplicateKeysReturn400 |
| E2 | wrong method on a known path -> 405 + `Allow` (beyond the brief) | Api: WrongMethodReturns405WithAllowHeader |
| E3 | malformed or non-UTF-8 wallet id on GET -> 404, not 500 | Api: UnknownWalletLookupIs404 (`w100`, `bad.id`, `%FF`) |
| E4 | body over 64 KiB -> 413; unknown route -> JSON 404 (beyond the brief) | Api: OversizedBodyReturns413, UnknownRouteReturnsJson404 |
| E5 | valid body accepted; unknown fields ignored; every invalid field reported at once | Validator: AcceptsValidRequest, IgnoresUnknownFields, ReportsEveryInvalidField |

## 7. How the project was built, step by step

The code was written and verified first, then committed in layers (scaffold, validation, service, HTTP, tests, smoke, docs).
So the commits show the final code of each layer, and the UTF-8 fix below is already part of the validation commit.
The work went in this order:

1. **Scaffold.** Solution, two projects, `.gitignore`. The API project turns on nullable reference types,
   `TreatWarningsAsErrors` and `GenerateDocumentationFile`. `Mvc.Testing` is pinned to 8.0.11, because newer majors
   target newer frameworks than `net8.0`.
2. **Validation**, then the **wallet service**, then the **HTTP layer** and `Program.cs`, then the **tests**.
   - **First build:** 0 warnings.
   - **First test run:** 98 of 99 passed. `RejectsInvalidUtf8` failed with `InvalidOperationException` from `GetString()`.
     The `Utf8.IsValid` check was added, plus an HTTP test for it. With the check removed, that HTTP test gets a 500,
     so the test really catches the bug.
   - Result: 100 of 100.
3. **Concurrency checks.** The concurrency tests passed 20 runs in a row. Then the lock was removed and the tests
   failed (section 3). The lock was restored and everything passed again.
4. **Smoke script** against `dotnet run -c Release` on port 18080 (8080 was taken by another app on the dev machine).
   All checks passed. A check for invalid UTF-8 was added, which makes 28.

## 8. Reading the API reference

Every type, method and field carries an XML doc comment (`///`), private ones included. The build writes them to
`src/LoyaltyPoints.Api/bin/<Configuration>/net8.0/LoyaltyPoints.Api.xml`, and IDEs show them on hover.
Because warnings are errors, a public type or member without a comment fails the build (`CS1591`).

To generate the HTML reference (this walkthrough plus every type and member):

```bash
dotnet tool restore                          # DocFX 2.81.0, pinned in .config/dotnet-tools.json
dotnet docfx docs/docfx.json                 # writes docs/_site/ (git-ignored)
dotnet docfx serve docs/_site --port 18090   # open http://localhost:18090
```

DocFX reads the project through the C# compiler, so records, enums and file-scoped namespaces come out correctly.
Doxygen 1.9.1 was tried first: it skipped all the records and the enum, and misread an expression-bodied method.
`Program` does not appear in the reference. It is the top-level entry point and lives in the global namespace, which DocFX leaves out.
The first `dotnet tool restore` downloads about 100 MB.
