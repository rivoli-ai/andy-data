# Andy Data

A structured, deterministic **dataframe engine** backed by [DuckDB](https://duckdb.org/), with **no dependency on any tool framework**. Load, transform, aggregate, join, reshape, and export tabular data (CSV, JSON, Parquet, partitioned Parquet, Delta Lake) through a closed, injection-safe vocabulary — no model-supplied SQL or code execution.

> **ALPHA**. No guarantees about functionality, stability, or safety. Query results have not been fully validated across all types/formats. Do not use in production or for decisions on critical data without independent verification. Use at your own risk. Licensed under [Apache 2.0](LICENSE).

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

Full documentation lives in [`docs/`](docs/):

- [Getting started](docs/getting-started.md) — install, first dataset, the execute loop.
- [Concepts](docs/concepts.md) — engine, datasets, lazy views, catalog, resource limits, concurrency, path policy.
- [Operations reference](docs/operations.md) — all 28 operations grouped by category, with parameters and examples; includes the [predicate](docs/operations.md#predicate-trees) and [expression](docs/operations.md#expression-trees) tree grammars.
- [File formats](docs/file-formats.md) — CSV, JSON, Parquet, partitioned Parquet, and Delta Lake (load + export options, partitioning, time travel).
- [Response & error contract](docs/tool-contract.md) — the response envelope, stats, and the stable error codes.
- [Benchmarks](docs/benchmarks.md) — performance characteristics, scaling, and known limits, with a reproducible harness in [`benchmarks/`](benchmarks/).

## Status

The framework-independent **engine + operation API** is complete and tested across Ubuntu/macOS/Windows. The `Andy.Tools.Data` integration (the `dataframe_*` LLM tools, shipped from the `andy-tools` repo) and the archival of `andy-tools-dataframe` are tracked in the `andy-tools` repository.

## Build & test

```bash
dotnet build
dotnet test
```

## Benchmarks

```bash
dotnet run --project benchmarks/Andy.Data.Benchmarks -c Release -- 100000,1000000,5000000 5
```

See [docs/benchmarks.md](docs/benchmarks.md) for measured results and analysis.
