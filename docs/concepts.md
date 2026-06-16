# Concepts

This page explains the model you're working with: the engine, datasets and the catalog, the **lazy
view** execution model (the single most important thing to understand for performance), resource
governance, concurrency, and the optional path policy.

## The engine

[`DataFrameEngine`](../src/Andy.Data/Operations/DataFrameEngine.cs) is the facade. It owns:

- a **DuckDB backend** — one `DuckDbBackend` instance wraps exactly one DuckDB connection, used under
  a lock; and
- a **dataset catalog** — the `dataset_id` → schema/provenance registry.

It constructs every operation once and dispatches by id:

```csharp
using var engine = new DataFrameEngine();                 // in-memory DuckDB + in-memory catalog
var ops = engine.Operations;                              // metadata for all 28 operations
var response = engine.Execute("dataframe_schema", parms); // dispatch by id
```

Two constructors let a host supply its own backend, catalog, [path policy](#path-policy), and
`ILoggerFactory`:

```csharp
new DataFrameEngine(pathPolicy, loggerFactory);                 // engine owns a fresh backend
new DataFrameEngine(backend, catalog, pathPolicy, loggerFactory); // caller owns the backend
```

`DataFrameEngine` is `IDisposable`. When it created the backend it disposes it; when you supplied the
backend, you own its lifetime. Each operation is also usable directly without the facade — e.g.
`new FilterOperation(backend, catalog).Execute(parameters, options)` — which is what a tool-framework
adapter does.

## Datasets and the catalog

A **dataset** is a named, session-scoped table-or-view registered under a caller-supplied
`dataset_id`. Ids must match `^[A-Za-z_][A-Za-z0-9_]{0,127}$` (letter/underscore start, up to 128
chars).

- A **load** (`load_csv`/`load_json`/`load_parquet`/`load_delta`) registers a new dataset.
- A **transform** reads one or more datasets and registers its result under the `into` id. When
  `into` is omitted (most transforms allow this), the result **replaces** the input `dataset_id`
  in place. A few operations require `into` because they have more than one input or no obvious
  single input to replace: `join`, `union`, and `rename`.
- `list` enumerates the catalog; `drop` releases a dataset and frees its backend resources once no
  remaining dataset depends on them.

Each [`DatasetEntry`](../src/Andy.Data.Abstractions/IDatasetCatalog.cs) records the id, the ordered
column schema, a `Source` provenance string (the load path or the producing operation), and the row
count. The catalog interface (`IDatasetCatalog`) is part of `Andy.Data.Abstractions`; the default is
`InMemoryDatasetCatalog`.

## Lazy views (read this for performance)

This is the defining execution characteristic of the engine.

**Loads and most transforms create DuckDB *views*, not materialized tables.** `load_csv` registers a
view over `read_csv_auto(...)`; a `filter` over it registers a view over that view; and so on. A chain
of transforms folds into a **single DuckDB plan** with predicates and projections pushed down to the
underlying scan. Nothing is computed until a **terminal** forces it.

The terminals are the row count and the preview that every operation's response carries (and `export`,
and `preview`). So:

- **A transform is cheap to define and the whole chain executes lazily and fused** — this is good for
  multi-step pipelines: intermediate results are not materialized to memory.
- **But each terminal re-executes the chain from its source.** Because a CSV/JSON/Parquet load is a
  *view over the file*, re-reading a derived dataset re-scans the source file. Calling `preview`
  repeatedly on a CSV-backed dataset re-parses the CSV each time. (See the
  [benchmarks](benchmarks.md), where filtering a CSV-backed dataset costs roughly a CSV scan, while
  the same filter over a Parquet-backed dataset is far cheaper — Parquet is columnar and the scan is
  pushed down.)

Practical implications:

- **For repeated access or many downstream steps, prefer a columnar source.** Load once from Parquet,
  or `export` a CSV-derived dataset to Parquet and load that back, then build your pipeline on the
  Parquet-backed dataset.
- **To force materialization**, terminate the chain into a concrete artifact (`export`) or rely on the
  engine's automatic checkpointing: past an internal chain-depth threshold the engine materializes an
  intermediate step as a real table, bounding how much of the chain every later count/preview
  re-executes. Sources are files (immutable for the session), so the snapshot stays correct.
- `join`, `union`, `pivot`, `unpivot`, and `sample` always materialize their result as a table.

## The response envelope

Every operation returns one [`DataFrameResponse`](../src/Andy.Data.Abstractions/DataFrameResponse.cs)
shape for both success and failure, so a caller parses one structure across all 28 operations. On
success you get `DatasetId`, `Schema`, `RowCount`, a bounded `PreviewRows` (≤ 50) with a
`PreviewTruncated` flag, `Warnings`, and a `Stats` block (`ElapsedMs`, `BytesScanned`, `RowsProduced`,
and an optional query `Plan` when called with `explain = true`). On failure you get a stable
`ErrorCode`, a `Message`, and optional `Details`. The full contract is in
[tool-contract.md](tool-contract.md).

## Resource governance & cancellation

Per-call limits are passed via
[`DataFrameExecuteOptions`](../src/Andy.Data/Operations/OperationContracts.cs):

| Field | Effect |
|-------|--------|
| `MaxMemoryBytes` | Sets DuckDB's `memory_limit`; the engine spills to a temp directory past it. `null`/non-positive = engine default. |
| `MaxExecutionTimeMs` | Wall-clock cap. On expiry the operation is cancelled and returns the `CANCELLED` error code. `null`/non-positive = no limit. |
| `CancellationToken` | Caller cancellation, linked with the timeout; cooperative through loaders and transforms. |

```csharp
engine.Execute("dataframe_join", parms, new DataFrameExecuteOptions
{
    MaxMemoryBytes = 1L * 1024 * 1024 * 1024,
    MaxExecutionTimeMs = 60_000,
    CancellationToken = ct,
});
```

Because DuckDB spills to disk past the memory limit, an operation that exceeds `MaxMemoryBytes` slows
down (disk I/O) rather than failing outright — see the [limits](benchmarks.md#limits--guidance).

## Concurrency

One backend instance is **one DuckDB connection used under a lock**, so it is safe to call an engine
concurrently from multiple threads — calls serialize, while parallelism happens *inside* each query
(DuckDB uses multiple cores per query). For **inter-query parallelism**, give each concurrent stream
its **own** `DataFrameEngine` (its own backend/connection). Datasets are not shared across engines.

## Path policy

By default every filesystem path is permitted. A host can register an
[`IPathPolicy`](../src/Andy.Data.Abstractions/IPathPolicy.cs) to constrain reads and writes
(allow-lists, sandbox roots, block-lists, anything):

```csharp
public sealed class SandboxPolicy(string root) : IPathPolicy
{
    public bool CanRead(string path)  => path.StartsWith(root, StringComparison.Ordinal);
    public bool CanWrite(string path) => path.StartsWith(root, StringComparison.Ordinal);
}

using var engine = new DataFrameEngine(pathPolicy: new SandboxPolicy("/data/sandbox"));
```

The path is **canonicalized before the policy sees it** — `..` segments are resolved and symbolic
links are resolved over the existing prefix — so a traversal or symlink cannot bypass a prefix policy.
For globs, the concrete base before the first wildcard is resolved. A denied path returns the
`PERMISSION_DENIED` error code.

## No injection surface

Predicates, expressions, and aggregations use **closed, enumerated vocabularies**; identifiers are
schema-resolved and quoted; literals are escaped. Every SQL token the engine emits is a fixed renderer
template, a schema-resolved quoted identifier, or an escaped literal. There is no path from input to
executed SQL or code — model- or user-supplied strings are treated as data, never as query text. The
predicate and expression grammars are documented in the
[operations reference](operations.md#predicate-trees).
