# Andy.Data — Technical Documentation

`Andy.Data` is a structured, deterministic **dataframe engine** backed by [DuckDB](https://duckdb.org/), with **no dependency on any tool framework**. It loads, transforms, aggregates, joins, reshapes, and exports tabular data (CSV, JSON, Parquet, partitioned Parquet, Delta Lake) through a closed, injection-safe operation vocabulary — no model-supplied SQL and no code execution.

This is the framework-independent core extracted from [`andy-tools-dataframe`](https://github.com/rivoli-ai/andy-tools-dataframe). You embed it directly — `new DataFrameEngine()` — or build a tool/agent integration on top of it. The Andy.Tools integration (`Andy.Tools.Data`, the `dataframe_*` LLM tools) ships separately from [`andy-tools`](https://github.com/rivoli-ai/andy-tools) and depends on this package.

> **ALPHA.** Query results have not been fully validated across all types and formats. Do not use in production or for decisions on critical data without independent verification. See the [root README](../README.md) and [LICENSE](../LICENSE).

## Table of Contents

1. [Getting Started](getting-started.md) — build, construct an engine, run your first operation
2. [Core Concepts](core-concepts.md) — datasets, the catalog, the response envelope, lifecycle
3. [Architecture](architecture.md) — layers, SQL rendering, the DuckDB backend, efficiency
4. [Operations Reference](operations.md) — every operation, with parameters and the predicate/expression grammars
5. [Response Envelope Contract](tool-contract.md) — the stable success/failure shape and error codes
6. [Reliability](reliability.md) — determinism, schema handling, durability, and the error contract
7. [Security](security.md) — the injection-free model and the `IPathPolicy` filesystem gate
8. [Troubleshooting](troubleshooting.md) — common issues and resolutions

## The two packages

| Package | What it is |
|---------|-----------|
| `Andy.Data` | The DuckDB-backed engine: backend, SQL renderers, the 28 operations, the `DataFrameEngine` facade, and observability. |
| `Andy.Data.Abstractions` | Framework-independent contract types: the response envelope (`DataFrameResponse`), error codes, dataset catalog, and the structured predicate/expression models + parsers. No dependency on DuckDB. |

## Quick links

- [Runnable examples](../examples/README.md) — `dotnet run --project examples/Andy.Data.Examples`
- [GitHub repository](https://github.com/rivoli-ai/andy-data)
- [DuckDB documentation](https://duckdb.org/docs/)

## License

Apache License 2.0. See [LICENSE](../LICENSE).
