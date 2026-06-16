# Operations Reference

> **Operations** — the first pillar. This is the complete catalog of dataframe operations, organized by category. Every operation is framework-independent: there is no tool framework, no DI container, and no permission subsystem in this repo. You dispatch an operation by id through the engine facade —
>
> ```csharp
> using var engine = new DataFrameEngine();      // fresh in-memory DuckDB backend + catalog
> DataFrameResponse r = engine.Execute(operationId, parameters, options);
> ```
>
> — and every call returns the same [common response envelope](tool-contract.md), a `DataFrameResponse` whose typed properties you read directly (`r.Success`, `r.Schema`, `r.RowCount`, `r.PreviewRows`, `r.Warnings`, `r.Stats`, or on failure `r.ErrorCode` / `r.Message`). Calls are **synchronous** — they return a `DataFrameResponse`, not a `Task`.

Operations are **orthogonal and composable**: each does one thing, produces a named dataset, and chains into the next. Coverage comes from composition, not from a large surface area. The core set below is drawn from relational algebra (select, filter, join, group-by, sort, distinct, union) plus the analytical extensions (window, pivot) that cover the large majority of real workflows.

## The engine API

```csharp
using Andy.Data;             // DataFrameResponse, ColumnSchema, DataFrameStats, DataFrameErrorCodes, IPathPolicy
using Andy.Data.Operations;  // DataFrameEngine, DataFrameExecuteOptions, *Operation classes
using Andy.Data.Backend;     // IDuckDbBackend, DuckDbBackend
using Andy.Data.Predicates;  // PredicateNode, PredicateParser
using Andy.Data.Expressions; // ExprNode, ExpressionParser

using var engine = new DataFrameEngine();   // owns its backend; disposing closes it

var r = engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales.csv",
    ["dataset_id"] = "sales",
});

if (r.Success)
{
    Console.WriteLine($"{r.DatasetId}: {r.RowCount} rows, {r.Schema.Count} columns");
    foreach (var col in r.Schema)
        Console.WriteLine($"  {col.Name}: {col.Type} (nullable={col.Nullable})");
    foreach (var w in r.Warnings)
        Console.WriteLine($"  warning: {w}");
}
else
{
    Console.WriteLine($"[{r.ErrorCode}] {r.Message}");
}
```

**`DataFrameResponse`** (class, namespace `Andy.Data`) — one shape for success and failure:

| Property | Type | Meaning |
|----------|------|---------|
| `Success` | `bool` | Whether the operation succeeded. |
| `DatasetId` | `string?` | The resulting dataset id (`into`, or the input `dataset_id` it replaced). |
| `Schema` | `IReadOnlyList<ColumnSchema>` | Ordered output columns. `ColumnSchema` is `record(string Name, string Type, bool Nullable = true)`. |
| `RowCount` | `long?` | Row count of the result. |
| `PreviewRows` | `IReadOnlyList<IReadOnlyDictionary<string, object?>>` | A bounded set of result rows. |
| `PreviewTruncated` | `bool` | `true` when more rows exist than were previewed. |
| `Warnings` | `IReadOnlyList<string>` | Non-fatal notes (coercions, data-quality summaries, …). |
| `Stats` | `DataFrameStats?` | `record(long ElapsedMs, long BytesScanned, long RowsProduced, string? Plan = null)`. |
| `ErrorCode` | `string?` | On failure, one of the [error codes](#error-codes). |
| `Message` | `string?` | On failure, a human-readable explanation. |
| `Details` | `IReadOnlyDictionary<string, object?>?` | On failure, optional structured context. |

**`DataFrameExecuteOptions`** (namespace `Andy.Data.Operations`) — per-call resource governance and cancellation:

- `long? MaxMemoryBytes` — DuckDB `memory_limit` in bytes; `null` or non-positive means unset.
- `int? MaxExecutionTimeMs` — wall-clock cap; a positive value cancels the operation (`CANCELLED`) after that many milliseconds.
- `CancellationToken CancellationToken` — caller cancellation.
- `static readonly DataFrameExecuteOptions Default` — no limits, no cancellation. Passed implicitly when you omit the third argument to `Execute`.

```csharp
var r = engine.Execute("dataframe_group_by", parameters, new DataFrameExecuteOptions
{
    MaxExecutionTimeMs = 30_000,
    MaxMemoryBytes = 2L * 1024 * 1024 * 1024,
    CancellationToken = ct,
});
```

### Calling an operation directly (no facade)

The facade simply constructs each operation once and dispatches by id. You can instead construct a single operation against your own backend and catalog and call its `Execute` directly — the same envelope comes back:

```csharp
using var backend = new DuckDbBackend();
var catalog = new InMemoryDatasetCatalog();
var r = new FilterOperation(backend, catalog).Execute(parameters, DataFrameExecuteOptions.Default);
```

Loaders and `dataframe_export` additionally accept an `IPathPolicy?` constructor argument to restrict which paths they may read or write (see [security.md](security.md)); the engine forwards the policy you pass to `new DataFrameEngine(pathPolicy)`.

## Conventions

- **`dataset_id`** (string, required on most ops): the input dataset.
- **`into`** (string, optional): the output dataset id. If omitted, the result replaces `dataset_id`.
- All operations are read-only with respect to source files; only `dataframe_export` writes.
- Column identifiers are validated against the dataset schema before execution.
- **`explain`** (boolean, optional, default `false`): accepted by the sixteen transformation operations — `select`, `filter`, `with_column`, `join`, `group_by`, `window`, `pivot`, `unpivot`, `unnest`, `sort`, `distinct`, `union`, `sample`, `fillna`, `dropna`, `rename`. When `true`, the DuckDB query plan is included in `r.Stats.Plan` (the `stats.plan` envelope field) without changing the result. See [architecture.md](architecture.md#explain-plans).
- See [Predicate trees](#predicate-trees) and [Expression trees](#expression-trees) for the structured grammars used by `filter` and `with_column`.

---

## Loading

### dataframe_load_csv

Load a CSV file (or glob) into a session dataset. Types are inferred unless overridden by `columns`.

**Parameters:**
- `path` (string, required): File path or glob (e.g. `data/*.csv`).
- `dataset_id` (string, required): Id to register the dataset under.
- `header` (boolean, optional, default: auto-detect): First row contains column names.
- `delimiter` (string, optional, default: auto-detect): Field delimiter.
- `quote` (string, optional, default: auto-detect): Quote character — exactly one character.
- `null_string` (string, optional): Token to treat as NULL (e.g. `"NA"`).
- `columns` (object, optional): Column-to-type map of schema hints (e.g. `{ "amount": "DECIMAL(12,2)" }`); overrides inference for those columns. Other columns keep inference.
- `sample_size` (integer, optional, default `20480`): Rows sampled for type inference; `-1` reads the whole file.

**Returns:** standard envelope; `r.Schema` reflects inferred/declared types, `r.Warnings` notes any coercions.

```csharp
engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales_*.csv",
    ["dataset_id"] = "sales",
    ["null_string"] = "NA",
    ["columns"] = new Dictionary<string, object?> { ["amount"] = "DECIMAL(12,2)" }
});
```

### dataframe_load_parquet

Load a Parquet file, glob, or Hive-partitioned directory. Schema and types come from the file metadata.

**Parameters:**
- `path` (string, required): File, glob, or directory (e.g. `events/` with `year=/month=` subdirs).
- `dataset_id` (string, required).
- `hive_partitioning` (boolean, optional, default: auto-detect): Treat `key=value/` directories as partition columns. When omitted, DuckDB auto-detects the layout from the matched paths; set it explicitly to force the behavior either way.
- `union_by_name` (boolean, optional, default `false`): Align columns by name across files with differing schemas.

**Returns:** standard envelope; partition keys appear as columns. Partition pruning is applied automatically when a later `filter` constrains a partition key — see [architecture.md](architecture.md#efficiency).

```csharp
engine.Execute("dataframe_load_parquet", new Dictionary<string, object?>
{
    ["path"] = "warehouse/events/",   // events/year=2025/month=01/*.parquet
    ["dataset_id"] = "events"
});
```

### dataframe_load_json

Load a JSON file or glob: newline-delimited JSON (NDJSON), a top-level array of objects, or auto-detected. Schema and column types are inferred from the JSON values.

**Parameters:**
- `path` (string, required): JSON file or glob (e.g. `data/*.ndjson`).
- `dataset_id` (string, required).
- `format` (string, optional, default `auto`): `auto` (detect the layout), `newline_delimited` (NDJSON), or `array` (a top-level JSON array of objects).

**Returns:** standard envelope; nested objects/arrays surface as DuckDB `STRUCT`/`LIST` columns.

```csharp
engine.Execute("dataframe_load_json", new Dictionary<string, object?>
{
    ["path"] = "data/events.ndjson",
    ["dataset_id"] = "events",
    ["format"] = "newline_delimited"   // omit for auto-detection
});
```

### dataframe_load_delta

Load a Delta Lake table — the latest snapshot, or an earlier one via **time travel**.

> **Latest snapshot** uses the DuckDB `delta` extension, auto-installed/loaded on first use
> (`INSTALL delta` / `LOAD delta`), which may need network access the first time, or be bundled with
> the DuckDB build. If it cannot be loaded, the operation returns `BACKEND_ERROR`.
>
> **Time travel** (`version` or `timestamp`) does *not* use the extension — the extension's
> `delta_scan` exposes no version/timestamp parameter. Instead the transaction log (`_delta_log/`) is
> replayed directly to resolve the data files active at that point, which also means time travel works
> with no extension or network. This is a happy-path reader: it supports **unpartitioned** tables whose
> history is plain JSON commits. It returns `BACKEND_ERROR` for checkpointed tables, partition columns,
> deletion vectors, or other reader features, and for a `version`/`timestamp` the log cannot satisfy.
> **Checkpoint limitation:** a table that contains a `_last_checkpoint` marker or any
> `*.checkpoint.parquet` files in `_delta_log/` is not supported for time travel or `append`; both
> operations require replaying JSON commits from version 0, and checkpoints mean older commits may have
> been compacted away. Load the latest snapshot (omit `version`/`timestamp`) to read checkpointed tables.
> `timestamp` resolves to the latest version committed at or before it. Pass at most one of
> `version`/`timestamp`.

**Parameters:**
- `path` (string, required): Delta table root.
- `dataset_id` (string, required).
- `version` (integer, optional): Load this snapshot version. Mutually exclusive with `timestamp`.
- `timestamp` (string, optional): ISO-8601 instant; loads the latest version at or before it.
  Mutually exclusive with `version`.

```csharp
engine.Execute("dataframe_load_delta", new Dictionary<string, object?>
{
    ["path"] = "lake/orders",
    ["dataset_id"] = "orders",
    ["version"] = 7              // omit version/timestamp for the latest snapshot
});
```

---

## Inspection

### dataframe_schema

Return the schema without scanning data.

**Parameters:** `dataset_id` (string, required).

**Returns:** `r.Schema` = ordered `ColumnSchema(Name, Type, Nullable)`; `r.RowCount` is `0` if the dataset has not been materialized.

### dataframe_profile

Compute per-column statistics.

**Parameters:**
- `dataset_id` (string, required).
- `columns` (array, optional): Subset to profile (default: all).
- `quantiles` (array of numbers, optional, default `[0.25, 0.5, 0.75]`): Quantiles for numeric columns.

**Returns:** envelope where `r.PreviewRows` holds one row per column — a pandas `describe()`-style summary — with: `null_count`, `distinct_count`, `count` (non-null), `min`, `max`, and, for numeric columns, `mean`, `std` (sample standard deviation), and the requested quantiles. Numeric statistics are serialized round-trippably (see [reliability.md](reliability.md#round-trippable-numeric-serialization)).

```csharp
var profile = engine.Execute("dataframe_profile", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["columns"] = new[] { "amount", "quantity" }
});
```

### dataframe_value_counts

Count how often each distinct value of a column occurs — the categorical-EDA equivalent of pandas `Series.value_counts()`.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `column` (string, required): Column whose value frequencies to count.
- `limit` (integer, optional): Keep only the top-N most frequent values.
- `dropna` (boolean, optional, default `true`): Exclude NULL values, matching pandas.

**Returns:** envelope for the new dataset registered under `into` (or replacing `dataset_id`), with columns `<column>`, `count`, and `proportion` (the value's share of the counted rows), ordered by `count` descending and the value ascending (a deterministic total order).

```csharp
var counts = engine.Execute("dataframe_value_counts", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "region_counts",
    ["column"] = "region",
    ["limit"] = 10
});
```

### dataframe_assert

Evaluate data-quality expectations against a dataset and return a per-expectation pass/fail report — a first-class equivalent of pandera / Great Expectations checks. It does not modify or register a dataset.

**Parameters:**
- `dataset_id` (string, required).
- `expectations` (array, required): One or more `{ "type", ... }` specs:
  - `not_null` — `{ type, column }`: the column has no NULLs.
  - `unique` — `{ type, column }`: no value occurs more than once.
  - `in_range` — `{ type, column, min?, max? }`: non-null values fall within the (inclusive) bounds; at least one of `min`/`max`.
  - `in_set` — `{ type, column, values }`: non-null values are drawn from `values`.
  - `matches` — `{ type, column, pattern }`: non-null values match the regular expression.
  - `row_count` — `{ type, equals?, min?, max? }`: the row count satisfies the bounds; at least one of `equals`/`min`/`max`.

**Returns:** the standard envelope where `r.PreviewRows` holds one row per expectation: `expectation`, `column`, `passed` (boolean), `violations` (count), and `details`. When any expectation fails, a `r.Warnings` entry summarizes how many — so a caller can branch on data quality without parsing each row.

```csharp
var report = engine.Execute("dataframe_assert", new Dictionary<string, object?>
{
    ["dataset_id"] = "orders",
    ["expectations"] = new object[]
    {
        new Dictionary<string, object?> { ["type"] = "not_null", ["column"] = "order_id" },
        new Dictionary<string, object?> { ["type"] = "unique", ["column"] = "order_id" },
        new Dictionary<string, object?> { ["type"] = "in_range", ["column"] = "amount", ["min"] = 0 },
        new Dictionary<string, object?> { ["type"] = "row_count", ["min"] = 1 }
    }
});

if (report.Success && report.Warnings.Count > 0)
    Console.WriteLine($"data-quality issues: {string.Join("; ", report.Warnings)}");
```

### dataframe_preview

Return a bounded set of rows.

**Parameters:**
- `dataset_id` (string, required).
- `mode` (string, optional, default `head`): one of `head`, `tail`, `sample`.
- `limit` (integer, optional, default `50`, max `1000`): Number of rows.
- `seed` (integer, **required when `mode = sample`**): Makes sampling deterministic. A `sample` request without a `seed` is rejected with `INVALID_ARGUMENT` rather than returning unstable rows (see [reliability.md](reliability.md#determinism)). Ignored for `head`/`tail`.

The rows are returned in `r.PreviewRows`; `r.PreviewTruncated` indicates whether the dataset holds more rows than were returned.

---

## Transformation

### dataframe_select

Project, rename, and reorder columns.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `columns` (array, required): Each entry is either a column name (string) or `{ "column", "as" }` to rename.

```csharp
engine.Execute("dataframe_select", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "sales_slim",
    ["columns"] = new object[]
    {
        "region",
        new Dictionary<string, object?> { ["column"] = "amount", ["as"] = "revenue" }
    }
});
```

### dataframe_rename

Rename one or more columns while keeping all other columns unchanged.

**Parameters:**
- `dataset_id` (string, required), `into` (string, required).
- `columns` (object, required): map of old column name → new column name.

Preserves column order. Two old columns may not be renamed to the same new name, and every old name must exist in the source schema.

```csharp
engine.Execute("dataframe_rename", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "sales_renamed",
    ["columns"] = new Dictionary<string, object?>
    {
        ["amount"] = "revenue",
        ["region"] = "territory"
    }
});
```

### dataframe_filter

Select rows matching a [predicate tree](#predicate-trees).

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `predicate` (object, required): A predicate tree.

```csharp
engine.Execute("dataframe_filter", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["predicate"] = new Dictionary<string, object?>
    {
        ["op"] = "and",
        ["conditions"] = new object[]
        {
            new Dictionary<string, object?> { ["column"]="status", ["op"]="eq", ["value"]="completed" },
            new Dictionary<string, object?> { ["column"]="amount", ["op"]="gte", ["value"]=100 }
        }
    }
});
```

### dataframe_with_column

Add or replace a column computed from an [expression tree](#expression-trees).

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `name` (string, required): New/replaced column name.
- `expression` (object, required): An expression tree.

```csharp
engine.Execute("dataframe_with_column", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["name"] = "net",
    ["expression"] = new Dictionary<string, object?>
    {
        ["op"] = "subtract",
        ["args"] = new object[]
        {
            new Dictionary<string, object?> { ["column"] = "amount" },
            new Dictionary<string, object?> { ["column"] = "discount" }
        }
    }
});
```

### dataframe_fillna

Replace NULL values — in **scalar mode** with a constant and/or per-column replacements, or in **carry mode** by forward/backward-filling along an ordering (the pandas `ffill`/`bfill` equivalent).

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `value` (string, optional): Scalar-mode global replacement value. Coerced to each column's type.
- `values` (object, optional): Scalar-mode `{ column: replacement }` map overriding `value` for those columns.
- `method` (string, optional): Carry mode — `ffill` (carry the last non-null value forward) or `bfill` (carry the next non-null value backward). Requires `order_by`. Cannot be combined with `value`/`values`.
- `order_by` (array, optional): Ordering column(s) that define previous/next for `method` (required when `method` is set).
- `partition_by` (array, optional): Carry-mode grouping columns; the fill restarts within each group.
- `columns` (array, optional): Carry-mode subset of columns to fill (default: all columns).
- In scalar mode, at least one of `value` or `values` must be provided.

```csharp
// Scalar mode
engine.Execute("dataframe_fillna", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "sales_filled",
    ["value"] = "0",
    ["values"] = new Dictionary<string, object?> { ["region"] = "Unknown" }
});

// Carry mode (forward-fill a time series per sensor)
engine.Execute("dataframe_fillna", new Dictionary<string, object?>
{
    ["dataset_id"] = "readings",
    ["into"] = "readings_filled",
    ["method"] = "ffill",
    ["order_by"] = new[] { "ts" },
    ["partition_by"] = new[] { "sensor_id" }
});
```

### dataframe_dropna

Remove rows containing NULL values.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `columns` (array, optional): Columns to check (default: all columns).
- `how` (string, optional, default `any`): `any` drops rows with any NULL in the checked columns; `all` drops rows where all checked columns are NULL.

```csharp
engine.Execute("dataframe_dropna", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["into"] = "sales_clean",
    ["columns"] = new[] { "amount", "region" },
    ["how"] = "any"
});
```

### dataframe_join

Join two datasets on one or more keys.

**Parameters:**
- `left` (string, required): Left dataset id.
- `right` (string, required): Right dataset id.
- `into` (string, required): Output dataset id.
- `how` (string, optional, default `inner`): one of `inner`, `left`, `right`, `full`, `semi`, `anti`, `cross`, `asof`.
- `on` (array, optional): Key column names present in both sides.
- `left_on` / `right_on` (arrays, optional): Use when key names differ; must be equal length.
- `asof_op` (string, optional, default `>=`): Inequality direction for `asof` joins (`>=` or `<=`). The last key is the as-of (inequality) column; any preceding keys are equality match columns.
- `suffix` (string, optional, default `_right`): Suffix for overlapping non-key columns.

```csharp
engine.Execute("dataframe_join", new Dictionary<string, object?>
{
    ["left"] = "orders",
    ["right"] = "customers",
    ["into"] = "orders_enriched",
    ["how"] = "left",
    ["on"] = new[] { "customer_id" }
});
```

### dataframe_group_by

Group rows and compute aggregates.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `group_by` (array, required): Grouping column names. May be empty `[]` for a grand total.
- `aggregations` (array, required): Each `{ "column", "function", "alias", "q"?, "column2"? }`.
  - `function` ∈ `count`, `count_distinct`, `approx_count_distinct`, `sum`, `product`, `avg`, `min`, `max`, `median`, `mode`, `stddev`, `stddev_pop`, `stddev_samp`, `var`, `var_pop`, `var_samp`, `bool_and`, `bool_or`, `first`, `last`, `list`, `quantile`, `approx_quantile`, `corr`, `covar`, `arg_min`, `arg_max`.
  - For `count` over all rows, use `column: "*"`.
  - `stddev`/`var` are the sample variants (aliases of `stddev_samp`/`var_samp`); use the explicit `_pop`/`_samp` forms when the distinction matters. `bool_and`/`bool_or` reduce a boolean column. `approx_count_distinct` (HyperLogLog) is a fast cardinality estimate.
  - `quantile` and `approx_quantile` require `q` in `[0, 1]`; they render as `quantile_cont(column, q)` and `approx_quantile(column, q)` (the latter trades exactness for speed on large inputs).
  - `corr` and `covar` require `column2`; they render as `corr(column, column2)` and `covar_samp(column, column2)`.
  - `arg_min` and `arg_max` require `column2`: they return `column`'s value in the row where `column2` is minimal / maximal (e.g. the product with the highest revenue).
- `having` (object, optional): A [predicate tree](#predicate-trees) applied to the aggregated result, equivalent to SQL `HAVING`. Columns referenced in `having` must be either `group_by` keys or aggregate `alias`es; otherwise the operation returns `INVALID_PREDICATE`.

```csharp
engine.Execute("dataframe_group_by", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["group_by"] = new[] { "region", "category" },
    ["aggregations"] = new object[]
    {
        new Dictionary<string, object?> { ["column"]="amount", ["function"]="sum", ["alias"]="revenue" },
        new Dictionary<string, object?> { ["column"]="*",      ["function"]="count", ["alias"]="orders" },
        new Dictionary<string, object?> { ["column"]="amount", ["function"]="quantile", ["q"]=0.95, ["alias"]="p95" },
        new Dictionary<string, object?> { ["column"]="x", ["function"]="corr", ["column2"]="y", ["alias"]="corr_xy" }
    },
    ["having"] = new Dictionary<string, object?> { ["column"]="revenue", ["op"]="gt", ["value"]=100 }
});
```

### dataframe_window

Apply window functions without collapsing rows.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `functions` (array, required): Each `{ "function", "column"?, "alias", "args"? }`.
  - `function` ∈ `row_number`, `rank`, `dense_rank`, `percent_rank`, `ntile`, `lag`, `lead`, `first_value`, `last_value`, `sum`, `avg`, `min`, `max`, `count`.
  - Rank functions (`row_number`, `rank`, `dense_rank`, `percent_rank`) ignore `column`.
  - `ntile` requires `args: [N]` (number of buckets); it ignores `column`.
  - `lag`/`lead` use `args: [offset]` (default 1).
  - `first_value`/`last_value` and aggregate window functions require `column`.
- `partition_by` (array, optional): Partition columns.
- `order_by` (array, optional): `{ "column", "direction", "nulls" }` entries.
  - `direction` ∈ `asc`, `desc` (default `asc`).
  - `nulls` ∈ `first`, `last`. When omitted, DuckDB's default null ordering applies.
- `frame` (object, optional): `{ "start", "end" }` for running/rolling aggregates. Each bound may be a string token (`unbounded_preceding`, `current_row`, `unbounded_following`) or a numeric offset object (`{ "preceding": N }`, `{ "following": N }`).

```csharp
engine.Execute("dataframe_window", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["functions"] = new object[]
    {
        new Dictionary<string, object?> { ["function"]="rank", ["alias"]="rank_in_region" },
        new Dictionary<string, object?> { ["function"]="ntile", ["args"]=new object[] { 4 }, ["alias"]="quartile" },
        new Dictionary<string, object?> { ["function"]="sum", ["column"]="amount", ["alias"]="rolling_sum" }
    },
    ["partition_by"] = new[] { "region" },
    ["order_by"] = new object[]
    {
        new Dictionary<string, object?> { ["column"]="amount", ["direction"]="desc" }
    },
    ["frame"] = new Dictionary<string, object?> { ["start"]="unbounded_preceding", ["end"]="current_row" }
});
```

### dataframe_pivot

Reshape long → wide.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `index` (array, required): Columns that remain rows.
- `columns` (string, required): Column whose distinct values become new columns.
- `values` (string, required): Column to aggregate into the new cells. May also be an array of
  `{ column, aggregation, alias? }` objects to compute multiple measures at once.
- `aggregation` (string, optional, default `sum`): Aggregate used with scalar `values`
  (`sum`, `avg`, `min`, `max`, `count`). Ignored when `values` is an array.

```csharp
engine.Execute("dataframe_pivot", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["index"] = new[] { "region" },
    ["columns"] = "month",
    ["values"] = new[]
    {
        new Dictionary<string, object?> { ["column"] = "amount", ["aggregation"] = "sum", ["alias"] = "total" },
        new Dictionary<string, object?> { ["column"] = "amount", ["aggregation"] = "avg", ["alias"] = "avg_amt" }
    }
});
```

### dataframe_unpivot

Reshape wide → long. `id_columns` stay as rows; `value_columns` are stacked into `{name_to, value_to}` pairs.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `id_columns` (array, required): Columns to keep as row identifiers (may be empty `[]`).
- `value_columns` (array, required): Columns to unpivot.
- `name_to` (string, optional, default `name`): Output column for the former value-column name.
- `value_to` (string, optional, default `value`): Output column for the value.

```csharp
engine.Execute("dataframe_unpivot", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["id_columns"] = new[] { "region" },
    ["value_columns"] = new[] { "Q1", "Q2", "Q3", "Q4" },
    ["name_to"] = "quarter",
    ["value_to"] = "revenue"
});
```

### dataframe_unnest

Explode a LIST column into one row per element. Other columns are replicated for each element.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `column` (string, required): LIST column to explode.

```csharp
engine.Execute("dataframe_unnest", new Dictionary<string, object?>
{
    ["dataset_id"] = "orders",
    ["column"] = "items"
});
```

### dataframe_sort

Order rows deterministically.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `by` (array, required): `{ "column", "direction", "nulls" }` entries.
  - `direction` ∈ `asc`, `desc` (default `asc`).
  - `nulls` ∈ `first`, `last` (default `last`).
- `limit` (integer, optional, `>= 1`): Keep only the first N rows after sorting (top-N).

Ties are broken by the order of `by` keys; the result ordering is fully specified and stable — see [reliability.md](reliability.md#explicit-ordering-type-and-null-handling).

### dataframe_distinct

Remove duplicate rows.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `columns` (array, optional): Columns to dedupe on (default: all columns).
- `keep` (string, optional, default `first`): `first` or `last` — which row within each duplicate
  group survives under the supplied `order_by`. `first` keeps the earliest row in that ordering;
  `last` keeps the latest. Requires `order_by` to be meaningful.
- `order_by` (array, optional): Array of `{ column, direction }` defining the ordering within each
  duplicate group; `keep` then selects the first or last row in that ordering. For example,
  `direction: asc` with `keep: last` keeps the row with the largest value of that column.

> **Note on `keep` and ordering direction.** `keep` selects which row in the stated `order_by` order
> survives; it does not change the meaning of your `direction`. Internally `keep=last` flips the scan
> direction of each `order_by` key so the underlying `DISTINCT ON` retains the last row of the group —
> this is an implementation detail. The directions you pass always describe the logical ordering, not
> the scan order, so `direction: asc` + `keep: last` always means "keep the largest".

### dataframe_union

Concatenate two or more schema-compatible datasets.

**Parameters:**
- `datasets` (array of strings, required): Dataset ids to concatenate, in order.
- `into` (string, required).
- `by_name` (boolean, optional, default `false`): Align columns by name rather than position.
- `distinct` (boolean, optional, default `false`): Drop duplicate rows across the union.
- `explain` (boolean, optional, default `false`): Include the DuckDB query plan in `r.Stats.Plan`.

### dataframe_sample

Materialize a deterministic reservoir sample of a dataset.

**Parameters:**
- `dataset_id` (string, required), `into` (string, optional).
- `n` (integer, required): Maximum rows to keep (>= 1).
- `seed` (integer, required): Deterministic seed for repeatable sampling.

```csharp
engine.Execute("dataframe_sample", new Dictionary<string, object?>
{
    ["dataset_id"] = "events",
    ["n"] = 1000,
    ["seed"] = 42
});
```

---

## Output & Management

### dataframe_export

Write a dataset to disk. The only operation that writes files. When the operation (or the engine) is
constructed with an `IPathPolicy`, the target path must be permitted by that policy, otherwise the
operation returns `PERMISSION_DENIED` (see [security.md](security.md)).

**Parameters:**
- `dataset_id` (string, required).
- `path` (string, required): Output file or directory.
- `format` (string, required): `csv`, `parquet`, `json`, or `delta`. `delta` writes a Delta table
  (Parquet data files plus a hand-written `_delta_log/`); the DuckDB `delta` extension is
  read-only, so writes are done without it.
- `mode` (string, optional, default `error`): `error` (fail if target exists), `append` (Delta only;
  add a new commit to an existing table), or `overwrite` (replace the target atomically).
- `partition_by` (array, optional): Partition columns (Parquet and Delta).
- `compression` (string, optional): e.g. `snappy`, `zstd` for Parquet or JSON.
- `header` (boolean, optional, default `true`): Write a header row (CSV only).
- `delimiter` (string, optional, default `,`): Field delimiter — exactly one character (CSV only).
- `quote` (string, optional, default `"`): Quote character — exactly one character (CSV only).
- `escape` (string, optional, default `"`): Escape character — exactly one character (CSV only).
- `array` (boolean, optional, default `false`): JSON only. When `false` the output is
  newline-delimited JSON (NDJSON); when `true` the output is a single top-level JSON array of
  objects.

CSV options are rejected for `parquet`, `json`, and `delta` formats with `INVALID_ARGUMENT`, and each
`delimiter`/`quote`/`escape` value must be exactly one character. `array` is rejected for non-JSON
formats. When the target exists and `mode` is not `overwrite` (nor `append` for Delta), the operation
returns `TARGET_EXISTS`.

> **Delta concurrency.** Concurrent `append` exports to the same Delta table path are serialized per
> path, so `_delta_log/` commits are emitted sequentially and concurrent writers cannot corrupt the
> transaction log. Appends to different Delta tables do not block each other. This locking is
> process-scoped; separate processes still require external coordination.

```csharp
var r = engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "orders_enriched",
    ["path"] = "out/orders/",
    ["format"] = "parquet",
    ["partition_by"] = new[] { "region" },
    ["compression"] = "zstd"
});
if (!r.Success) Console.WriteLine($"[{r.ErrorCode}] {r.Message}");
```

### dataframe_list

List datasets registered in the current session.

**Parameters:** none.

**Returns:** envelope where `r.PreviewRows` holds `{ "dataset_id", "row_count", "column_count", "source" }` per dataset. The envelope's `r.DatasetId` is the literal `session` — this overview refers to the whole session, not a single dataset.

### dataframe_drop

Release a dataset and free its resources.

**Parameters:** `dataset_id` (string, required).

---

## Predicate trees

`dataframe_filter` takes a tree of **condition** and **logical** nodes. No strings are concatenated into a query — the vocabulary is enumerated and validated. (The model behind this grammar is `PredicateNode` / `PredicateParser` in the `Andy.Data.Predicates` namespace.)

**Condition node:**
```json
{ "column": "amount", "op": "gte", "value": 100 }
```

Comparison ops: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`.
Set/range ops: `in` (`"values": [...]`), `between` (`"low"`, `"high"`).
Null ops: `is_null`, `is_not_null` (no `value`).
Text ops: `like`, `ilike`, `starts_with`, `ends_with`, `contains`, `matches` (`value` is the pattern/substring; `matches` uses regular expressions).

A condition can compare two columns by using `value_column` instead of `value`:
```json
{ "column": "updated_at", "op": "gt", "value_column": "created_at" }
```

**Logical node:**
```json
{ "op": "and", "conditions": [ <node>, <node>, ... ] }
```
Logical ops: `and`, `or` (n-ary), `not` (single `condition`).

Nodes nest arbitrarily:
```json
{
  "op": "and",
  "conditions": [
    { "column": "status", "op": "eq", "value": "completed" },
    { "op": "or", "conditions": [
      { "column": "region", "op": "in", "values": ["EMEA", "APAC"] },
      { "column": "amount", "op": "gt", "value": 1000 }
    ]}
  ]
}
```

## Expression trees

`dataframe_with_column` takes an expression tree of **leaves** (`{ "column": "x" }` or `{ "literal": 42 }`) and **operator nodes** (`{ "op": "...", "args": [ ... ] }`). (The model behind this grammar is `ExprNode` / `ExpressionParser` in the `Andy.Data.Expressions` namespace.)

Arithmetic: `add`, `subtract`, `multiply`, `divide`, `modulo`, `round`, `abs`, `floor`, `ceil`, `power`, `ln`.
String: `concat`, `upper`, `lower`, `trim`, `substring`, `length`, `replace`, `split_part`, `lpad`, `rpad`, `regexp_replace`, `regexp_extract`, `regexp_matches`.
Conditional: `coalesce`, `nullif`, `greatest`, `least`, `clip`, `case`. (`clip(x, lo, hi)` bounds `x` into `[lo, hi]`; `greatest`/`least` take two or more args.)
Cast: `{ "op": "cast", "to": "DOUBLE", "args": [ <expr> ] }` and `{ "op": "try_cast", ... }` for safe casts that return NULL on failure.
Temporal: `date_trunc`, `date_part`, `date_diff`, `strptime`, `date_add`.
List: `list_length(expr)`, `list_contains(expr, value)`, `array(value1, value2, ...)`.
Struct access: `{ "op": "struct_field", "field": "name", "args": [ <expr> ] }` extracts a field from a `STRUCT` expression (e.g. a nested JSON object loaded by `dataframe_load_json`). Nesting is supported.
Misc: `hash`.

`case` uses the predicate grammar for its `when` branches:
```json
{
  "op": "case",
  "when": [
    { "predicate": { "column": "amount", "op": "gte", "value": 1000 }, "then": { "literal": "high" } },
    { "predicate": { "column": "amount", "op": "gte", "value": 100 }, "then": { "literal": "medium" } }
  ],
  "else": { "literal": "low" }
}
```

`strptime(expr, format)` parses strings to timestamps. `format` is a literal drawn from a closed (but broad) vocabulary covering the common date, date-time, and time layouts — ISO (`%Y-%m-%d`, `%Y-%m-%dT%H:%M:%S`, with optional `.%f`/`Z`), slash/dot/dash variants in both `m/d/Y` and `d/m/Y` order (e.g. `%m/%d/%Y`, `%d.%m.%Y`), month-name forms (`%d %b %Y`, `%B %d, %Y`), compact (`%Y%m%d`, `%Y%m%d%H%M%S`), and time-only (`%H:%M`, `%H:%M:%S`). A format outside the vocabulary returns `INVALID_PREDICATE`.

`date_add(unit, n, expr)` adds `n` units to a date/timestamp. `unit` is a literal from `year`, `month`, `day`, `hour`, `minute`, `second` (and their plurals).

The function set is fixed and validated; there is no path from caller input to executed code. See [security.md](security.md#injection-free-design).

## Error codes

All operations share the error contract documented in [tool-contract.md](tool-contract.md#failure-fields). On failure, `r.Success` is `false` and `r.ErrorCode` carries one of the stable codes from `DataFrameErrorCodes` (namespace `Andy.Data`); `r.Message` explains, and `r.Details` may carry structured context. The common codes:

| Code (`DataFrameErrorCodes`) | Value | Meaning |
|------|-------|---------|
| `DatasetNotFound` | `DATASET_NOT_FOUND` | Referenced `dataset_id` is not registered |
| `ColumnNotFound` | `COLUMN_NOT_FOUND` | A referenced column is not in the dataset schema |
| `InvalidType` | `INVALID_TYPE` | Operation/operator not valid for a column's type |
| `InvalidArgument` | `INVALID_ARGUMENT` | A parameter value is malformed or out of vocabulary |
| `InvalidAggregation` | `INVALID_AGGREGATION` | Unknown or misapplied aggregate function |
| `InvalidPredicate` | `INVALID_PREDICATE` | Malformed predicate or expression tree |
| `SchemaMismatch` | `SCHEMA_MISMATCH` | Union/join inputs are not compatible |
| `FileNotFound` | `FILE_NOT_FOUND` | Source path does not exist |
| `PermissionDenied` | `PERMISSION_DENIED` | Path outside the policy allow-list, or missing permission |
| `TargetExists` | `TARGET_EXISTS` | Export target exists and `mode` is not `overwrite` |
| `Cancelled` | `CANCELLED` | Operation cancelled by caller or exceeded `MaxExecutionTimeMs` |
| `BackendError` | `BACKEND_ERROR` | DuckDB-level execution error (message included) |

Branch on the code, not the message:

```csharp
var r = engine.Execute("dataframe_filter", parameters);
if (!r.Success)
{
    switch (r.ErrorCode)
    {
        case DataFrameErrorCodes.DatasetNotFound: /* load it first */ break;
        case DataFrameErrorCodes.InvalidPredicate: /* fix the predicate tree */ break;
        default: throw new InvalidOperationException($"[{r.ErrorCode}] {r.Message}");
    }
}
```

See also: [getting-started.md](getting-started.md), [core-concepts.md](core-concepts.md), [architecture.md](architecture.md), [tool-contract.md](tool-contract.md), [reliability.md](reliability.md), [security.md](security.md), [troubleshooting.md](troubleshooting.md).
