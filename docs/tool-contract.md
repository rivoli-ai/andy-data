# The Response Contract

Every operation in `Andy.Data` returns the same value: a `DataFrameResponse`. This
page is the **stable contract** for that envelope — its fields, its snake_case
JSON projection, and the full set of error codes. The contract types live in the
`Andy.Data.Abstractions` assembly (namespace `Andy.Data`) and are preserved
**verbatim from `andy-tools-dataframe`**, so an integration built against the old
framework tools sees byte-for-byte the same shape. Several source files reference
this document by path (`docs/tool-contract.md`); keep it in sync with the types.

## One shape for success and failure

`DataFrameResponse` is a single type that carries both outcomes. Branch on
`Success` and read the appropriate group of fields:

```csharp
if (response.Success)
{
    // success fields: DatasetId, Schema, RowCount, PreviewRows,
    //                 PreviewTruncated, Warnings, Stats
}
else
{
    // failure fields: ErrorCode, Message, Details
    Console.WriteLine($"[{response.ErrorCode}] {response.Message}");
}
```

A single parsing path therefore handles every operation, success or failure.

## Success fields

| C# property | Type | Envelope key | Meaning |
|-------------|------|--------------|---------|
| `Success` | `bool` | `success` | `true` for success. |
| `DatasetId` | `string?` | `dataset_id` | The id the result was registered under (the `into` id, or the replaced `dataset_id`). |
| `Schema` | `IReadOnlyList<ColumnSchema>` | `schema` | Ordered output columns; each is `{ name, type, nullable }`. |
| `RowCount` | `long?` | `row_count` | Total rows in the result (not the preview count). |
| `PreviewRows` | `IReadOnlyList<IReadOnlyDictionary<string, object?>>` | `preview_rows` | Bounded sample rows (column name → value). |
| `PreviewTruncated` | `bool` | `preview_truncated` | `true` when `RowCount` exceeds the preview size. |
| `Warnings` | `IReadOnlyList<string>` | `warnings` | Non-fatal advisories. |
| `Stats` | `DataFrameStats?` | `stats` | Execution statistics (see below). |

### `ColumnSchema`

`ColumnSchema` is a record `(string Name, string Type, bool Nullable = true)`.
`Type` is the **verbatim DuckDB type** (for example `VARCHAR`, `BIGINT`,
`DECIMAL(12,2)`, `TIMESTAMP`). Its envelope shape is `{ name, type, nullable }`.

### `DataFrameStats`

`DataFrameStats` is a record `(long ElapsedMs, long BytesScanned, long
RowsProduced, string? Plan = null)`:

| C# property | Envelope key | Notes |
|-------------|--------------|-------|
| `ElapsedMs` | `elapsed_ms` | Wall-clock time for the operation. |
| `BytesScanned` | `bytes_scanned` | `0` for transform operations. For the four file loaders (`dataframe_load_csv`, `dataframe_load_parquet`, `dataframe_load_json`, `dataframe_load_delta`) it is an **on-disk file-size estimate** of the input scanned — not a profiler-measured byte count. |
| `RowsProduced` | `rows_produced` | Rows the operation produced. |
| `Plan` | `plan` | The DuckDB query plan. Present **only** when the operation was called with `explain = true`; otherwise the key is omitted entirely. |

## Failure fields

| C# property | Type | Envelope key | Meaning |
|-------------|------|--------------|---------|
| `Success` | `bool` | `success` | `false` for failure. |
| `ErrorCode` | `string?` | `error_code` | A stable code from `DataFrameErrorCodes` (see below) — safe for programmatic branching. |
| `Message` | `string?` | `message` | Human/model-facing explanation. |
| `Details` | `IReadOnlyDictionary<string, object?>?` | `details` | Optional structured context (e.g. the offending column name). Omitted from the envelope when `null`. |

## The `ToEnvelope()` snake_case mapping

`DataFrameResponse.ToEnvelope()` projects the response into an
`IDictionary<string, object?>` with **snake_case keys** — the model-facing JSON
shape. The branch is chosen by `Success`, so success and failure never mix fields.

### A successful envelope

```json
{
  "success": true,
  "dataset_id": "eu_totals",
  "schema": [
    { "name": "category", "type": "VARCHAR", "nullable": true },
    { "name": "total", "type": "DECIMAL(12,2)", "nullable": true }
  ],
  "row_count": 3,
  "preview_rows": [
    { "category": "books", "total": 1240.50 },
    { "category": "music", "total": 880.00 }
  ],
  "preview_truncated": true,
  "warnings": [],
  "stats": {
    "elapsed_ms": 12,
    "bytes_scanned": 0,
    "rows_produced": 3
  }
}
```

Notes on the success shape:

- `schema` is the list of `ColumnSchema.ToEnvelope()` objects.
- `stats.plan` is absent here because the operation ran without `explain = true`.
- `preview_truncated` is `true`, signalling more rows exist than the preview shows
  — use `dataframe_export` for the complete result.

### An error envelope

```json
{
  "success": false,
  "error_code": "COLUMN_NOT_FOUND",
  "message": "Column 'regon' does not exist in dataset 'sales'.",
  "details": {
    "column": "regon",
    "dataset_id": "sales"
  }
}
```

If `Details` is `null`, the `details` key is omitted entirely. None of the
success-only keys (`dataset_id`, `schema`, `row_count`, …) appear in an error
envelope.

## Error codes

Every code is a constant on the static `DataFrameErrorCodes` class (namespace
`Andy.Data`). They are part of the contract and safe to branch on programmatically.

| Code | When it occurs |
|------|----------------|
| `DATASET_NOT_FOUND` | A referenced `dataset_id` is not registered in the catalog. |
| `COLUMN_NOT_FOUND` | A referenced column does not exist in the dataset's schema. |
| `INVALID_TYPE` | A value or column type is incompatible with the requested operation (e.g. a type hint or coercion that cannot be applied). |
| `INVALID_ARGUMENT` | A parameter is missing, malformed, or out of range — including dispatch to an unknown operation id. |
| `INVALID_AGGREGATION` | An aggregation spec is malformed: unknown function, missing `column`, missing/invalid `q`, or a missing/disallowed `column2`. |
| `INVALID_PREDICATE` | A predicate (or `having`) tree is not a valid condition/logical node, or references a column it may not. |
| `SCHEMA_MISMATCH` | Inputs that must share a schema do not (e.g. a union or append whose columns or types disagree). |
| `FILE_NOT_FOUND` | A load `path` (or required input file) does not exist or is empty. |
| `PERMISSION_DENIED` | The path policy forbids reading or writing the requested path. |
| `TARGET_EXISTS` | An export target already exists and `mode` is `error` (the default). |
| `CANCELLED` | The operation was cancelled via the `CancellationToken` or exceeded `MaxExecutionTimeMs`. |
| `BACKEND_ERROR` | The DuckDB backend raised an error not captured by a more specific code. |

## Why previews are bounded

`preview_rows` is intentionally a **small, bounded sample**, not the full result.
This keeps every response a predictable size regardless of dataset volume — which
matters when a language model consumes the envelope — and makes responses cheap to
inspect after each step. `preview_truncated` tells you when rows were withheld. The
**full** materialized result always comes from `dataframe_export`, which writes the
complete dataset to disk (CSV, Parquet, JSON, or Delta).

## Next steps

- [operations.md](operations.md) — the per-operation parameter reference.
- [reliability.md](reliability.md) — determinism, error handling, and durability
  guarantees.
