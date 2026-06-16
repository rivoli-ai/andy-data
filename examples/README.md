# Andy.Data Examples

A runnable scenario suite that drives the framework-independent dataframe engine through its public surface only: construct a `DataFrameEngine`, call `engine.Execute(id, parameters, options)`, and read the typed `DataFrameResponse`. No tool framework, no dependency injection, no SQL.

Each scenario seeds a throwaway temp directory with small sample CSVs (`sales.csv`, `regions.csv`), runs against a fresh in-memory engine, and cleans up after itself.

## Running

```bash
# from the repository root
dotnet run --project examples/Andy.Data.Examples            # run every scenario
dotnet run --project examples/Andy.Data.Examples -- list    # list scenario names
dotnet run --project examples/Andy.Data.Examples -- join    # run one (or several) by name
```

## Scenarios

| Name | What it shows |
|------|---------------|
| `load-inspect` | Load a CSV, then read its schema and a bounded preview. |
| `filter-aggregate` | Filter rows with a structured predicate, `group_by` + `sum`/`count`, then `sort` top-down. |
| `derived-column` | Add a computed column from a structured **expression tree** (no SQL). |
| `join` | Inner-join two datasets on a shared key. |
| `export-reload` | Export a dataset to Parquet, then load it back (round-trip). |
| `error-envelope` | Trigger failures (`DATASET_NOT_FOUND`, `COLUMN_NOT_FOUND`) and read the stable error envelope. |
| `path-policy` | Confine filesystem access with an `IPathPolicy` sandbox; an export outside it returns `PERMISSION_DENIED`. |

## Prerequisites

- .NET 8.0 SDK or later
- No separate DuckDB install — the backend is embedded via the `Andy.Data` package.

## Where to go next

- [Getting Started](../docs/getting-started.md)
- [Operations Reference](../docs/operations.md)
- [Response Envelope Contract](../docs/tool-contract.md)
