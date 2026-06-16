# Getting Started

`Andy.Data` is the framework-independent dataframe engine extracted from
`andy-tools-dataframe`. It is a plain .NET 8 library: you construct a
`DataFrameEngine`, dispatch operations by id with a parameters dictionary, and
read back a single, stable `DataFrameResponse`. There is no dependency injection,
no tool framework, and no model-supplied SQL or code — every operation is a
closed, schema-resolved, injection-safe primitive backed by an embedded DuckDB.

## Prerequisites

- **.NET 8 SDK** or later.
- **DuckDB** — embedded, no separate install required. The `Andy.Data` package
  pulls in `DuckDB.NET.Data.Full`, which carries the native DuckDB runtime, so
  the analytical engine ships inside the library.

## Clone and build

```bash
git clone https://github.com/rivoli-ai/andy-data.git
cd andy-data
dotnet build
dotnet test
```

## Referencing the library

Two of the packages matter to consumers:

| Package | What it gives you |
|---------|-------------------|
| `Andy.Data` | The DuckDB-backed engine: the `DataFrameEngine` facade, the 28 operations, the backend, and the SQL renderers. |
| `Andy.Data.Abstractions` | The framework-independent contract types: `DataFrameResponse`, `ColumnSchema`, `DataFrameStats`, `DataFrameErrorCodes`, `IDatasetCatalog`, and the structured predicate/expression models. No DuckDB dependency. |

Add a project reference to the engine (which transitively references the
abstractions):

```xml
<ItemGroup>
  <ProjectReference Include="../andy-data/src/Andy.Data/Andy.Data.csproj" />
</ItemGroup>
```

Or reference the published NuGet package:

```bash
dotnet add package Andy.Data
```

## Constructing an engine

The engine is the only entry point you need. The parameterless constructor spins
up a fresh in-memory DuckDB backend and an in-memory dataset catalog:

```csharp
using Andy.Data;            // contract types: DataFrameResponse, ColumnSchema, DataFrameErrorCodes
using Andy.Data.Operations; // DataFrameEngine, DataFrameExecuteOptions

using var engine = new DataFrameEngine();
```

The engine is `IDisposable` and owns the backend it created — the `using`
statement disposes the DuckDB connection at the end of the scope. Construct one
engine per session (see [core-concepts.md](core-concepts.md) on concurrency).

Two constructor overloads exist:

```csharp
// Override the path policy (file read/write sandboxing) and/or logging.
new DataFrameEngine(IPathPolicy? pathPolicy = null, ILoggerFactory? loggerFactory = null);

// Bring your own backend and catalog (you own the backend's lifetime).
new DataFrameEngine(IDuckDbBackend backend, IDatasetCatalog catalog,
                    IPathPolicy? pathPolicy = null, ILoggerFactory? loggerFactory = null);
```

You can inspect the registered operations through `engine.Operations`
(an `IReadOnlyCollection<OperationMetadata>`), fetch one by id with
`engine.Get(id)`, and reach the underlying `engine.Backend` and `engine.Catalog`.

## Your first operation: load a CSV and inspect the schema

Every operation runs through `engine.Execute`, which is **synchronous**:

```csharp
DataFrameResponse Execute(
    string operationId,
    IReadOnlyDictionary<string, object?> parameters,
    DataFrameExecuteOptions? options = null);
```

Load a CSV into a named, session-scoped dataset and print its inferred schema:

```csharp
var load = engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales.csv",
    ["dataset_id"] = "sales",
});

if (load.Success)
{
    Console.WriteLine($"Loaded '{load.DatasetId}' with {load.RowCount} rows.");
    foreach (var column in load.Schema)
    {
        // column.Type is the verbatim DuckDB type, e.g. VARCHAR or DECIMAL(12,2).
        Console.WriteLine($"  {column.Name}: {column.Type} (nullable: {column.Nullable})");
    }
}
else
{
    Console.WriteLine($"[{load.ErrorCode}] {load.Message}");
}
```

## A first transformation: filter, then group_by

Operations are orthogonal primitives you chain by `dataset_id`. The result of a
transform is registered under the optional `into` parameter; if you omit `into`,
the result **replaces** the input `dataset_id`. Here we keep the chain explicit by
writing each step into a new dataset.

Filtering uses a structured **predicate tree** — never a SQL string. A condition
node is `{ column, op, value }`:

```csharp
var filtered = engine.Execute("dataframe_filter", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "sales_eu",
    ["predicate"] = new Dictionary<string, object?>
    {
        ["column"] = "region",
        ["op"] = "eq",
        ["value"] = "EU",
    },
});
```

Aggregating uses a list of aggregation specs `{ column, function, alias }`:

```csharp
var byCategory = engine.Execute("dataframe_group_by", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales_eu",
    ["into"] = "eu_totals",
    ["group_by"] = new[] { "category" },
    ["aggregations"] = new object[]
    {
        new Dictionary<string, object?>
        {
            ["column"] = "amount",
            ["function"] = "sum",
            ["alias"] = "total",
        },
    },
});
```

## Reading results

Always branch on `Success` first. The same `DataFrameResponse` shape carries both
outcomes (see [tool-contract.md](tool-contract.md)):

```csharp
if (byCategory.Success)
{
    Console.WriteLine($"Rows: {byCategory.RowCount}");

    // Schema describes the output columns.
    foreach (var c in byCategory.Schema)
        Console.WriteLine($"  {c.Name}: {c.Type}");

    // PreviewRows is a bounded list of dictionaries (column name -> value).
    foreach (IReadOnlyDictionary<string, object?> row in byCategory.PreviewRows)
        Console.WriteLine(string.Join(", ", row.Select(kv => $"{kv.Key}={kv.Value}")));

    if (byCategory.PreviewTruncated)
        Console.WriteLine("(preview truncated — export for the full result)");

    foreach (var warning in byCategory.Warnings)
        Console.WriteLine($"warning: {warning}");

    // Stats carries timing and volume; Stats.Plan is set only with explain = true.
    Console.WriteLine($"took {byCategory.Stats?.ElapsedMs} ms, " +
                      $"produced {byCategory.Stats?.RowsProduced} rows");
}
else
{
    Console.WriteLine($"[{byCategory.ErrorCode}] {byCategory.Message}");
}
```

`PreviewRows` is intentionally **bounded** — it is a small, model-friendly sample,
not the whole dataset. `PreviewTruncated` is `true` whenever the dataset has more
rows than the preview holds.

## Exporting full results

Because previews are bounded, the full materialized result comes from
`dataframe_export`, which writes a dataset to disk (CSV, Parquet, JSON, or Delta):

```csharp
var export = engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "eu_totals",
    ["path"] = "out/eu_totals.parquet",
    ["format"] = "parquet",
    ["mode"] = "overwrite", // error (default) | append (Delta only) | overwrite
});
```

Exporting requires filesystem write permission (governed by the engine's path
policy) and returns the standard envelope.

## Next steps

- [core-concepts.md](core-concepts.md) — datasets, the catalog, composition,
  lifecycle, concurrency, and resource governance.
- [operations.md](operations.md) — the full catalog of operations and their
  parameters.
- [examples/README.md](../examples/README.md) — end-to-end worked examples.
