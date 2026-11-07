# Handoff Notes

An ASP.NET Core (.NET 8) service that awards loyalty points safely under partner retries and concurrent requests.
Commands to build, start, test and reset are in [README.md](README.md). Code walkthrough: [docs/WALKTHROUGH.md](docs/WALKTHROUGH.md).

## Key decisions

| Area | Decision | Reason |
| --- | --- | --- |
| Stack | C# / .NET 8, ASP.NET Core minimal API, xUnit | The role's stack |
| Structure | Validation (pure), wallet service (state), HTTP (mapping) | Each layer is tested on its own |
| Parsing | `JsonDocument` by hand, not model binding | Web defaults accept `"25"` as a number and match names case-insensitively |
| Concurrency | One `lock` around lookup, compare, store and credit | The race is on the identity, not just the balance |
| Award identity | `(sourceId, externalTransactionId)` tuple as the dictionary key | No collisions, unlike string concatenation |

## Error precedence (POST /awards)

1. **400:** invalid UTF-8, malformed JSON, non-object body, duplicate key, or any invalid field. Runs before any state is read.
2. **200:** known identity, same wallet and points. Returns the original record.
3. **409:** known identity, different wallet or points, even if the new wallet is unknown.
4. **404:** new identity, unknown wallet. Nothing is stored, so the identity stays reusable.
5. **201:** accepted, balance credited once.

Wrong method gives 405 with `Allow`; a body over 64 KiB gives 413. App errors are JSON with a message.

## Verification performed

| Check | Result |
| --- | --- |
| `dotnet build` | 0 warnings, warnings treated as errors |
| `dotnet test` | 100/100 pass |
| Concurrency tests repeated 20× | 20/20 green |
| Lock removed from `AwardPoints` | HTTP concurrency tests failed 10/10 runs (lost updates, two 201s for one identity); lock restored, all green |
| `scripts/smoke.sh` against Kestrel | 28/28 pass, including a 20-request burst (one 201, nineteen 200) |
| Fresh `git clone`, tests and smoke | 100/100, 28/28 |

**Issue found and fixed:** `JsonDocument` accepts invalid UTF-8 inside strings and only fails when the value is read.
A body with byte `0xFF` in `sourceId` returned 500. The body is now checked with `Utf8.IsValid` first and gets 400. Tests added.

## Limitations

- State is in memory only. After a restart, a retried award is accepted again. Persistence would need the award
  insert and balance update in one transaction, with a unique index on the identity.
- One global lock serializes all awards: fine here, a bottleneck under heavy load.
- No authentication: callers aren't tied to a `sourceId`.
- Award ids are a per-process counter: unique, but predictable.
- `Content-Type` is not enforced.
- A request that isn't valid HTTP gets Kestrel's own empty 400, not a JSON body.

## Time spent

About 55 minutes, including implementation, testing and documentation.
AI assistance (Claude) was used for implementation and review; every check above was run and its output read.
