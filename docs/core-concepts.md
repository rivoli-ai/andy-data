# Core Concepts

`Andy.Data` is a small set of orthogonal primitives over an embedded DuckDB
engine. This page covers the model you work with: datasets and the catalog,
operations as composable primitives, the single response envelope, bounded
previews, the session lifecycle, concurrency, and resource governance. The whole
surface is reached through one object — `DataFrameEngine` — which you construct
with `new DataFrameEngine()` (no dependency injection).

## Datasets

A **dataset** is named tabular data living in the engine for the duration of a
session. Every dataset is identified by a caller-supplied `dataset_id` and is
registered in the engine's catalog. Datasets are not persisted — they exist only
inside the engine's DuckDB connection until you drop them or dispose the engine.

- A **load** operation (`dataframe_load_csv`, `dataframe_load_json`,
  `dataframe_load_parquet`, `dataframe_load_delta`) registers a new dataset under
  the `dataset_id` you supply.
- A **transform** operation reads `dataset_id` and registers its result either
  under the optional `into` id, or — if `into` is omitted — **back under the same
  `dataset_id`, replacing it**. This is how you chain steps without accumulating
  intermediate names.

```csharp
// Replace in place: "sales" now refers to the filtered result.
engine.Execute("dataframe_filter", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["predicate"] = new Dictionary<string, object?>
        { ["column"] = "amount", ["op"] = "gt", ["value"] = 0 },
});

// Branch out: produce a new dataset, leave "sales" untouched.
engine.Execute("dataframe_filter", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "positive_sales",
    ["predicate"] = new Dictionary<string, object?>
        { ["column"] = "amount", ["op"] = "gt", ["value"] = 0 },
});
```

## The dataset catalog

The catalog is the session-scoped registry that maps each `dataset_id` to its
schema and provenance. It is modeled by `IDatasetCatalog` (namespace
`Andy.Data`), with `InMemoryDatasetCatalog` as the default implementation. Each
entry is a `DatasetEntry(string DatasetId, IReadOnlyList<ColumnSchema> Schema,
string Source, long? RowCount)`.

```csharp
foreach (var entry in engine.Catalog.List())
    Console.WriteLine($"{entry.DatasetId} <- {entry.Source} ({entry.RowCount} rows)");

if (engine.Catalog.Contains("sales"))
{
    var schema = engine.Catalog.TryGetSchema("sales");
    // ...
}
```

One engine owns exactly **one catalog and one DuckDB connection**. The catalog
holds framework-independent metadata (id, schema, provenance, known row count);
the actual backend relation keyed by `dataset_id` lives in the backend layer. The
`dataframe_drop` operation (and disposing the engine) releases datasets.

## Operations as composable primitives

The engine exposes a fixed vocabulary of 28 operations — loaders, transforms,
inspectors, and an exporter. They are deliberately **orthogonal**: each does one
thing, and broad coverage comes from **composition**, not from large
multi-purpose operations. You filter, then group, then sort, then export, by
chaining `dataset_id` from one call to the next.

Every operation:

- is dispatched by a stable id (e.g. `dataframe_filter`, `dataframe_group_by`),
- takes an `IReadOnlyDictionary<string, object?>` of parameters,
- runs synchronously and returns a `DataFrameResponse`,
- never accepts model-supplied SQL or code — predicates, expressions, and
  aggregations are closed enumerated vocabularies; identifiers are schema-resolved
  and quoted; literals are escaped.

Inspect what is available:

```csharp
foreach (var meta in engine.Operations)
    Console.WriteLine($"{meta.Id}: {meta.Name}");
```

A few parameters recur across operations:

| Parameter | Meaning |
|-----------|---------|
| `dataset_id` | Required on most operations — the input dataset. |
| `into` | Optional output dataset id. If omitted, the result replaces `dataset_id`. |
| `explain` | Boolean on transforms — when `true`, the DuckDB query plan is returned in `stats.plan`. |

See [operations.md](operations.md) for the full catalog.

## The response envelope

Every operation returns one shape — `DataFrameResponse` — for both success and
failure, so a single parsing path handles all outcomes. On success it carries the
output `dataset_id`, the `schema`, `row_count`, a bounded `preview`, `warnings`,
and execution `stats`. On failure it carries `success = false`, a stable
`error_code`, a `message`, and optional `details`.

```csharp
if (response.Success) { /* schema, row_count, preview_rows, stats */ }
else                  { /* error_code, message, details */ }
```

The full field-by-field contract — including the snake_case JSON shape produced by
`ToEnvelope()` and every error code — is documented in
[tool-contract.md](tool-contract.md).

## Bounded previews vs. full export

`PreviewRows` on the response is a small, **bounded** sample of the result —
enough to verify a transform and friendly to a language model's context budget,
but never the whole dataset. `PreviewTruncated` is `true` whenever the result has
more rows than the preview holds.

To obtain the **complete** materialized result, write it to disk with
`dataframe_export` (CSV, Parquet, JSON, or Delta). Previews are for inspection;
export is for full output. This separation keeps responses small and predictable
regardless of dataset size.

## Lifecycle

A session follows a simple arc:

1. **Construct** the engine — `using var engine = new DataFrameEngine();`
2. **Load** one or more datasets from files.
3. **Transform** by chaining `dataset_id` (replace in place, or branch with
   `into`).
4. **Inspect** (`dataframe_schema`, `dataframe_preview`, `dataframe_profile`, …)
   or **export** the full result.
5. **Dispose** — the `using` block tears down the DuckDB connection and releases
   every dataset. (Drop individual datasets early with `dataframe_drop`.)

## Concurrency

The backend is thread-safe but serializing: **one backend instance is one DuckDB
connection used under a lock**. Calls are safe to make concurrently, but
inter-query work serializes — parallelism is intra-query (DuckDB's vectorized
engine parallelizes a single query internally). For inter-query parallelism, use
**one engine per concurrent stream** rather than sharing a single engine across
threads doing independent work.

## Resource governance and cancellation

Each call can be bounded with `DataFrameExecuteOptions` (namespace
`Andy.Data.Operations`), passed as the third argument to `Execute`:

```csharp
using var cts = new CancellationTokenSource();

var options = new DataFrameExecuteOptions
{
    MaxMemoryBytes = 512L * 1024 * 1024, // DuckDB memory_limit; null/<=0 = unset
    MaxExecutionTimeMs = 30_000,          // wall-clock cap; null/<=0 = no limit
    CancellationToken = cts.Token,
};

var response = engine.Execute("dataframe_group_by", parameters, options);
```

- `MaxMemoryBytes` sets DuckDB's `memory_limit` (in bytes); the engine spills to
  disk when it can. `null` or a non-positive value leaves it unset.
- `MaxExecutionTimeMs` cancels the operation after that many milliseconds; `null`
  or a non-positive value means no limit.
- `CancellationToken` is observed cooperatively through loaders and transforms; a
  cancelled operation returns the `CANCELLED` error code.

Use `DataFrameExecuteOptions.Default` (or omit the argument) for no limits and no
cancellation.

## Next steps

- [operations.md](operations.md) — the full operation catalog and parameters.
- [tool-contract.md](tool-contract.md) — the response-envelope and error-code
  contract.
- [architecture.md](architecture.md) — how the engine, backend, and renderers fit
  together.
