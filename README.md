# Andy Data

A structured, deterministic **dataframe engine** backed by [DuckDB](https://duckdb.org/), with **no dependency on any tool framework**. Load, transform, aggregate, join, reshape, and export tabular data (CSV, JSON, Parquet, partitioned Parquet, Delta Lake) through a closed, injection-safe vocabulary — no model-supplied SQL or code execution.

> **ALPHA RELEASE WARNING**
>
> This software is in ALPHA stage. **NO GUARANTEES** are made about its functionality, stability, or safety.
>
> **CRITICAL WARNINGS:**
> - Query results have **NOT BEEN FULLY VALIDATED** across all data types and formats
> - Schema inference and type coercion may behave unexpectedly on malformed inputs
> - **DO NOT USE** in production environments
> - **DO NOT USE** for decisions on critical or irreplaceable data without independent verification
> - The authors assume **NO RESPONSIBILITY** for incorrect results, data loss, or damages
>
> **USE AT YOUR OWN RISK**

Licensed under the [Apache License 2.0](LICENSE).

## What this is

`Andy.Data` is the **framework-independent core** extracted from [`andy-tools-dataframe`](https://github.com/rivoli-ai/andy-tools-dataframe). It knows nothing about `Andy.Tools`: it is a plain .NET library you can embed directly, or build a tool/agent integration on top of.

The Andy.Tools integration (the `dataframe_*` LLM tools) lives separately as `Andy.Tools.Data` in [`andy-tools`](https://github.com/rivoli-ai/andy-tools); it depends on this package.

| Package | What it is |
|---------|-----------|
| `Andy.Data` | The DuckDB-backed engine: backend, SQL renderers, the operation surface, and the embedded analytical runtime. |
| `Andy.Data.Abstractions` | Framework-independent contract types: the response envelope (`DataFrameResponse`), error codes, dataset catalog, and the structured predicate/expression models + parsers. No dependency on DuckDB. |

## Design properties (carried over from andy-tools-dataframe)

- **No code execution / no injection surface.** Predicates, expressions, and aggregations use closed, enumerated vocabularies; identifiers are schema-resolved and quoted; literals are escaped. Every SQL token is a fixed renderer template, a schema-resolved quoted identifier, or an escaped literal.
- **Deterministic, legible results.** A stable response envelope and a stable set of error codes; explicit ordering, type, and null handling; round-trippable numeric serialization.
- **Delta write durability.** Atomic, put-if-absent commits with cross-process optimistic-concurrency retry; staged-then-swapped new/overwrite tables.
- **Resource governance & cancellation.** Host-set memory limit (DuckDB `memory_limit` + spill) and cooperative cancellation through loaders and transforms.
- **Concurrency.** Thread-safe: one backend instance == one DuckDB connection used under a lock (safe to call concurrently; parallelism is intra-query). Use one backend instance per stream for inter-query parallelism.

## Operation API

All 28 operations are available as a framework-independent API. Use the `DataFrameEngine` facade and dispatch by operation id, passing a parameters dictionary and getting back a `DataFrameResponse`:

```csharp
using Andy.Data.Operations;

using var engine = new DataFrameEngine(); // fresh in-memory DuckDB backend + catalog

engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales.csv", ["dataset_id"] = "sales",
});

var byRegion = engine.Execute("dataframe_group_by", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales", ["group_by"] = new[] { "region" },
    ["aggregations"] = new object[]
    {
        new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "total" },
    },
});

if (byRegion.Success) { /* byRegion.Schema, byRegion.RowCount, byRegion.PreviewRows, byRegion.Warnings, byRegion.Stats */ }
```

Each operation is also usable directly (e.g. `new FilterOperation(backend, catalog).Execute(parameters, options)`); resource limits and cancellation are passed via `DataFrameExecuteOptions`. Parameters are validated against each operation's declared schema (`DataFrameParameterValidator`) before the body runs, producing the documented error codes — no tool-framework dependency.

The 28 operations: load_csv/json/parquet/delta, schema, profile, preview, value_counts, assert, select, filter, with_column, rename, group_by, window, pivot, unpivot, unnest, join, sample, sort, distinct, union, fillna, dropna, export, list, drop.

## Documentation

Full technical documentation lives in [`docs/`](docs/README.md):

- [Getting Started](docs/getting-started.md) — build, construct an engine, run your first operation
- [Core Concepts](docs/core-concepts.md) — datasets, the catalog, the response envelope, lifecycle
- [Architecture](docs/architecture.md) — layers, SQL rendering, the DuckDB backend
- [Operations Reference](docs/operations.md) — every operation, with parameters and the predicate/expression grammars
- [Response Envelope Contract](docs/tool-contract.md) — the stable success/failure shape and error codes
- [Reliability](docs/reliability.md) — determinism, schema handling, and the error contract
- [Security](docs/security.md) — the injection-free model and the `IPathPolicy` filesystem gate
- [Troubleshooting](docs/troubleshooting.md) — common issues and resolutions

Runnable end-to-end samples are in [`examples/`](examples/README.md).

## Status

The framework-independent **engine + operation API** is complete and tested across Ubuntu/macOS/Windows. The Andy.Tools integration (the `dataframe_*` LLM tools, `Andy.Tools.Data`) ships separately from the [`andy-tools`](https://github.com/rivoli-ai/andy-tools) repo and builds on this package; the original [`andy-tools-dataframe`](https://github.com/rivoli-ai/andy-tools-dataframe) repo is being archived in favor of this split.

## Build & test

```bash
dotnet build
dotnet test
```

## Examples

```bash
dotnet run --project examples/Andy.Data.Examples       # run the full scenario suite
dotnet run --project examples/Andy.Data.Examples -- list
```

## License

Apache License 2.0. See [LICENSE](LICENSE).
