# Architecture

`Andy.Data` is a framework-independent dataframe engine. It loads tabular files (CSV, JSON,
Parquet, Delta), transforms them with a closed set of operations, and exports the result — all
through an embedded [DuckDB](https://duckdb.org) connection, with no SQL or code supplied by the
caller and no dependency on any tool framework.

This document describes how the engine is layered, how a single call flows through it, the SQL
rendering pipeline, the DuckDB backend, the Delta transaction-log reader/writer, observability, the
concurrency model, and the efficiency properties that fall out of the design.

See also: [core-concepts.md](core-concepts.md), [operations.md](operations.md),
[reliability.md](reliability.md).

## The two packages

The library ships as two NuGet packages with a deliberate dependency boundary.

| Package | Contains | DuckDB dependency |
| --- | --- | --- |
| `Andy.Data.Abstractions` | Contract types only: the response envelope (`DataFrameResponse`), the stable error codes (`DataFrameErrorCodes`), the dataset catalog contract (`IDatasetCatalog`, `InMemoryDatasetCatalog`, `DatasetEntry`), column schema (`ColumnSchema`), the path-policy contract (`IPathPolicy`), and the structured predicate/expression models with their parsers (`Predicates/`, `Expressions/`). | None |
| `Andy.Data` | The DuckDB backend (`DuckDbBackend`), SQL renderers (`Sql/`), the 28 operations (`Operations/`), the engine facade (`DataFrameEngine`), per-call options (`DataFrameExecuteOptions`), and observability (`DataFrameActivitySource`). | DuckDB.NET |

A host that only needs to parse responses, branch on error codes, or build a predicate tree depends
on `Andy.Data.Abstractions` alone and never pulls in the native DuckDB engine. The parsers live in
the abstractions package on purpose: turning model-supplied JSON into a validated, typed tree is a
contract concern, independent of how that tree is later rendered to SQL.

## Layers and data flow

A call enters through `DataFrameEngine.Execute` and flows down through validation, the operation
body, the SQL renderers, and the backend, then back up as a single envelope.

```
caller
  │  engine.Execute(operationId, parameters, options)
  ▼
┌──────────────────────────────────────────────────────────────────────┐
│ DataFrameEngine (facade)                                               │
│   dispatch by operationId  ──►  the matching DataFrameOperationBase    │
└──────────────────────────────────────────────────────────────────────┘
  ▼
┌──────────────────────────────────────────────────────────────────────┐
│ DataFrameOperationBase.Guard  (one pipeline for every operation)       │
│   1. DataFrameParameterValidator.Validate(parameters, Metadata)        │
│        required / type / range / pattern / allowed-values              │
│        ──► INVALID_TYPE | INVALID_ARGUMENT on violation                │
│   2. apply resource governance from DataFrameExecuteOptions            │
│        MaxMemoryBytes ──► backend.ApplyResourceLimits                   │
│        MaxExecutionTimeMs + caller token ──► linked CancellationToken  │
│   3. run the operation body                                            │
│   4. map outcome ──► DataFrameResponse                                 │
│        DataFrameException ──► its error code                           │
│        OperationCanceledException ──► CANCELLED                        │
│        any other exception ──► BACKEND_ERROR                           │
└──────────────────────────────────────────────────────────────────────┘
  ▼  (operation body)
┌──────────────────────────────────────────────────────────────────────┐
│ Parse + render                                                         │
│   PredicateParser / ExpressionParser   (closed vocabularies)           │
│        ──► typed PredicateNode / ExprNode tree                         │
│   PredicateSqlRenderer / ExpressionSqlRenderer / SqlText               │
│        ──► WHERE / projection fragments built from:                    │
│              fixed renderer templates                                  │
│            + schema-resolved quoted identifiers                        │
│            + escaped literals                                          │
└──────────────────────────────────────────────────────────────────────┘
  ▼
┌──────────────────────────────────────────────────────────────────────┐
│ DuckDbBackend  (one in-memory DuckDB connection, used under a lock)    │
│   register views / materialize tables / run SELECT / count / preview   │
│   each dataset_id ──► an internally generated physical relation name   │
└──────────────────────────────────────────────────────────────────────┘
  ▲
  └──► DataFrameResponse  { Success, DatasetId, Schema, RowCount,
                            PreviewRows, PreviewTruncated, Warnings, Stats,
                            ErrorCode, Message, Details }
```

`Execute` is **synchronous** and never throws across the boundary: every outcome — success, a
validation failure, a backend failure, or cancellation — comes back as a `DataFrameResponse`.

### The Guard pipeline

Every operation runs its body inside `DataFrameOperationBase.Guard`, which centralizes the
behavior so all 28 operations produce the identical envelope:

1. **Validate.** `DataFrameParameterValidator.Validate` checks the parameters dictionary against
   the operation's declared `OperationMetadata.Parameters`: required-ness, JSON type
   (`string`/`integer`/`number`/`boolean`/`array`/`object`), inclusive numeric range, string
   regex pattern, and closed allowed-value sets. A missing/required, wrong-type, out-of-range, or
   pattern violation maps to `INVALID_TYPE`; an out-of-vocabulary value (a value not in
   `AllowedValues`) maps to `INVALID_ARGUMENT`.
2. **Govern resources.** If `MaxMemoryBytes` is positive it is applied to the backend's DuckDB
   `memory_limit`; if `MaxExecutionTimeMs` is positive a linked `CancellationTokenSource` is armed
   to cancel after that wall-clock budget, combined with the caller's token. The effective token is
   handed to the body.
3. **Run + map.** The body runs. A `DataFrameException` becomes its carried error code; an
   `OperationCanceledException` becomes `CANCELLED`; anything else becomes `BACKEND_ERROR`. A trace
   activity wraps the whole thing and is tagged with the error code on failure.

## The SQL rendering pipeline — no injection surface

The engine never executes caller-supplied SQL. Operations that take expressive input
(`dataframe_filter`, `dataframe_with_column`, `dataframe_group_by`, …) accept a **structured tree**,
not a string, and that tree is built from closed enumerated vocabularies.

1. **Parse.** `PredicateParser` / `ExpressionParser` (in `Andy.Data.Abstractions`) turn the
   model-supplied dictionary into a typed `PredicateNode` / `ExprNode` tree. Operator and function
   names are checked against fixed `HashSet`s; anything unknown throws `INVALID_PREDICATE`.
2. **Render.** `PredicateSqlRenderer` and `ExpressionSqlRenderer` walk the tree and emit a SQL
   fragment. Every token in the output is one of exactly three things:
   - a **fixed renderer template** — e.g. `eq` always renders to `{col} = {right}`, `upper` to
     `upper({arg})`; the operator/function set is closed and enumerated in the renderer's `switch`.
   - a **schema-resolved, quoted identifier** — `SqlText.ResolveColumnQuoted` matches the
     caller's column name (case-insensitively) against the dataset's actual schema, throwing
     `COLUMN_NOT_FOUND` (with a "did you mean" suggestion) if it is absent, then double-quotes it.
   - an **escaped literal** — `SqlText.Literal` renders values as culture-invariant,
     round-trippable forms; strings are single-quoted with `'` doubled, so a literal cannot break
     out of its quotes.

   Further closed vocabularies guard the corners: cast target types must match a conservative regex,
   `strptime` formats and `date_*` units are checked against enumerated sets, and CSV column-type
   hints must match a plain-type-name regex.

Because there is no path from input to a raw SQL string — every byte of generated SQL is a template,
a quoted identifier, or an escaped literal — there is no injection surface and no model-supplied SQL.
See [security.md](security.md) for the full argument.

## The DuckDB backend

`DuckDbBackend` is the only component that touches the database. It wraps a single embedded,
in-memory DuckDB connection (`Data Source=:memory:`).

### Logical ids vs. physical relations

Each caller-facing `dataset_id` maps to an internally generated, unique physical relation name
(`df_1`, `df_2`, …) held in a private dictionary. The caller-supplied id never reaches SQL, so it
cannot be an injection vector, and a transform whose output id equals its input id
(`into == dataset_id`) is not a circular reference — the new physical is a fresh name that selects
from the old one.

### Lazy views fold a chain into one plan

Loaders and transforms both register **lazy `VIEW`s**, not materialized tables. A chain such as
`filter → select → group_by` collapses into a single DuckDB plan, letting DuckDB push predicates and
projections down to the underlying scan instead of materializing each intermediate step. Past a
bounded view depth the next derivation is checkpointed into a materialized `TABLE` snapshot, so
re-executing the chain for each count/preview stays depth-bounded rather than re-scanning the whole
history at every step. Set-shaped operations that cannot stay lazy — `join`, `union`, `pivot`,
`unpivot`, `sample` — always create materialized `TABLE`s and are independent of their inputs from
then on.

### Lifecycle and memory reclamation

The backend tracks each physical's dependencies. Dropping or remapping an id triggers a reachability
sweep: only relations no longer reachable from any mapped id are dropped (dependents before
parents). Views are cheap metadata, but the materialized tables that join/union/pivot create hold
real memory that the sweep reclaims once nothing references them.

### EXPLAIN plans

When a transform is called with `explain=true`, the backend runs `EXPLAIN` over the rendered
statement and returns the DuckDB plan tree, which the operation places in `Stats.Plan`. This shows
the projection/predicate pushdown and any partition pruning without materializing data.

## Efficiency

The efficiency properties are consequences of the backend design and DuckDB itself:

- **Column projection.** `dataframe_select` renders only the requested columns; lazy views push the
  projection to the scan, so unread columns are never decoded (especially cheap for Parquet, which
  is columnar).
- **Predicate pushdown.** `dataframe_filter`'s rendered `WHERE` clause folds into the same plan as
  the scan, so DuckDB applies the predicate at the scan level wherever the source supports it.
- **Partition pruning.** Loading Hive-partitioned Parquet with `hive_partitioning=true` exposes the
  partition columns; predicates on them let DuckDB skip whole partition directories.
- **Bounded memory.** DuckDB's vectorized, larger-than-memory engine processes data in batches. When
  `MaxMemoryBytes` is set, the backend applies DuckDB's `memory_limit` and configures a temp
  spill directory, so a query that exceeds the cap **spills to disk** rather than failing.
- **Inspecting the plan.** `explain=true` returns the chosen plan in `Stats.Plan` so you can confirm
  pushdown and pruning are happening.

`DataFrameStats` (on every successful envelope) reports `ElapsedMs`, a best-effort `BytesScanned`
(for file loaders), `RowsProduced`, and the optional `Plan`.

## Observability and tracing

`DataFrameActivitySource` is a `System.Diagnostics.ActivitySource` named `Andy.Data`. The Guard
pipeline opens a `dataframe.tool.execute` activity per operation (tagged with the operation id, and
the error code on failure); each DuckDB statement opens a nested `dataframe.sql.execute` activity
(tagged with the SQL text, marked error/cancelled on failure). Activities are produced **only when a
listener is subscribed**, so the library stays fully functional with no OpenTelemetry/logging
provider configured. An optional `ILoggerFactory` passed to the engine adds structured logs at the
operation and SQL-statement level.

## Concurrency model

**One backend == one DuckDB connection used under a lock.** Every backend method takes a single
gate (`lock`) for the full duration of its statement — the result set is materialized before the
lock is released. This means:

- The backend is safe to call concurrently, but inter-query work **serializes**: two operations on
  one engine do not run their SQL in parallel.
- Parallelism within a single query (DuckDB's intra-query parallelism) is unaffected and still used.
- For concurrent independent workloads, **use one engine (one backend, one connection) per
  concurrent stream**.

Disposal happens under the same gate, so tearing down a backend cannot race an in-flight statement;
a call arriving after disposal surfaces `ObjectDisposedException` (mapped to `BACKEND_ERROR`).
Cooperative cancellation interrupts a running statement via DuckDB's interrupt API from the
cancelling thread while the executing thread polls the flag — see
[reliability.md](reliability.md) for the cancellation contract.
