# Troubleshooting

This guide is keyed by the symptom you see — almost always a failure envelope with a stable
`ErrorCode`, or a surprising-but-correct result. Each entry gives the cause and the resolution.

Recall the failure envelope: `r.Success == false`, with `r.ErrorCode`, `r.Message`, and an optional
`r.Details`. See also: [operations.md](operations.md), [security.md](security.md).

## By error code

### `DATASET_NOT_FOUND`

**Symptom.** An operation that references a `dataset_id` fails immediately.

**Cause.** The id is not registered in this engine's catalog/backend — a typo, a different casing
(ids are compared exactly), or a dataset from a different `DataFrameEngine` instance. Datasets are
**session-scoped**: they live for the lifetime of the engine that loaded them and are not shared
across engine instances or after disposal.

**Resolution.** Load or derive the dataset in the same engine first; call `dataframe_list` to see
what is registered. Reuse one engine across the chain of operations that share datasets.

### `COLUMN_NOT_FOUND`

**Symptom.** A filter, expression, select, sort, group-by, or partition reference fails.

**Cause.** The column name does not exist in the dataset's resolved schema.

**Resolution.** Check `r.Details["did_you_mean"]` — the engine suggests the closest column name. Use
`dataframe_schema` to list exact column names. Matching is case-insensitive, so case is not the
issue; a typo or a stale assumption about the schema is.

### `INVALID_TYPE` and type-coercion surprises

**Symptom.** A parameter is rejected, or a CSV column came back as the "wrong" type (e.g. a numeric
column inferred as `VARCHAR`, leading zeros stripped, or a date read as text).

**Cause.** Either a parameter has the wrong JSON type / is out of range / fails its pattern / is a
missing required value, **or** CSV type inference sampled rows and guessed a type you did not want.
CSV inference samples `sample_size` rows (default 20480); if the disagreeing values appear only
later in the file, inference can miss them.

**Resolution.**

- For parameters: send the declared type (e.g. an integer where an integer is expected).
- For CSV inference: pin specific columns with the `columns` type-hint map, e.g.
  `columns = { "zip": "VARCHAR", "amount": "DECIMAL(12,2)" }` (values are DuckDB type names). To
  consider the whole file during inference, set `sample_size = -1` (slower, but exact).

```csharp
engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "data/orders.csv",
    ["dataset_id"] = "orders",
    ["columns"] = new Dictionary<string, object?> { ["zip"] = "VARCHAR", ["amount"] = "DECIMAL(12,2)" },
    ["sample_size"] = -1,   // read the whole file for inference
});
```

### `INVALID_PREDICATE` / `INVALID_AGGREGATION`

**Symptom.** A filter/expression or a group-by aggregation is rejected.

**Cause.** An operator, function, aggregation, date/time unit, or `strptime` format that is not in
the engine's closed vocabulary, or a structurally malformed predicate/expression tree.

**Resolution.** Use only the documented operators, functions, and aggregations
([operations.md](operations.md)). Common slips: a SQL-style operator name (`=`, `!=`) instead of the
tree operator (`eq`, `neq`); a function not in the set; or a `date_part` unit/`strptime` format
outside the supported list. The engine accepts a structured tree, not a SQL string — there is no way
to "escape" into raw SQL.

### `FILE_NOT_FOUND`

**Symptom.** A loader fails before reading anything.

**Cause.** A concrete (non-glob) input path does not exist, or a Delta target is not actually a
Delta table (no `_delta_log/` directory).

**Resolution.** Verify the path (it is resolved relative to the engine process's working directory).
For multiple files use a glob such as `data/*.csv`; a glob that matches nothing is not a
`FILE_NOT_FOUND` (only a concrete missing path is). For Delta, point at the table root that contains
`_delta_log/`.

### `PERMISSION_DENIED`

**Symptom.** A load or export fails before touching the file; `r.Details["path"]` shows a
canonicalized path.

**Cause.** A registered `IPathPolicy` denied `CanRead` (loaders) or `CanWrite` (export) for that
path.

**Resolution.** This is the path gate working as configured. Allow the path in your policy, or
target a permitted location. Note the path is canonicalized (`..` resolved, symlinks resolved) before
the policy sees it, so check the *real* target, not the spelling you passed. When no policy is
registered, all paths are permitted and this code cannot occur. See [security.md](security.md).

### `TARGET_EXISTS`

**Symptom.** An export fails because the destination is already there, or a Delta append reports a
concurrent writer.

**Cause.** `dataframe_export` ran with the default `mode=error` against an existing target, or a
Delta commit version was claimed by another writer first.

**Resolution.** Set `mode=overwrite` to replace the target atomically (staged-then-swapped), or, for
Delta, `mode=append` to add a new commit. A cross-process Delta append retries automatically; a
persistent `TARGET_EXISTS` on append indicates a stuck table.

### `CANCELLED`

**Symptom.** A long operation returns `CANCELLED`.

**Cause.** The caller's `CancellationToken` was cancelled, or `MaxExecutionTimeMs` was exceeded.

**Resolution.** If the time limit is too tight, raise `MaxExecutionTimeMs` (or leave it unset). If a
caller is cancelling, that is expected. Cancellation is cooperative and interrupts the in-flight
DuckDB statement; you never get a partial success.

### `BACKEND_ERROR`

**Symptom.** An unexpected failure not covered by the codes above.

**Cause.** A DuckDB engine error, or — most commonly for Delta — one of:

- **Delta extension unavailable.** Reading the latest snapshot via `dataframe_load_delta` uses the
  DuckDB `delta` extension, which is auto-installed (`INSTALL delta; LOAD delta`) on first use. The
  install needs **network access once**, after which it is cached, or the extension must be bundled
  with the DuckDB build. Offline first use yields `BACKEND_ERROR` explaining this. (Delta
  *time travel* and *export* do not need the extension — they replay/emit the log directly.)
- **Delta time-travel / append limits.** The engine's own log reader/writer is a happy-path
  implementation: it supports **unpartitioned tables with plain JSON-commit history only**. It
  refuses — with a clear `BACKEND_ERROR`, never a wrong answer — when it sees **checkpoints**
  (`_last_checkpoint` or `*.checkpoint.parquet`), **partition columns** (for time travel),
  **deletion vectors**, or **column mapping**.

**Resolution.** For the extension: perform one online run to cache it, or bundle it. For the Delta
limits: load the latest snapshot without `version`/`timestamp` (which uses the extension), or avoid
checkpointing/partitioning/deletion-vectors/column-mapping on tables you need to time-travel or
append to with this engine.

## Other symptoms

### Previews look truncated

**Symptom.** `r.PreviewRows` holds only a handful of rows and `r.PreviewTruncated == true`.

**Cause.** **By design.** Every operation returns a *bounded* preview (the head of the result); the
full result lives in the session dataset, not in the envelope. `r.RowCount` reports the true total.

**Resolution.** To obtain the full data, run `dataframe_export` to CSV/Parquet/JSON/Delta. Do not
treat `PreviewRows` as the complete result.

### Out of memory on a large operation

**Symptom.** A join/group-by/sort over large data fails or strains memory.

**Cause.** No memory cap was set, or the cap is too low and there is no spill configured.

**Resolution.** Set `MaxMemoryBytes` in `DataFrameExecuteOptions`. The backend configures a temp
spill directory alongside the `memory_limit`, so a query that exceeds the cap **spills to disk**
rather than failing. Raise the cap if spilling makes it too slow.

```csharp
var options = new DataFrameExecuteOptions { MaxMemoryBytes = 1L * 1024 * 1024 * 1024 }; // 1 GiB
var r = engine.Execute("dataframe_group_by", parameters, options);
```

To confirm the plan (pushdown/pruning), pass `explain = true` on a transform and read `r.Stats.Plan`.

### DuckDB native-load issues on a platform

**Symptom.** Constructing a `DataFrameEngine` / `DuckDbBackend` throws at startup (a native library
load failure), or every operation returns `BACKEND_ERROR`.

**Cause.** The DuckDB.NET native runtime for the current OS/architecture is missing or
incompatible — common in trimmed/self-contained publishes or unusual platforms.

**Resolution.** Ensure the matching DuckDB.NET native runtime package is restored for your target
RID and is present next to the app. Verify the platform is supported by your DuckDB.NET version. The
extension auto-install also needs writable extension storage and (on first use) network access.
