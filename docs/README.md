# Andy.Data documentation

`Andy.Data` is a structured, deterministic **dataframe engine** backed by [DuckDB](https://duckdb.org/),
with no dependency on any tool framework. You load tabular data (CSV, JSON, Parquet, partitioned
Parquet, Delta Lake), transform it through a **closed, injection-safe vocabulary** of 28 operations,
and read back a single stable response envelope. No model-supplied SQL or code is ever executed.

## Start here

| Doc | What it covers |
|-----|----------------|
| [Getting started](getting-started.md) | Install the package, load your first dataset, run the execute loop. |
| [Concepts](concepts.md) | The engine, datasets and the catalog, **lazy views**, resource limits, cancellation, concurrency, and the path policy. |
| [Operations reference](operations.md) | All 28 operations grouped by category, with parameters, defaults, and examples. Includes the [predicate-tree](operations.md#predicate-trees) and [expression-tree](operations.md#expression-trees) grammars. |
| [File formats](file-formats.md) | CSV, JSON, Parquet, partitioned Parquet, and Delta Lake — load and export options, partitioning, and Delta time travel. |
| [Response & error contract](tool-contract.md) | The response envelope, the `stats` block, and the stable error codes. |
| [Benchmarks](benchmarks.md) | Measured performance, scaling behavior, and known limits, plus a reproducible harness. |

## The operation surface at a glance

The 28 operations, grouped the way the [reference](operations.md) is organized:

| Category | Operations |
|----------|-----------|
| [Loading](operations.md#loading) | `load_csv`, `load_json`, `load_parquet`, `load_delta` |
| [Inspection](operations.md#inspection) | `schema`, `preview`, `profile`, `value_counts`, `assert`, `list` |
| [Projection & row selection](operations.md#projection--row-selection) | `select`, `filter`, `with_column`, `rename` |
| [Aggregation & analytics](operations.md#aggregation--analytics) | `group_by`, `window` |
| [Reshaping](operations.md#reshaping) | `pivot`, `unpivot`, `unnest` |
| [Combining](operations.md#combining) | `join`, `union` |
| [Ordering, sampling & dedup](operations.md#ordering-sampling--dedup) | `sort`, `sample`, `distinct` |
| [Missing data](operations.md#missing-data) | `fillna`, `dropna` |
| [Output & lifecycle](operations.md#output--lifecycle) | `export`, `drop` |

## Two-minute example

```csharp
using Andy.Data.Operations;

using var engine = new DataFrameEngine();

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

if (byRegion.Success)
{
    Console.WriteLine($"{byRegion.RowCount} regions; first preview row: " +
        string.Join(", ", byRegion.PreviewRows[0].Select(kv => $"{kv.Key}={kv.Value}")));
}
```

See [Getting started](getting-started.md) for the full walkthrough.
