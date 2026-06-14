# Migration: andy-tools-dataframe → andy-data + Andy.Tools.Data

This repository splits the DuckDB dataframe library into a **framework-independent core** (`andy-data`) and a thin **Andy.Tools integration** that will ship from the `andy-tools` repo. Goal: a data library that does not know or use `Andy.Tools`, with the tool wrappers built on top of it.

## Target architecture

```
rivoli-ai/andy-data            (this repo, private)
  Andy.Data.Abstractions       envelope, error codes, catalog, predicate/expression models + parsers
  Andy.Data                    DuckDB engine + SQL renderers + the operation API   (NO Andy.Tools dependency)

rivoli-ai/andy-tools           (existing)
  Andy.Tools.Data              thin ITool adapters (dataframe_* tools) + permissions glue, over Andy.Data
                               ships alongside Andy.Tools, same solution + CI

rivoli-ai/andy-tools-dataframe (archived once the above is live)
```

## Phases

- **Phase 0 — engine core (DONE).** Extracted the framework-free files (abstractions, DuckDB backend, Delta log, SQL renderers, observability) into `Andy.Data` / `Andy.Data.Abstractions` with renamed `Andy.Data.*` namespaces. Builds and tests green on Ubuntu/macOS/Windows (engine smoke tests + the abstractions parser/envelope/catalog suite). No `Andy.Tools` reference anywhere.

- **Phase 1 — operation API (NEXT).** Move the 28 operation bodies (filter, select, with_column, group_by, window, join, pivot, unpivot, unnest, sort, distinct, union, sample, rename, fillna, dropna, value_counts, assert, profile, schema, preview, list, drop, export, load_csv/json/parquet/delta) into `Andy.Data` as a framework-free operation surface that returns `DataFrameResponse`. This requires replacing the one piece of framework coupling — `ToolBase.ValidateParameters` (used by `Guard`) — with a small framework-free parameter validator (required / type / range / pattern / allowed-values) driven by an `Andy.Data`-owned parameter schema. The operation bodies themselves are already framework-free and already return `DataFrameResponse`. Port the integration/golden/property test suites to drive the operation API directly.

- **Phase 2 — Andy.Tools.Data adapter.** Add `Andy.Tools.Data` to `rivoli-ai/andy-tools`: thin `ITool` classes that hold the `ToolMetadata` (`dataframe_*` ids, parameter schemas, permissions), build an execution-options struct from `ToolExecutionContext`, call the `Andy.Data` operation, and map `DataFrameResponse` → `ToolResult`. Plus the `Andy.Permissions` glue. Consumes `Andy.Data` via a private feed (GitHub Packages) while this repo is private. Same solution + CI as `Andy.Tools`.

- **Phase 3 — archive.** Once `Andy.Tools.Data` ships from `andy-tools`, archive `rivoli-ai/andy-tools-dataframe`.

## Notes

- The model-facing contract (response-envelope shape + stable error codes) is preserved verbatim — it lives in `Andy.Data.Abstractions` and is unchanged by the move.
- The operation parameter contract stays `Dictionary<string, object?>` for v1 (no behavior change); typed request overloads can be added later.
