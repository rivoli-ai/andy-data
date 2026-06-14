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

## Status

This repository currently hosts the **engine core** (backend, SQL renderers, predicate/expression parsers, and the response/error envelope), building and tested across Ubuntu/macOS/Windows. The full framework-independent **operation API** (the migration of the 28 `filter`/`group_by`/`join`/… operations into this library, with a framework-free parameter validator) is in progress — see [MIGRATION.md](MIGRATION.md).

## Build & test

```bash
dotnet build
dotnet test
```
