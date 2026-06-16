# Response & error contract

Every operation returns one [`DataFrameResponse`](../src/Andy.Data.Abstractions/DataFrameResponse.cs)
shape, for both success and failure, so a caller parses one structure across all 28 operations. **No
operation throws across the `Execute` boundary** — schema violations, domain errors, cancellation, and
unexpected backend faults are all mapped to a failure envelope with a stable error code.

```csharp
var r = engine.Execute("dataframe_filter", parameters);
if (r.Success) { /* read r.Schema, r.RowCount, r.PreviewRows, r.Stats, r.Warnings */ }
else           { /* branch on r.ErrorCode; show r.Message; inspect r.Details */ }
```

## Success envelope

| Field (C#) | Envelope key | Type | Meaning |
|------------|--------------|------|---------|
| `Success` | `success` | bool | `true`. |
| `DatasetId` | `dataset_id` | string | The result dataset id (or `"session"` for `list`). |
| `Schema` | `schema` | array | Ordered columns: `{ name, type, nullable }`. `type` is the verbatim DuckDB type (e.g. `VARCHAR`, `DECIMAL(12,2)`). |
| `RowCount` | `row_count` | long? | Total rows in the result (not the preview length). |
| `PreviewRows` | `preview_rows` | array | Up to **50** rows of the result, as `{ column: value }` maps. |
| `PreviewTruncated` | `preview_truncated` | bool | `true` when `row_count` exceeds the preview length. |
| `Warnings` | `warnings` | array | Non-fatal advisories (e.g. an `assert` failure summary). |
| `Stats` | `stats` | object | Execution stats — see below. |

The preview is a **bounded window**, not the full result; the full result stays in the engine under
its `dataset_id`. To see more rows, call [`dataframe_preview`](operations.md#dataframe_preview) with a
larger `limit` (≤ 1000), chain another operation, or [`export`](operations.md#dataframe_export).

### The `stats` block

[`DataFrameStats`](../src/Andy.Data.Abstractions/DataFrameStats.cs):

| Field | Envelope key | Meaning |
|-------|--------------|---------|
| `ElapsedMs` | `elapsed_ms` | Wall-clock time for the operation. |
| `BytesScanned` | `bytes_scanned` | For the four file loaders, an **on-disk file-size estimate** of the input scanned — not a profiler-measured byte count. `0` for transforms. |
| `RowsProduced` | `rows_produced` | Rows in the produced dataset. |
| `Plan` | `plan` | The DuckDB query plan — **only present when the operation was called with `explain = true`**. |

## Failure envelope

| Field (C#) | Envelope key | Meaning |
|------------|--------------|---------|
| `Success` | `success` | `false`. |
| `ErrorCode` | `error_code` | A stable code from the table below — safe to branch on programmatically. |
| `Message` | `message` | Human-readable detail. |
| `Details` | `details` | Optional structured context (e.g. `{ "parameter": "amount" }`, `{ "dataset_id": "sales" }`). |

## Error codes

The stable set from
[`DataFrameErrorCodes`](../src/Andy.Data.Abstractions/DataFrameErrorCodes.cs):

| Code | Raised when |
|------|-------------|
| `DATASET_NOT_FOUND` | A referenced `dataset_id`/`left`/`right` is not registered. |
| `COLUMN_NOT_FOUND` | A referenced column does not exist in the input schema. |
| `INVALID_TYPE` | A parameter has the wrong type, or a value can't be coerced to a column's type. |
| `INVALID_ARGUMENT` | A parameter is missing, malformed, out of range, or an unknown operation id was dispatched. |
| `INVALID_AGGREGATION` | An aggregation spec is invalid (unknown function, missing `q`/`column2`, etc.). |
| `INVALID_PREDICATE` | A predicate **or expression** tree is malformed or uses an unknown operator. |
| `SCHEMA_MISMATCH` | Inputs are incompatible (e.g. a positional `union` with differing column counts). |
| `FILE_NOT_FOUND` | A load path / glob matched no file. |
| `PERMISSION_DENIED` | A read/write path was denied by the configured [path policy](concepts.md#path-policy). |
| `TARGET_EXISTS` | An `export` with `mode=error` found the target already present. |
| `CANCELLED` | The caller cancelled, or `MaxExecutionTimeMs` was exceeded. |
| `BACKEND_ERROR` | Any other unexpected backend failure (the catch-all). |

> Note: the expression parser reports malformed **expression** trees as `INVALID_PREDICATE` (shared
> with the predicate parser), not a distinct code.

## Serialization

`DataFrameResponse.ToEnvelope()` produces the snake_case dictionary shown in the tables above (the
shape a tool-framework adapter places in its tool result). `ColumnSchema.ToEnvelope()` and
`DataFrameStats.ToEnvelope()` do the same for their nested shapes. The C# properties and the envelope
keys are 1:1 as tabulated.
