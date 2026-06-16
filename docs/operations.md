# Operations reference

All 28 operations, grouped by category. Every operation is dispatched through the engine by id and
returns the [standard response envelope](tool-contract.md):

```csharp
var response = engine.Execute("dataframe_<op>", parameters, options);
```

Conventions used below:

- **Required** parameters are marked •; everything else is optional with its default shown.
- `dataset_id` and `into` ids must match `^[A-Za-z_][A-Za-z0-9_]{0,127}$`.
- Most transforms accept `into` (the output id) and default to **replacing** `dataset_id` in place
  when it is omitted. `join`, `union`, and `rename` **require** `into`.
- `explain` (boolean, default `false`) is available on every transform; when `true` the DuckDB query
  plan is returned in `stats.plan`.
- Remember the [lazy-view model](concepts.md#lazy-views-read-this-for-performance): transforms compose
  into one fused plan and are not materialized until a terminal (count/preview/export) runs.

**Categories**

- [Loading](#loading) — `load_csv`, `load_json`, `load_parquet`, `load_delta`
- [Inspection](#inspection) — `schema`, `preview`, `profile`, `value_counts`, `assert`, `list`
- [Projection & row selection](#projection--row-selection) — `select`, `filter`, `with_column`, `rename`
- [Aggregation & analytics](#aggregation--analytics) — `group_by`, `window`
- [Reshaping](#reshaping) — `pivot`, `unpivot`, `unnest`
- [Combining](#combining) — `join`, `union`
- [Ordering, sampling & dedup](#ordering-sampling--dedup) — `sort`, `sample`, `distinct`
- [Missing data](#missing-data) — `fillna`, `dropna`
- [Output & lifecycle](#output--lifecycle) — `export`, `drop`

Grammars: [predicate trees](#predicate-trees) · [expression trees](#expression-trees) ·
[aggregation functions](#aggregation-functions) · [window functions](#window-functions)

---

## Loading

The loaders register a new session-scoped dataset from a file or glob. Format-specific options (CSV
dialect, JSON layout, Parquet partitioning, Delta time travel) are detailed in
[file-formats.md](file-formats.md). Loads honor the [path policy](concepts.md#path-policy) and the
[memory limit](concepts.md#resource-governance--cancellation).

### `dataframe_load_csv`

Loads a CSV file (or glob such as `data/*.csv`) into a named dataset. Column types are inferred by
sampling, unless overridden per column.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `path` | string | • | | CSV file or glob. |
| `dataset_id` | string | • | | Id to register under. |
| `header` | boolean | | auto-detect | Whether row 1 holds column names. |
| `delimiter` | string | | auto-detect | Field delimiter. |
| `quote` | string | | auto-detect | Quote character (exactly one). |
| `null_string` | string | | | Token to read as NULL (e.g. `"NA"`). |
| `columns` | object | | inferred | `{ "amount": "DECIMAL(12,2)" }` type overrides (DuckDB type names). |
| `sample_size` | integer | | `20480` | Rows sampled for type inference; `-1` reads the whole file. |

```csharp
engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales/*.csv", ["dataset_id"] = "sales",
    ["null_string"] = "NA",
    ["columns"] = new Dictionary<string, object?> { ["amount"] = "DECIMAL(12,2)" },
});
```

### `dataframe_load_json`

Loads a JSON file (or glob) — newline-delimited JSON (NDJSON) or a top-level array of objects. Schema
and types are inferred from the values.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `path` | string | • | | JSON file or glob (e.g. `data/*.ndjson`). |
| `dataset_id` | string | • | | Id to register under. |
| `format` | string | | `auto` | `auto`, `newline_delimited`, or `array`. |

### `dataframe_load_parquet`

Loads a Parquet file, a glob, or a Hive-partitioned directory glob. Schema and types come from the
file metadata (no inference). This is the **fastest source** — see [benchmarks](benchmarks.md).

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `path` | string | • | | File, glob, or partitioned-dir glob (e.g. `events/**/*.parquet`). |
| `dataset_id` | string | • | | Id to register under. |
| `hive_partitioning` | boolean | | auto | Expose `key=value/` dirs as partition columns. |
| `union_by_name` | boolean | | `false` | Align columns by name across files with differing schemas. |

### `dataframe_load_delta`

Loads a Delta Lake table. With no version/timestamp it reads the latest snapshot via the DuckDB delta
extension; supplying `version` or `timestamp` performs **time travel** by replaying the transaction
log. See [Delta details & limitations](file-formats.md#delta-lake).

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `path` | string | • | | Delta table root directory. |
| `dataset_id` | string | • | | Id to register under. |
| `version` | integer | | latest | Load this snapshot version. Mutually exclusive with `timestamp`. |
| `timestamp` | string | | latest | Load the latest version at/before this ISO-8601 instant. Mutually exclusive with `version`. |

---

## Inspection

These read metadata or produce a report; most do **not** register a new dataset (the exceptions are
`value_counts`, which does, and the report-style ops whose rows land in `preview_rows`).

### `dataframe_schema`

Returns the column names, types, and nullability of a dataset **without scanning data**.

| Param | Type | | Notes |
|-------|------|--|-------|
| `dataset_id` | string | • | Dataset to describe. |

### `dataframe_preview`

Returns a bounded set of rows: the first (`head`), last (`tail`), or a random `sample`.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Dataset to preview. |
| `mode` | string | | `head` | `head`, `tail`, or `sample`. |
| `limit` | integer | | `50` | Rows to return, `1..1000`. |
| `seed` | integer | | | **Required when `mode=sample`**; makes sampling repeatable. |

### `dataframe_profile`

A `pandas.describe()`-style per-column summary: `null_count`, `distinct_count`, `count` (non-null),
`min`, `max`, and — for numeric columns — `mean`, `std` (sample), and quantiles. One stats row per
column lands in `preview_rows`.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Dataset to profile. |
| `columns` | array | | all | Subset of columns. |
| `quantiles` | array | | `[0.25,0.5,0.75]` | Quantiles in `[0,1]` for numeric columns. |

### `dataframe_value_counts`

Counts how often each distinct value of `column` occurs, returning `{ <column>, count, proportion }`
ordered by count descending (ties broken by value ascending, for determinism). Registers the result.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input dataset. |
| `column` | string | • | | Column to count. |
| `into` | string | | replaces input | Output id. |
| `limit` | integer | | all | Keep the top-N most frequent values. |
| `dropna` | boolean | | `true` | Exclude NULLs (matches pandas). |

### `dataframe_assert`

Evaluates data-quality expectations and returns a per-expectation pass/fail report (it does **not**
modify or register a dataset). `preview_rows` holds one row per expectation
(`expectation, column, passed, violations, details`); a warning summarizes any failures so a caller
can branch on data quality.

| Param | Type | | Notes |
|-------|------|--|-------|
| `dataset_id` | string | • | Dataset to check. |
| `expectations` | array | • | Array of `{ type, ... }` specs (see below). |

Expectation `type` values:

| type | extra fields | passes when |
|------|--------------|-------------|
| `not_null` | `column` | no NULLs in the column |
| `unique` | `column` | all values distinct |
| `in_range` | `column`, `min?`, `max?` | every value within `[min, max]` |
| `in_set` | `column`, `values[]` | every value is in the set |
| `matches` | `column`, `pattern` | every value matches the regex |
| `row_count` | `min?`, `max?`, `equals?` | the row count satisfies the bound(s) |

```csharp
engine.Execute("dataframe_assert", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["expectations"] = new object[]
    {
        new Dictionary<string, object?> { ["type"] = "not_null", ["column"] = "id" },
        new Dictionary<string, object?> { ["type"] = "in_range", ["column"] = "amount", ["min"] = 0 },
        new Dictionary<string, object?> { ["type"] = "row_count", ["min"] = 1 },
    },
});
```

### `dataframe_list`

Lists the datasets registered in the session. `preview_rows` holds one row per dataset
(`dataset_id, row_count, column_count, source`). The envelope's top-level `dataset_id` is the literal
`"session"`. Takes no parameters.

---

## Projection & row selection

### `dataframe_select`

Projects, renames, and reorders columns. `columns` entries are either a column name (string) or
`{ column, as }` to rename.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `columns` | array | • | | Names or `{ column, as }` objects. |
| `into` | string | | replaces input | Output id. |

```csharp
["columns"] = new object[]
{
    "id",
    new Dictionary<string, object?> { ["column"] = "amount", ["as"] = "amount_usd" },
}
```

### `dataframe_filter`

Selects rows matching a structured [predicate tree](#predicate-trees) — never SQL text.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `predicate` | object | • | | A predicate tree (see [grammar](#predicate-trees)). |
| `into` | string | | replaces input | Output id. |

### `dataframe_with_column`

Adds or replaces a single column computed from a structured [expression tree](#expression-trees) —
never SQL text.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `name` | string | • | | New or replaced column name. |
| `expression` | object | • | | An expression tree (see [grammar](#expression-trees)). |
| `into` | string | | replaces input | Output id. |

### `dataframe_rename`

Renames one or more columns; unmentioned columns are kept and order is preserved. **Requires `into`.**

| Param | Type | | Notes |
|-------|------|--|-------|
| `dataset_id` | string | • | Input. |
| `into` | string | • | Output id. |
| `columns` | object | • | Map of old name → new name. |

---

## Aggregation & analytics

### `dataframe_group_by`

Groups by zero or more columns and computes aggregates. `group_by` may be empty for a grand total.
Each aggregation is `{ column, function, alias, q?, column2? }`. Use column `"*"` with `count` for a
row count. An optional `having` [predicate tree](#predicate-trees) filters the aggregated result; its
columns must be group keys or declared aggregate aliases.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `group_by` | array | • | | Grouping columns (may be empty). |
| `aggregations` | array | • | | Aggregate specs (see [functions](#aggregation-functions)). |
| `into` | string | | replaces input | Output id. |
| `having` | object | | | Predicate tree over the aggregated rows. |

```csharp
["group_by"] = new[] { "region", "category" },
["aggregations"] = new object[]
{
    new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "total" },
    new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "quantile", ["q"] = 0.95, ["alias"] = "p95" },
    new Dictionary<string, object?> { ["column"] = "*", ["function"] = "count", ["alias"] = "n" },
},
```

### `dataframe_window`

Adds window-function columns **without collapsing rows**. `functions` is an array of
`{ function, column?, alias, args? }`; `partition_by` and `order_by` (`{ column, direction, nulls }`)
define the window, with an optional `frame`.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `functions` | array | • | | Window-function specs (see [functions](#window-functions)). |
| `into` | string | | replaces input | Output id. |
| `partition_by` | array | | none | Partition columns. |
| `order_by` | array | | none | `{ column, direction (asc\|desc), nulls (first\|last) }`. |
| `frame` | object | | | `{ start, end }`; each bound is `unbounded_preceding` / `current_row` / `unbounded_following` or `{ preceding: N }` / `{ following: N }`. |

```csharp
["functions"] = new object[]
{
    new Dictionary<string, object?> { ["function"] = "rank", ["alias"] = "rnk" },
    new Dictionary<string, object?> { ["function"] = "sum", ["column"] = "amount", ["alias"] = "running_total" },
},
["partition_by"] = new[] { "region" },
["order_by"] = new object[] { new Dictionary<string, object?> { ["column"] = "ts", ["direction"] = "asc" } },
["frame"] = new Dictionary<string, object?> { ["start"] = "unbounded_preceding", ["end"] = "current_row" },
```

---

## Reshaping

### `dataframe_pivot`

Long → wide. `index` columns remain rows; the distinct values of the `columns` column become new
columns. `values` is either a single column name with `aggregation` (default `sum`), or an array of
`{ column, aggregation, alias? }`.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `index` | array | • | | Columns that remain rows. |
| `columns` | string | • | | Column whose distinct values become new columns. |
| `values` | string/array | • | | A column name, or `{ column, aggregation, alias? }` objects. |
| `into` | string | | replaces input | Output id. |
| `aggregation` | string | | `sum` | `sum`/`avg`/`min`/`max`/`count`; scalar-`values` form only. |

### `dataframe_unpivot`

Wide → long. `id_columns` are kept as row identifiers; `value_columns` are stacked into a name column
(`name_to`, default `name`) and a value column (`value_to`, default `value`).

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `id_columns` | array | • | | Kept as identifiers (may be empty). |
| `value_columns` | array | • | | Columns to stack into rows. |
| `into` | string | | replaces input | Output id. |
| `name_to` | string | | `name` | Output name column. |
| `value_to` | string | | `value` | Output value column. |

### `dataframe_unnest`

Explodes a `LIST` column so each element becomes its own row; other columns are replicated.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `column` | string | • | | LIST column to explode. |
| `into` | string | | replaces input | Output id. |

---

## Combining

### `dataframe_join`

Joins two datasets into `into`. **Requires `into`.** Provide `on` (keys in both sides) or
`left_on`/`right_on` (equal length). For `asof`, the last key is the inequality column and any
preceding keys are equality matches.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `left` | string | • | | Left dataset id. |
| `right` | string | • | | Right dataset id. |
| `into` | string | • | | Output id. |
| `how` | string | | `inner` | `inner`/`left`/`right`/`full`/`semi`/`anti`/`cross`/`asof`. |
| `on` | array | | | Key columns present in both sides. |
| `left_on` / `right_on` | array | | | Equal-length key lists (alternative to `on`). |
| `asof_op` | string | | `>=` | `>=` or `<=`; the as-of inequality when `how=asof`. |
| `suffix` | string | | `_right` | Suffix for overlapping non-key right columns. |

### `dataframe_union`

Concatenates two or more datasets into `into`. **Requires `into`.**

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `datasets` | array | • | | Ordered ids to concatenate (≥ 2). |
| `into` | string | • | | Output id. |
| `by_name` | boolean | | `false` | Align columns by name (else by position). |
| `distinct` | boolean | | `false` | Drop duplicate rows across the union. |

---

## Ordering, sampling & dedup

### `dataframe_sort`

Orders rows by one or more keys; ties break by key order. Optional `limit` keeps the first N (top-N).

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `by` | array | • | | `{ column, direction (asc\|desc), nulls (first\|last) }`. |
| `into` | string | | replaces input | Output id. |
| `limit` | integer | | all | Keep the first N rows after sorting. |

### `dataframe_sample`

Materializes a deterministic **reservoir sample**. Both `n` and `seed` are required; `seed` makes the
sample repeatable.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `n` | integer | • | | Reservoir size (≥ 1). |
| `seed` | integer | • | | Deterministic seed. |
| `into` | string | | replaces input | Output id. |

### `dataframe_distinct`

Removes duplicate rows. With no `columns`, dedupes whole rows. With `columns`, keeps one row per
distinct combination; `keep` (`first`/`last`) plus `order_by` decide which survives.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `into` | string | | replaces input | Output id. |
| `columns` | array | | all | Columns to dedupe on. |
| `keep` | string | | `first` | `first`/`last` within each group, under `order_by`. |
| `order_by` | array | | | `{ column, direction (asc\|desc) }` defining the within-group order. |

---

## Missing data

### `dataframe_fillna`

Replaces NULLs. **Scalar mode:** provide a global `value` and/or a per-column `values` map (at least
one). **Carry mode:** set `method` to `ffill`/`bfill` along an `order_by` ordering, optionally within
`partition_by` groups (the pandas ffill/bfill equivalent). `method` cannot combine with
`value`/`values`.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `into` | string | | replaces input | Output id. |
| `value` | string | | | Scalar mode: global replacement, coerced to each column's type. |
| `values` | object | | | Scalar mode: per-column overrides. |
| `method` | string | | | Carry mode: `ffill` or `bfill` (requires `order_by`). |
| `order_by` | array | | | Ordering for `method`. |
| `partition_by` | array | | | Carry-mode groups; the fill restarts per group. |
| `columns` | array | | all | Carry-mode subset to fill. |

### `dataframe_dropna`

Removes rows with NULLs. `columns` restricts the check; `how` drops when `any` (default) or `all` of
the checked columns are NULL.

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Input. |
| `into` | string | | replaces input | Output id. |
| `columns` | array | | all | Columns to check. |
| `how` | string | | `any` | `any` or `all`. |

---

## Output & lifecycle

### `dataframe_export`

Writes a dataset to disk. **`mode=error`** fails if the target exists; **`overwrite`** replaces it
atomically; **`append`** adds a commit (Delta only). Honors the [path policy](concepts.md#path-policy)
for writes. Format-specific options are in [file-formats.md](file-formats.md).

| Param | Type | | Default | Notes |
|-------|------|--|---------|-------|
| `dataset_id` | string | • | | Dataset to export. |
| `path` | string | • | | Output file or directory. |
| `format` | string | • | | `csv`, `parquet`, `json`, or `delta`. |
| `mode` | string | | `error` | `error`, `append` (Delta), or `overwrite`. |
| `partition_by` | array | | | Partition columns (Parquet and Delta). |
| `compression` | string | | | Codec, e.g. `snappy`, `zstd`, `gzip` (Parquet/JSON). |
| `array` | boolean | | `false` | JSON: write a top-level array (else NDJSON). |
| `header` | boolean | | `true` | CSV: write a header row. |
| `delimiter` | string | | `,` | CSV field delimiter. |
| `quote` | string | | `"` | CSV quote character. |
| `escape` | string | | `"` | CSV escape character. |

### `dataframe_drop`

Releases a dataset; its backend resources are freed once no remaining dataset depends on them.

| Param | Type | | Notes |
|-------|------|--|-------|
| `dataset_id` | string | • | Dataset to release. |

---

## Predicate trees

The structured predicate language used by `dataframe_filter` and the `having` clause of
`dataframe_group_by`. It is an **enumerated, closed vocabulary** — there is no path from input to
executed SQL (see [no injection surface](concepts.md#no-injection-surface)). Parsed by
[`PredicateParser`](../src/Andy.Data.Abstractions/Predicates/PredicateParser.cs); malformed input
returns `INVALID_PREDICATE`.

A node is either a **condition** (has a `column`) or a **logical** node (has an `op` but no `column`).

**Condition node** — `{ "column", "op", <operand> }`:

| `op` | operand | meaning |
|------|---------|---------|
| `eq` `neq` `gt` `gte` `lt` `lte` | `value` *or* `value_column` | comparison against a literal or another column |
| `in` | `values: [...]` (non-empty) | membership |
| `between` | `low`, `high` | inclusive range |
| `is_null` `is_not_null` | — | null test |
| `like` `ilike` | `value` | SQL `LIKE` / case-insensitive `LIKE` |
| `starts_with` `ends_with` `contains` | `value` | substring tests |
| `matches` | `value` | regular-expression match |

Comparison and text operators take **either** a `value` (literal) **or** a `value_column` (compare to
another column), not both.

**Logical nodes:**

- `{ "op": "and"|"or", "conditions": [ <node>, ... ] }` — n-ary (one or more children).
- `{ "op": "not", "condition": <node> }` — negation of one child.

```jsonc
{
  "op": "and",
  "conditions": [
    { "column": "amount", "op": "between", "low": 100, "high": 1000 },
    { "op": "or", "conditions": [
      { "column": "region", "op": "in", "values": ["EU", "US"] },
      { "column": "vip", "op": "eq", "value": true }
    ]},
    { "op": "not", "condition": { "column": "status", "op": "eq", "value": "void" } }
  ]
}
```

In C#, build the tree with nested `Dictionary<string, object?>` and `object[]`:

```csharp
["predicate"] = new Dictionary<string, object?>
{
    ["op"] = "and",
    ["conditions"] = new object[]
    {
        new Dictionary<string, object?> { ["column"] = "amount", ["op"] = "gte", ["value"] = 100 },
        new Dictionary<string, object?> { ["column"] = "region", ["op"] = "in", ["values"] = new[] { "EU", "US" } },
    },
}
```

## Expression trees

The structured expression language used by `dataframe_with_column`. Closed vocabulary, parsed by
[`ExpressionParser`](../src/Andy.Data.Abstractions/Expressions/ExpressionParser.cs); malformed input
returns `INVALID_PREDICATE`.

**Leaves:**

- `{ "column": "name" }` — a column reference.
- `{ "literal": <value> }` — a constant.

**Operator node** — `{ "op": "<fn>", "args": [ <expr>, ... ] }`. Argument counts are validated:

| Group | operators (arg count) |
|-------|------------------------|
| Arithmetic | `add` (≥2), `subtract` (2), `multiply` (≥2), `divide` (2), `modulo` (2), `round` (1–2), `abs` (1), `floor` (1), `ceil` (1), `power` (2), `ln` (1) |
| String | `concat` (≥2), `upper` (1), `lower` (1), `trim` (1), `substring` (2–3), `length` (1), `replace` (3), `split_part` (3), `lpad` (2–3), `rpad` (2–3), `regexp_replace` (3), `regexp_extract` (2–3), `regexp_matches` (2) |
| Conditional | `coalesce` (≥1), `nullif` (2), `greatest` (≥2), `least` (≥2), `clip` (3) |
| Temporal | `date_trunc` (2), `date_part` (2), `date_diff` (3), `strptime` (2), `date_add` (3) |
| List | `list_length` (1), `list_contains` (2), `array` (≥1) |
| Misc | `hash` (1) |

**Special nodes:**

- `{ "op": "cast", "to": "<type>", "args": [ <expr> ] }` — cast (aborts on failure).
- `{ "op": "try_cast", "to": "<type>", "args": [ <expr> ] }` — safe cast (NULL on failure).
- `{ "op": "case", "when": [ { "predicate": <pred>, "then": <expr> }, ... ], "else": <expr>? }` —
  searched CASE; each `predicate` is a [predicate tree](#predicate-trees).
- `{ "op": "struct_field", "field": "name", "args": [ <expr> ] }` — access a STRUCT field.

```jsonc
// margin = round((revenue - cost) / revenue, 4)
{
  "op": "round",
  "args": [
    { "op": "divide", "args": [
      { "op": "subtract", "args": [ { "column": "revenue" }, { "column": "cost" } ] },
      { "column": "revenue" }
    ]},
    { "literal": 4 }
  ]
}
```

```jsonc
// bucket = CASE WHEN amount >= 1000 THEN 'big' ELSE 'small' END
{
  "op": "case",
  "when": [
    { "predicate": { "column": "amount", "op": "gte", "value": 1000 }, "then": { "literal": "big" } }
  ],
  "else": { "literal": "small" }
}
```

## Aggregation functions

Used in `dataframe_group_by` aggregation specs (`{ column, function, alias, q?, column2? }`):

| Function | Notes |
|----------|-------|
| `count` | Use column `"*"` for a row count. |
| `count_distinct`, `approx_count_distinct` | Exact / HyperLogLog distinct count. |
| `sum`, `product`, `avg`, `min`, `max` | |
| `median`, `mode` | |
| `stddev`, `stddev_pop`, `stddev_samp`, `var`, `var_pop`, `var_samp` | Dispersion. |
| `bool_and`, `bool_or` | Boolean aggregates. |
| `first`, `last`, `list` | First/last value; collect into a LIST. |
| `quantile`, `approx_quantile` | Require `q` in `[0,1]`. |
| `corr`, `covar` | Require `column2`. |
| `arg_min`, `arg_max` | Require `column2`: returns `column` for the row where `column2` is min/max. |

## Window functions

Used in `dataframe_window` function specs (`{ function, column?, alias, args? }`):

| Function | Notes |
|----------|-------|
| `row_number`, `rank`, `dense_rank`, `percent_rank` | Ranking (no `column`). |
| `ntile` | `args: [n]`. |
| `lag`, `lead` | Offset access; `column` plus optional offset in `args`. |
| `first_value`, `last_value` | Boundary values over the frame. |
| `sum`, `avg`, `min`, `max`, `count` | Aggregates over the window/frame. |

Define the window with `partition_by`, `order_by`, and an optional `frame` — see the
[`dataframe_window`](#dataframe_window) example.
