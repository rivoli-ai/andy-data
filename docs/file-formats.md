# File formats

`Andy.Data` reads and writes four formats: **CSV**, **JSON**, **Parquet** (including partitioned
Parquet), and **Delta Lake**. This page collects the format-specific load and export options and the
behaviors worth knowing for each. For the general operation contract see the
[operations reference](operations.md); all paths honor the [path policy](security.md#the-ipathpolicy-filesystem-gate).

A note that applies to every format: loads register a **lazy view over the file** (see
[lazy views](architecture.md#lazy-views-fold-a-chain-into-one-plan)). Columnar Parquet scans are pushed
down and are dramatically cheaper to re-read than CSV/JSON — if you load from CSV/JSON and then run a
multi-step pipeline, consider `export`-ing to Parquet once and continuing from there.

| | Load | Export | Partitioning | Notes |
|--|:----:|:------:|:------------:|-------|
| CSV | ✓ | ✓ | — | Dialect auto-detected on load; full dialect control on export. |
| JSON | ✓ | ✓ | — | NDJSON or array; auto-detected on load. |
| Parquet | ✓ | ✓ | ✓ (Hive) | Fastest format; glob and partition-pruning friendly. |
| Delta Lake | ✓ | ✓ | ✓ | Snapshot + time travel on load; atomic commits on export. |

---

## CSV

### Loading — [`dataframe_load_csv`](operations.md#dataframe_load_csv)

DuckDB auto-detects the dialect (header, delimiter, quote) and infers column types by sampling
`sample_size` rows (default 20480; `-1` reads the whole file). Override any of it:

```csharp
engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/sales/*.csv",      // single file or glob
    ["dataset_id"] = "sales",
    ["header"] = true,
    ["delimiter"] = ";",
    ["quote"] = "\"",
    ["null_string"] = "NA",             // token to read as NULL
    ["columns"] = new Dictionary<string, object?>  // per-column type overrides (DuckDB type names)
    {
        ["amount"] = "DECIMAL(12,2)",
        ["ts"] = "TIMESTAMP",
    },
    ["sample_size"] = -1,               // read the whole file for inference (accuracy over speed)
});
```

- **Type inference is a sampling trade-off.** If late rows widen a column's type (e.g. an integer
  column that later holds decimals), raise `sample_size` or pin the type via `columns`.
- CSV load carries a **fixed per-call cost** (dialect sniffing) — see the
  [benchmarks](benchmarks.md), where it dominates small loads.

### Exporting — [`dataframe_export`](operations.md#dataframe_export) with `format=csv`

```csharp
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "summary", ["path"] = "out/summary.csv", ["format"] = "csv",
    ["mode"] = "overwrite",
    ["header"] = true,        // default true
    ["delimiter"] = ",",      // default ,
    ["quote"] = "\"",         // default "
    ["escape"] = "\"",        // default "
    ["compression"] = "gzip", // optional
});
```

CSV does not support `partition_by`.

---

## JSON

### Loading — [`dataframe_load_json`](operations.md#dataframe_load_json)

Reads **newline-delimited JSON (NDJSON)** or a **top-level array of objects**. `format` is `auto`
(default — detects the layout), `newline_delimited`, or `array`. Schema and types are inferred from the
values.

```csharp
engine.Execute("dataframe_load_json", new Dictionary<string, object?>
{
    ["path"] = "data/events/*.ndjson", ["dataset_id"] = "events", ["format"] = "auto",
});
```

Nested objects become `STRUCT` columns and JSON arrays become `LIST` columns; reach into them with the
`struct_field` expression node and [`dataframe_unnest`](operations.md#dataframe_unnest) respectively.

### Exporting — `format=json`

```csharp
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "events", ["path"] = "out/events.ndjson", ["format"] = "json",
    ["mode"] = "overwrite",
    ["array"] = false,        // false = NDJSON (default); true = one top-level JSON array
    ["compression"] = "gzip", // optional
});
```

---

## Parquet

The recommended interchange and intermediate format: columnar, typed, compressed, and the cheapest to
re-scan.

### Loading — [`dataframe_load_parquet`](operations.md#dataframe_load_parquet)

Accepts a single file, a glob, or a **Hive-partitioned directory glob**. Schema and types come from
the file metadata — no inference step.

```csharp
// Single file or glob
engine.Execute("dataframe_load_parquet", new Dictionary<string, object?>
{
    ["path"] = "data/sales.parquet", ["dataset_id"] = "sales",
});

// Hive-partitioned tree: events/region=EU/dt=2024-01-01/part-0.parquet
engine.Execute("dataframe_load_parquet", new Dictionary<string, object?>
{
    ["path"] = "events/**/*.parquet",
    ["dataset_id"] = "events",
    ["hive_partitioning"] = true,   // expose region= / dt= as columns (default: auto-detect)
    ["union_by_name"] = true,       // align columns by name across files with differing schemas
});
```

- With `hive_partitioning`, the `key=value/` directory segments become real columns you can filter on,
  and predicate push-down enables **partition pruning** (files for non-matching partitions are
  skipped).
- `union_by_name` lets you load an evolving schema where newer files have extra columns.

### Exporting — `format=parquet`

```csharp
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "events", ["path"] = "out/events", ["format"] = "parquet",
    ["mode"] = "overwrite",
    ["partition_by"] = new[] { "region", "dt" },  // writes a Hive-partitioned directory tree
    ["compression"] = "zstd",                      // e.g. snappy (default-ish), zstd, gzip
});
```

When `partition_by` is set, `path` is treated as a **directory** and a `key=value/` tree is written;
otherwise `path` is a single Parquet file.

---

## Delta Lake

Read and write [Delta Lake](https://delta.io/) tables. Reading the latest snapshot uses the DuckDB
delta extension; **time travel** and writes are handled by the library's own transaction-log logic
(see [`DeltaLog`](../src/Andy.Data/Backend/DeltaLog.cs)).

### Loading — [`dataframe_load_delta`](operations.md#dataframe_load_delta)

```csharp
// Latest snapshot
engine.Execute("dataframe_load_delta", new Dictionary<string, object?>
{
    ["path"] = "lake/sales", ["dataset_id"] = "sales",
});

// Time travel by version
engine.Execute("dataframe_load_delta", new Dictionary<string, object?>
{
    ["path"] = "lake/sales", ["dataset_id"] = "sales_v3", ["version"] = 3,
});

// Time travel by timestamp (latest version at or before the instant)
engine.Execute("dataframe_load_delta", new Dictionary<string, object?>
{
    ["path"] = "lake/sales", ["dataset_id"] = "sales_jan",
    ["timestamp"] = "2024-01-31T23:59:59Z",
});
```

`version` and `timestamp` are mutually exclusive.

**Time-travel limitations.** Time travel replays the transaction log directly and supports
**unpartitioned tables without checkpoints, deletion vectors, or other advanced reader features**.
Tables using those return a clear error rather than wrong results. Reading the **latest** snapshot
(no version/timestamp) goes through the delta extension and is not subject to those constraints.

### Exporting — `format=delta`

Delta is the only format that supports `mode=append`. Writes are **atomic, put-if-absent commits with
cross-process optimistic-concurrency retry**; new/overwrite tables are staged then swapped.

```csharp
// Create or overwrite
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales", ["path"] = "lake/sales", ["format"] = "delta",
    ["mode"] = "overwrite",
    ["partition_by"] = new[] { "region" },
});

// Append a new commit
engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "new_rows", ["path"] = "lake/sales", ["format"] = "delta", ["mode"] = "append",
});
```

---

## Choosing a format

- **Ingesting external data** → CSV/JSON loaders; pin types via `columns` (CSV) where inference is
  risky.
- **Intermediate results and re-reads** → Parquet. Load once, build the pipeline on the Parquet-backed
  dataset.
- **Versioned, appendable tables with snapshot/time-travel semantics** → Delta Lake.
- **Large datasets you'll filter by a key** → partition by that key (Parquet or Delta) to get
  partition pruning.
