# Reliability

`Andy.Data` is built so that a model (or any caller) can depend on its behavior: one response shape
for every outcome, a stable set of error codes, deterministic results, schema-aware execution,
durable Delta writes, and bounded resource use with cooperative cancellation.

See also: [tool-contract.md](tool-contract.md), [operations.md](operations.md),
[architecture.md](architecture.md).

## Determinism

### One stable envelope

Every operation returns the same `DataFrameResponse` shape for both success and failure, so a caller
parses one structure across all 28 operations:

```csharp
using var engine = new DataFrameEngine();

DataFrameResponse r = engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales.csv",
    ["dataset_id"] = "sales",
});

if (r.Success)
{
    Console.WriteLine($"{r.DatasetId}: {r.RowCount} rows, {r.Schema.Count} columns");
    // r.PreviewRows is a bounded preview; r.PreviewTruncated is true when rows were withheld.
}
else
{
    Console.WriteLine($"{r.ErrorCode}: {r.Message}");   // branch on r.ErrorCode
}
```

`Execute` is synchronous and never throws across the boundary — the Guard pipeline maps every
exception to an envelope (see [architecture.md](architecture.md)).

### Explicit ordering, type, and null handling

- **Ordering.** Results are only ordered when an operation orders them (e.g. `dataframe_sort`).
  Operations do not promise an incidental row order; rely on an explicit sort when order matters.
- **Types.** Column types come from DuckDB's `DESCRIBE` of the relation and are surfaced verbatim in
  `ColumnSchema.Type`. CSV types are inferred (see `INVALID_TYPE` below); the `columns` hint map
  pins specific columns to chosen DuckDB types.
- **Nulls.** `ColumnSchema.Nullable` reflects DuckDB's nullability. SQL `NULL` round-trips as .NET
  `null` in preview rows and literals (`SqlText.Literal(null)` renders `NULL`).

### Round-trippable numeric serialization

Backend values are normalized to JSON-serializable, round-trippable forms:

- `double`/`float` render with the `"R"` (round-trip) format, culture-invariant, so no precision is
  silently lost and no locale changes the decimal separator.
- DuckDB `HUGEINT` comes back as a `BigInteger`; it is mapped to `long` when it fits, else to
  `decimal` when that fits, else to its exact decimal string — never to a lossy float.
- Profile quantile keys are formatted round-trippably (`q_0.25`, not a rounded form) so distinct
  quantiles never collide.

## Schema awareness

Identifiers are **schema-resolved before execution**. When an operation references a column,
`SqlText.ResolveColumn` matches the caller's name (case-insensitively) against the dataset's actual
schema and uses the canonical name; an unknown column throws `COLUMN_NOT_FOUND` with a `did_you_mean`
suggestion in `Details`, before any SQL runs. This means a typo fails fast with a precise error
rather than producing a surprising or empty result.

## The error contract

Failure envelopes carry a stable `ErrorCode` (from `DataFrameErrorCodes`), a human-readable
`Message`, and an optional `Details` dictionary. These codes are a contract and are safe for
programmatic branching. See [tool-contract.md](tool-contract.md) for the envelope contract.

| Error code | Fires when |
| --- | --- |
| `DATASET_NOT_FOUND` | A referenced `dataset_id` is not registered in the catalog/backend (wrong id, or a session that no longer holds it). |
| `COLUMN_NOT_FOUND` | A referenced column does not exist in the dataset's resolved schema. `Details` includes `did_you_mean`. |
| `INVALID_TYPE` | A parameter has the wrong JSON type, is out of declared numeric range, fails its regex pattern, a required parameter is missing, a value cannot be coerced to the expected CLR type, or a column type cannot be mapped (e.g. to a Delta type on export). |
| `INVALID_ARGUMENT` | A value is out of a closed `AllowedValues` set, an unknown operation id is dispatched, a malformed dataset id is used, or an argument combination is rejected (e.g. a single-character option longer than one char, `array` for a non-JSON export, `append` for a non-Delta export). |
| `INVALID_AGGREGATION` | An aggregation function name is not in the closed aggregation vocabulary. |
| `INVALID_PREDICATE` | A predicate or expression tree is malformed, or uses an operator/function/unit/format outside its closed vocabulary. |
| `SCHEMA_MISMATCH` | Schemas that must agree do not — e.g. a Delta append whose column names/types/nullability or partition columns differ from the existing table. |
| `FILE_NOT_FOUND` | A concrete (non-glob) input path does not exist, or a target is not a Delta table (no `_delta_log/`). |
| `PERMISSION_DENIED` | A registered `IPathPolicy` denied a read (loaders) or a write (export) for the canonicalized path. See [security.md](security.md). |
| `TARGET_EXISTS` | An export target already exists and `mode=error`, or a Delta commit version was claimed by a concurrent writer first. |
| `CANCELLED` | The caller's `CancellationToken` was cancelled, or `MaxExecutionTimeMs` was exceeded. |
| `BACKEND_ERROR` | Any other unexpected failure: a DuckDB engine error, the Delta extension being unavailable, or a Delta feature outside the supported envelope (checkpoints, partitioned time travel, deletion vectors, column mapping). |

## Delta write durability

`dataframe_export` with `format=delta` writes the Delta transaction log directly (the DuckDB delta
extension can only read), and does so durably.

### Atomic, all-or-nothing commits (put-if-absent)

Each JSON commit file is written with `FileMode.CreateNew`, which atomically **fails rather than
clobbers** if that version already exists, and the full payload is flushed to disk before the handle
closes. So a commit number either exists in full or does not exist at all — a crash mid-write cannot
publish a partial commit.

### Cross-process optimistic-concurrency retry

Appends serialize same-process writers with a per-table-root lock, then run an optimistic-concurrency
loop: each attempt re-reads the latest committed version under the lock, claims the next number, and
publishes with put-if-absent. If a writer in **another process** published that number first, the
write throws `TARGET_EXISTS`; the loop re-reads the new head and retries against the next number (up
to a bounded number of attempts, so a structurally stuck table surfaces an error instead of looping
forever). The data files are version-independent and already written, so only the commit-log entry
needs a fresh number — no data is rewritten.

### Staged-then-swapped table writes

A new/overwrite Delta table is built completely in a temporary sibling directory, then swapped into
place: the existing target is moved aside only after the new table is fully written, and is restored
if the swap fails. A failure mid-export (disk full, interruption) never destroys the original table
or leaves a half-written table at the target path. Export also validates up front
(`EnsureExportable`) that every column type maps to a Delta type, so an unmappable schema fails with
`INVALID_TYPE` before any I/O.

## Resource governance and cooperative cancellation

`DataFrameExecuteOptions` (passed per call) governs resource use:

```csharp
using var engine = new DataFrameEngine();
using var cts = new CancellationTokenSource();

var options = new DataFrameExecuteOptions
{
    MaxMemoryBytes = 512L * 1024 * 1024,   // DuckDB memory_limit; query spills to disk past this
    MaxExecutionTimeMs = 30_000,           // wall-clock budget; exceeding it yields CANCELLED
    CancellationToken = cts.Token,         // caller cancellation; cancelling yields CANCELLED
};

var r = engine.Execute("dataframe_group_by", parameters, options);
```

- **`MaxMemoryBytes`** sets DuckDB's `memory_limit` (in bytes). Because the backend also configures
  a temp spill directory, hitting the cap makes the query **spill to disk** rather than fail.
  `null` or non-positive means unset (the engine default applies).
- **`MaxExecutionTimeMs`** arms a linked token that cancels the operation after that many
  milliseconds of wall-clock time; exceeding it yields `CANCELLED`. `null` or non-positive means no
  limit.
- **`CancellationToken`** is the caller's token; cancelling it interrupts the in-flight DuckDB
  statement (via DuckDB's interrupt API) and yields `CANCELLED`.

Cancellation is **cooperative**: the token is checked at statement entry and the running statement is
interrupted; the interrupted statement surfaces as a `CANCELLED` envelope, never a partial success.
`DataFrameExecuteOptions.Default` applies no limits and no cancellation.
