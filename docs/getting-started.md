# Getting started

## Install

`Andy.Data` targets .NET 8. Add the two packages:

```bash
dotnet add package Andy.Data
dotnet add package Andy.Data.Abstractions
```

- **`Andy.Data`** — the DuckDB-backed engine: the backend, the SQL renderers, and the 28 operations.
- **`Andy.Data.Abstractions`** — the framework-independent contract types: the response envelope
  (`DataFrameResponse`), the error codes, the dataset catalog, and the structured predicate/expression
  models and parsers. It has no DuckDB dependency, so a host that only needs the contract types (e.g.
  to build a tool adapter) can reference it alone.

The native DuckDB binaries for Linux (x64/arm64), macOS, and Windows (x64/arm64) ship transitively
and are copied to your output directory automatically.

## The execute loop

Everything goes through one facade, [`DataFrameEngine`](concepts.md#the-engine):

```csharp
using Andy.Data.Operations;

using var engine = new DataFrameEngine(); // fresh in-memory DuckDB backend + dataset catalog

DataFrameResponse response = engine.Execute(
    operationId: "dataframe_load_csv",
    parameters: new Dictionary<string, object?>
    {
        ["path"] = "data/sales.csv",
        ["dataset_id"] = "sales",
    });
```

Three things to know:

1. **You dispatch by operation id** (`"dataframe_load_csv"`, `"dataframe_filter"`, …) and pass a
   `Dictionary<string, object?>` of parameters. The full list is in the
   [operations reference](operations.md).
2. **Every call returns the same `DataFrameResponse` envelope** — success or failure. No operation
   throws across the `Execute` boundary. Always check `response.Success`. See the
   [response & error contract](tool-contract.md).
3. **Datasets are named and session-scoped.** A load registers a dataset under a `dataset_id`; a
   transform reads one or more datasets and registers its result under `into` (or replaces the input
   when `into` is omitted). See [datasets and the catalog](concepts.md#datasets-and-the-catalog).

## A full pipeline

```csharp
using Andy.Data;
using Andy.Data.Operations;

using var engine = new DataFrameEngine();

// 1. Load.
engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales.csv", ["dataset_id"] = "sales",
});

// 2. Filter to the rows we care about (structured predicate — never SQL text).
engine.Execute("dataframe_filter", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales", ["into"] = "big_sales",
    ["predicate"] = new Dictionary<string, object?>
    {
        ["op"] = "and",
        ["conditions"] = new object[]
        {
            new Dictionary<string, object?> { ["column"] = "amount", ["op"] = "gte", ["value"] = 1000 },
            new Dictionary<string, object?> { ["column"] = "region", ["op"] = "in", ["values"] = new[] { "EU", "US" } },
        },
    },
});

// 3. Aggregate.
var summary = engine.Execute("dataframe_group_by", new Dictionary<string, object?>
{
    ["dataset_id"] = "big_sales", ["into"] = "summary", ["group_by"] = new[] { "region" },
    ["aggregations"] = new object[]
    {
        new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "total" },
        new Dictionary<string, object?> { ["column"] = "*", ["function"] = "count", ["alias"] = "orders" },
    },
});

// 4. Inspect the result.
if (summary.Success)
{
    foreach (var col in summary.Schema)
        Console.WriteLine($"{col.Name}: {col.Type} (nullable={col.Nullable})");

    Console.WriteLine($"rows: {summary.RowCount}, elapsed: {summary.Stats?.ElapsedMs} ms");

    foreach (var row in summary.PreviewRows)
        Console.WriteLine(string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));
}

// 5. Write it out.
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "summary", ["path"] = "out/summary.parquet",
    ["format"] = "parquet", ["mode"] = "overwrite",
});
```

## Reading the response

The success envelope carries a **bounded preview** (up to 50 rows), not the full result — the full
result stays in the engine under its `dataset_id`. Use the preview for inspection; chain another
operation, `export`, or `preview` (with a larger `limit`) to get more. See
[the contract](tool-contract.md#success-envelope) for every field.

```csharp
if (!response.Success)
{
    // Stable, branchable error code — see docs/tool-contract.md#error-codes
    Console.Error.WriteLine($"{response.ErrorCode}: {response.Message}");
    return;
}
```

## Resource limits & cancellation

Pass a `DataFrameExecuteOptions` to cap memory or wall-clock time, or to thread a cancellation token:

```csharp
var options = new DataFrameExecuteOptions
{
    MaxMemoryBytes = 512L * 1024 * 1024, // DuckDB memory_limit + spill-to-disk past it
    MaxExecutionTimeMs = 30_000,         // cancels with the CANCELLED error code if exceeded
    CancellationToken = ct,
};

engine.Execute("dataframe_group_by", parameters, options);
```

See [resource governance](concepts.md#resource-governance--cancellation).

## Where to go next

- [Concepts](concepts.md) — how datasets, lazy views, and the catalog actually behave (read this before
  building anything non-trivial).
- [Operations reference](operations.md) — the full vocabulary.
- [File formats](file-formats.md) — loading and exporting CSV/JSON/Parquet/Delta.
