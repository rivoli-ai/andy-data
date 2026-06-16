# Benchmarks

A quick, reproducible characterization of `Andy.Data`'s performance, scaling, and limits. The goal is
to map the **rough shape** of the library — where time goes, how it scales, and where the edges are —
not to publish a rigorous, machine-normalized benchmark suite.

## What the harness does

[`benchmarks/Andy.Data.Benchmarks`](../benchmarks/Andy.Data.Benchmarks) generates deterministic
synthetic data (`id, region, category, amount, qty, ts`; 5 regions × 20 categories) at several row
counts, then times each operation as the **median of N repetitions** with a warm-up pass off the
clock. Each measurement is a full `engine.Execute(...)` call — so it includes the operation **plus the
row count and preview that every response carries** (the [terminals](concepts.md#lazy-views-read-this-for-performance)
that force the lazy plan to execute).

Two deliberate choices shape the numbers:

- **`load_csv` and `export_parquet` read/write the file directly.** `load_csv` includes DuckDB's
  dialect sniffing and type inference.
- **All transforms (`filter`, `group_by`, `sort`, `window`, `distinct`, `join`) run over a
  Parquet-backed dataset**, not the CSV-backed one. This isolates the operation cost from CSV
  re-parsing — see [why this matters](#the-source-format-dominates-derived-cost) below.

## Results

Measured on: macOS (Darwin 24.6), x64, 32 logical cores, .NET 8, DuckDB via `DuckDB.NET`. Median of 7
runs. **Your numbers will differ** with hardware, core count, and load; treat these as orders of
magnitude, not guarantees.

### Timings (median ms)

| rows | load_csv | load_parquet | filter | group_by | sort+limit | window | distinct | join | export_parquet |
|-----:|---------:|-------------:|-------:|---------:|-----------:|-------:|---------:|-----:|---------------:|
| 100,000 | 260 | 6 | 8 | 10 | 17 | 53 | 20 | 48 | 117 |
| 1,000,000 | 304 | 7 | 11 | 18 | 111 | 296 | 33 | 122 | 206 |
| 5,000,000 | 371 | 8 | 18 | 43 | 500 | 1,563 | 133 | 183 | 332 |
| 10,000,000 | 446 | 24 | 65 | 121 | 1,203 | 3,801 | 253 | 278 | 705 |

### Dataset sizes & memory

| rows | CSV on disk | Parquet on disk | process RSS after scale |
|-----:|------------:|----------------:|------------------------:|
| 100,000 | 3.2 MB | 1.5 MB | 433 MB |
| 1,000,000 | 33.1 MB | 14.8 MB | 1,000 MB |
| 5,000,000 | 169.7 MB | 73.8 MB | 4,382 MB |
| 10,000,000 | 340.5 MB | 147.6 MB | 6,051 MB |

> RSS is the **process** resident set sampled after each scale, dominated by the native DuckDB engine
> and not promptly returned to the OS between scales; it is a high-water indicator, not a per-dataset
> working-set measurement. The Parquet files are ~2.3× smaller than the CSV on disk.

## What the numbers say

**Parquet load is effectively free; CSV load has a fixed floor.** `load_parquet` reads only metadata
(~6–24 ms across all scales). `load_csv` carries a ~250 ms fixed cost (dialect sniffing + type
inference) that dominates small files and grows slowly with size. *Takeaway:* for anything you read
more than once, use Parquet.

**Scans and aggregations are fast and scale near-linearly.** `filter` and `group_by` over a
Parquet-backed dataset stay in the tens of milliseconds up to a million rows and remain double-digit
to low-triple-digit at 10 M, because predicates/projections are pushed down to the columnar scan.

**Sorts and window functions are the expensive operations.** `sort` (a full ordering) and especially
`window` (here, `row_number` over a partition ordered by a high-cardinality column — essentially a full
sort) grow super-linearly and dominate at scale. If a step is slow, it is almost always an order-by.
Push `limit` into [`sort`](operations.md#dataframe_sort) for top-N to avoid ordering more than you
need, and prefer narrower partitions / lower-cardinality order keys for windows.

**`join` and `export` materialize**, so they include a write step and sit in the middle of the pack.

### The source format dominates derived cost

This is the single most important performance fact about the library, and it follows directly from the
[lazy-view model](concepts.md#lazy-views-read-this-for-performance): a load registers a **view over the
source file**, and a transform over it is a view over that view. The row count and preview that every
response returns then **re-execute the chain from the source**.

A concrete illustration from an earlier run where the same transforms read from the **CSV-backed**
dataset instead of the Parquet-backed one:

| op (100 K rows) | over Parquet-backed | over CSV-backed |
|-----------------|--------------------:|----------------:|
| filter | ~8 ms | ~265 ms |
| group_by | ~10 ms | ~285 ms |
| distinct | ~20 ms | ~300 ms |

The CSV-backed transforms each cost roughly a **full CSV re-parse** (twice — once for the count, once
for the preview), because the CSV is re-scanned every time the chain is forced. Over Parquet the same
operations are 1–2 orders of magnitude cheaper. *Takeaway, restated:* load CSV/JSON once, `export` to
Parquet, and build your pipeline on the Parquet-backed dataset — or rely on the engine's automatic
chain-depth checkpointing to materialize long chains.

## Limits & guidance

- **In-memory engine.** The default backend is in-memory DuckDB; the working set must fit in RAM
  (DuckDB spills to a temp dir past the [`MaxMemoryBytes`](concepts.md#resource-governance--cancellation)
  limit, trading speed for capacity). The 10 M-row scale here pushed process RSS to ~6 GB.
- **Bound big operations.** Set `MaxMemoryBytes` and `MaxExecutionTimeMs` for untrusted or unbounded
  workloads; a runaway op returns `CANCELLED` rather than exhausting the host.
- **Previews are bounded (≤ 50 rows in a response, ≤ 1000 via `preview`).** Don't treat the preview as
  the result set; the full result lives in the engine under its `dataset_id`.
- **One connection per engine.** A single engine serializes operations (parallelism is *intra*-query).
  For concurrent independent pipelines, use one [engine per stream](concepts.md#concurrency).
- **Ordering is the cost center.** `sort` and `window` dominate at scale; everything else is cheap by
  comparison.

## Reproduce

```bash
# defaults: scales 100000,1000000,5000000 ; 5 repetitions
dotnet run --project benchmarks/Andy.Data.Benchmarks -c Release

# custom scales and repetition count
dotnet run --project benchmarks/Andy.Data.Benchmarks -c Release -- 100000,1000000,5000000,10000000 7
```

The Markdown report is printed to stdout (progress goes to stderr), so you can capture it with a
redirect:

```bash
dotnet run --project benchmarks/Andy.Data.Benchmarks -c Release -- 1000000 5 > results.md
```

The harness is a plain Stopwatch wall-clock measurement (median of repetitions, with warm-up), not a
statistically rigorous study — for that, wrap the operations in
[BenchmarkDotNet](https://benchmarkdotnet.org/). It is intentionally simple so the numbers are easy to
regenerate and reason about.
