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

- **Phase 1 — operation API (DONE).** Moved all 28 operation bodies into `Andy.Data/Operations` as framework-free `*Operation` classes (`: DataFrameOperationBase`) returning `DataFrameResponse`. Added the framework-free pieces that replace `ToolBase`: `DataFrameParam`/`OperationMetadata` (parameter schema), `DataFrameExecuteOptions` (memory/time/cancellation), `DataFrameParameterValidator` (required/type/range/pattern/allowed-values → the documented error codes), and `DataFrameOperationBase` (the `Guard` validate-run-map pipeline plus all the shared helpers — `Materialize`, `Finish`, `Resolve*`, `Get*`, `ToStringList`, path canonicalization + symlink resolution). Added a `DataFrameEngine` facade (construct-all + dispatch-by-id, with a self-contained backend option). A focused per-operation test suite (every operation exercised through the engine) is green on Ubuntu/macOS/Windows, alongside the ported abstractions suite. The exhaustive behavioral suite remains in `andy-tools-dataframe` until that repo is archived.

- **Phase 2 — Andy.Tools.Data adapter.** Add `Andy.Tools.Data` to `rivoli-ai/andy-tools`: thin `ITool` classes that hold the `ToolMetadata` (`dataframe_*` ids, parameter schemas, permissions), build an execution-options struct from `ToolExecutionContext`, call the `Andy.Data` operation, and map `DataFrameResponse` → `ToolResult`. Plus the `Andy.Permissions` glue. Consumes `Andy.Data` via a private feed (GitHub Packages) while this repo is private. Same solution + CI as `Andy.Tools`.

- **Phase 3 — archive.** Once `Andy.Tools.Data` ships from `andy-tools`, archive `rivoli-ai/andy-tools-dataframe`.

## Notes

- The model-facing contract (response-envelope shape + stable error codes) is preserved verbatim — it lives in `Andy.Data.Abstractions` and is unchanged by the move.
- The operation parameter contract stays `Dictionary<string, object?>` for v1 (no behavior change); typed request overloads can be added later.
