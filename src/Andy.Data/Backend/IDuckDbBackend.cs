using Andy.Data;

namespace Andy.Data.Backend;

/// <summary>
/// Options for loading a CSV source. Only the path is required; the rest tune parsing.
/// <see cref="Columns"/> maps column name → DuckDB type and overrides inference for those columns
/// (type tokens are validated by the calling operation). <see cref="SampleSize"/> is the number of
/// rows sampled for type inference (DuckDB default 20480; -1 reads the whole file).
/// </summary>
public sealed record CsvLoadOptions(
    string Path,
    bool? Header = null,
    string? Delimiter = null,
    string? NullString = null,
    string? Quote = null,
    IReadOnlyDictionary<string, string>? Columns = null,
    long? SampleSize = null);

/// <summary>
/// Options for CSV export. All members are optional; when omitted DuckDB uses its defaults
/// (header true, comma delimiter, double-quote quote/escape).
/// </summary>
public sealed record CsvExportOptions(
    bool? Header = null,
    string? Delimiter = null,
    string? Quote = null,
    string? Escape = null);

/// <summary>
/// Abstraction over the embedded DuckDB engine. Datasets are registered as views keyed by a
/// (validated) <c>dataset_id</c>; the planner/operations query them. File paths and option values
/// are rendered as escaped, injection-safe string literals (DuckDB cannot prepare DDL, so they
/// cannot be bound parameters); a DuckDB single-quoted literal cannot break out of its quotes, so
/// no model input reaches the query as code.
/// </summary>
/// <remarks>
/// <para><b>Cancellation.</b> Methods that execute SQL accept an optional
/// <see cref="CancellationToken"/>. The token is checked at operation entry, and an in-flight
/// statement is interrupted via <c>DuckDBCommand.Cancel()</c> (DuckDB's interrupt API) when the
/// token fires, surfacing as an <see cref="OperationCanceledException"/>. If the token fires in
/// the narrow window after a statement completes, the next statement may run to completion before
/// the cancellation is observed at the following token check.</para>
/// </remarks>
public interface IDuckDbBackend : IDisposable
{
    /// <summary>
    /// Runs <paramref name="body"/> while holding the backend's operation lock, making a
    /// multi-step sequence (derive → count → catalog register → preview) atomic with respect to
    /// concurrent operations such as <see cref="Drop"/>. Reentrant: <paramref name="body"/> may
    /// call other backend methods.
    /// </summary>
    T RunExclusive<T>(Func<T> body);

    /// <summary>
    /// Applies an engine-level memory cap (DuckDB <c>memory_limit</c> plus a spill
    /// <c>temp_directory</c>) from the calling operation's resource limits. Idempotent per value;
    /// with a shared backend the engine reflects the most recent operation's limit.
    /// </summary>
    void ApplyResourceLimits(long maxMemoryBytes);

    /// <summary>Registers (or replaces) a view that reads a CSV source. Returns the inferred schema.</summary>
    IReadOnlyList<ColumnSchema> RegisterCsv(string datasetId, CsvLoadOptions options, CancellationToken ct = default);

    /// <summary>
    /// Registers (or replaces) a view that reads a Parquet file, glob, or partitioned dir.
    /// <paramref name="hivePartitioning"/> exposes <c>key=value/</c> directories as partition
    /// columns (DuckDB auto-detects when null); <paramref name="unionByName"/> aligns columns by
    /// name across files with differing schemas (DuckDB default false when null).
    /// </summary>
    IReadOnlyList<ColumnSchema> RegisterParquet(
        string datasetId, string path, bool? hivePartitioning = null, bool? unionByName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registers (or replaces) a view that reads a JSON source (NDJSON, a top-level array of
    /// objects, or auto-detected). <paramref name="format"/> is a validated token — null/"auto"
    /// auto-detects via <c>read_json_auto</c>; "newline_delimited"/"array" force the layout.
    /// </summary>
    IReadOnlyList<ColumnSchema> RegisterJson(
        string datasetId, string path, string? format = null, CancellationToken ct = default);

    /// <summary>Registers (or replaces) a view that reads a Delta Lake table via the delta extension.</summary>
    IReadOnlyList<ColumnSchema> RegisterDelta(string datasetId, string path, CancellationToken ct = default);

    /// <summary>
    /// Registers (or replaces) a view over a Delta Lake table at a specific point in time, by
    /// replaying the transaction log to resolve the active data files and reading them directly
    /// (no delta extension required). Exactly one of <paramref name="version"/> /
    /// <paramref name="timestamp"/> may be set; if both are null the latest version is resolved.
    /// Throws a documented <c>BACKEND_ERROR</c> for unsupported tables (checkpoints, partitions,
    /// deletion vectors, reader features) or an unsatisfiable version/timestamp.
    /// </summary>
    IReadOnlyList<ColumnSchema> RegisterDeltaVersion(
        string datasetId, string path, long? version, DateTimeOffset? timestamp,
        CancellationToken ct = default);

    /// <summary>
    /// Writes a registered dataset as a Delta Lake table. Supports creating a new table, appending a
    /// new commit, or atomically overwriting an existing target. The DuckDB delta extension is
    /// read-only, so this is implemented by hand-writing <c>_delta_log/</c> JSON commits.
    /// </summary>
    /// <param name="datasetId">Dataset to export.</param>
    /// <param name="path">Output directory (the Delta table root).</param>
    /// <param name="mode">error | append | overwrite.</param>
    /// <param name="partitionByQuoted">Pre-quoted partition column identifiers (or null/empty).</param>
    /// <param name="ct">Cancels the export (see the interface remarks).</param>
    void ExportDelta(string datasetId, string path, string mode, IReadOnlyList<string>? partitionByQuoted, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the DuckDB <c>delta</c> extension can be installed/loaded in this environment.
    /// Used by tests to skip Delta-dependent cases when the extension is unavailable (e.g. offline CI).
    /// </summary>
    bool IsDeltaExtensionAvailable();

    /// <summary>
    /// Reshapes long → wide via a DuckDB PIVOT. Identifiers/aggregate are rendered by the caller.
    /// </summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="fromId">Input dataset id.</param>
    /// <param name="indexQuoted">Quoted index (GROUP BY) columns that remain rows.</param>
    /// <param name="columnQuoted">Quoted column whose distinct values become new columns.</param>
    /// <param name="aggExpr">Rendered aggregate over the value column, e.g. <c>sum("amount")</c>.</param>
    /// <param name="ct">Cancels the pivot (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Pivot(
        string intoId, string fromId, IReadOnlyList<string> indexQuoted, string columnQuoted, string aggExpr,
        CancellationToken ct = default);

    /// <summary>
    /// Reshapes wide → long via a DuckDB UNPIVOT. All identifiers are pre-quoted by the caller.
    /// </summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="fromId">Input dataset id.</param>
    /// <param name="idColumnsQuoted">Quoted columns to keep as row identifiers.</param>
    /// <param name="valueColumnsQuoted">Quoted columns to unpivot.</param>
    /// <param name="nameToQuoted">Quoted output column name for the former value-column name.</param>
    /// <param name="valueToQuoted">Quoted output column name for the value.</param>
    /// <param name="ct">Cancels the unpivot (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Unpivot(
        string intoId, string fromId,
        IReadOnlyList<string> idColumnsQuoted, IReadOnlyList<string> valueColumnsQuoted,
        string nameToQuoted, string valueToQuoted,
        CancellationToken ct = default);

    /// <summary>
    /// Materializes a deterministic reservoir sample of the dataset.
    /// </summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="fromId">Input dataset id.</param>
    /// <param name="n">Reservoir size.</param>
    /// <param name="seed">Deterministic seed for REPEATABLE.</param>
    /// <param name="ct">Cancels the sample (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Sample(
        string intoId, string fromId, int n, long seed, CancellationToken ct = default);

    /// <summary>
    /// Materializes a derived dataset from a SELECT over an existing dataset and returns its schema.
    /// Clause fragments are pre-rendered, injection-safe SQL built from validated identifiers and
    /// escaped literals by the calling operation. Safe even when <paramref name="intoId"/> equals
    /// <paramref name="fromId"/> (the result is materialized before the source is replaced).
    /// </summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="fromId">Input dataset id.</param>
    /// <param name="selectClause">The projection (e.g. <c>*</c> or quoted columns/aggregates).</param>
    /// <param name="whereClause">Optional rendered WHERE clause (without the WHERE keyword).</param>
    /// <param name="groupByClause">Optional rendered GROUP BY clause (without the GROUP BY keyword).</param>
    /// <param name="orderByClause">Optional rendered ORDER BY clause (without the ORDER BY keyword).</param>
    /// <param name="limit">Optional row limit.</param>
    /// <param name="havingClause">Optional rendered HAVING clause (without the HAVING keyword).</param>
    /// <param name="ct">Cancels the derivation (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Derive(
        string intoId, string fromId, string selectClause,
        string? whereClause = null, string? groupByClause = null,
        string? orderByClause = null, int? limit = null,
        string? havingClause = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the SELECT a <see cref="Derive"/> would run,
    /// without materializing it. Used to expose pushdown/pruning via <c>explain=true</c>.
    /// </summary>
    string Explain(
        string fromId, string selectClause,
        string? whereClause = null, string? groupByClause = null,
        string? orderByClause = null, int? limit = null,
        string? havingClause = null, CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the SELECT a <see cref="Join"/> would
    /// materialize, without running it. Parameters mirror <see cref="Join"/>.
    /// </summary>
    string ExplainJoin(
        string leftId, string rightId, string joinTypeSql, string? onSql, string selectClause,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the UNION a <see cref="Union"/> would
    /// materialize, without running it. Parameters mirror <see cref="Union"/>.
    /// </summary>
    string ExplainUnion(IReadOnlyList<string> fromIds, bool byName, bool distinct, CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the PIVOT a <see cref="Pivot"/> would
    /// materialize, without running it. Parameters mirror <see cref="Pivot"/>.
    /// </summary>
    string ExplainPivot(
        string fromId, IReadOnlyList<string> indexQuoted, string columnQuoted, string aggExpr,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the UNPIVOT a <see cref="Unpivot"/> would
    /// materialize, without running it. Parameters mirror <see cref="Unpivot"/>.
    /// </summary>
    string ExplainUnpivot(
        string fromId,
        IReadOnlyList<string> idColumnsQuoted, IReadOnlyList<string> valueColumnsQuoted,
        string nameToQuoted, string valueToQuoted, CancellationToken ct = default);

    /// <summary>
    /// Returns the DuckDB optimized plan (EXPLAIN) for the SAMPLE a <see cref="Sample"/> would
    /// materialize, without running it. Parameters mirror <see cref="Sample"/>.
    /// </summary>
    string ExplainSample(string fromId, int n, long seed, CancellationToken ct = default);

    /// <summary>
    /// Writes a registered dataset to disk via a DuckDB COPY. Format/compression are validated by
    /// the caller; the path and partition columns are rendered as injection-safe literals/identifiers.
    /// </summary>
    /// <param name="datasetId">Dataset to export.</param>
    /// <param name="path">Output file or directory.</param>
    /// <param name="format">"csv", "parquet", or "json" (lower-case).</param>
    /// <param name="partitionByQuoted">Pre-quoted partition column identifiers (or null/empty).</param>
    /// <param name="compression">Validated compression token (or null).</param>
    /// <param name="overwrite">Allow overwriting an existing target directory.</param>
    /// <param name="csvOptions">CSV-specific options; ignored for non-CSV formats.</param>
    /// <param name="array">JSON only: write a top-level array instead of newline-delimited JSON.</param>
    /// <param name="ct">Cancels the export (see the interface remarks).</param>
    void Export(string datasetId, string path, string format,
        IReadOnlyList<string>? partitionByQuoted, string? compression, bool overwrite,
        CsvExportOptions? csvOptions = null, bool array = false,
        CancellationToken ct = default);

    /// <summary>Returns the schema of a registered dataset view.</summary>
    IReadOnlyList<ColumnSchema> Describe(string datasetId, CancellationToken ct = default);

    /// <summary>Returns the total row count of a registered dataset view.</summary>
    long CountRows(string datasetId, CancellationToken ct = default);

    /// <summary>Returns up to <paramref name="limit"/> rows for a preview.</summary>
    /// <param name="datasetId">Id of a registered dataset view.</param>
    /// <param name="mode">"head", "tail", or "sample".</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="seed">Required for "sample"; makes it repeatable.</param>
    /// <param name="rowCount">Known total row count (used for "tail").</param>
    /// <param name="ct">Cancels the preview (see the interface remarks).</param>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Preview(
        string datasetId, string mode, int limit, long? seed, long rowCount,
        CancellationToken ct = default);

    /// <summary>Drops a registered dataset view (no-op if absent).</summary>
    void Drop(string datasetId);

    /// <summary>
    /// Joins two datasets. The result references the left/right relations as aliases <c>l</c> and
    /// <c>r</c>; <paramref name="selectClause"/> and <paramref name="onSql"/> are rendered by the
    /// caller using those aliases. Returns the result schema.
    /// </summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="leftId">Left input dataset id.</param>
    /// <param name="rightId">Right input dataset id.</param>
    /// <param name="joinTypeSql">INNER, LEFT, RIGHT, FULL, SEMI, or ANTI.</param>
    /// <param name="onSql">Rendered ON predicate (null for SEMI/ANTI with no keys is invalid).</param>
    /// <param name="selectClause">Projection referencing aliases <c>l</c>/<c>r</c>.</param>
    /// <param name="ct">Cancels the join (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Join(
        string intoId, string leftId, string rightId, string joinTypeSql, string? onSql, string selectClause,
        CancellationToken ct = default);

    /// <summary>Concatenates datasets (UNION ALL, or UNION when <paramref name="distinct"/>).</summary>
    /// <param name="intoId">Output dataset id.</param>
    /// <param name="fromIds">Input dataset ids, in order.</param>
    /// <param name="byName">Align columns by name rather than position.</param>
    /// <param name="distinct">Drop duplicate rows across the union.</param>
    /// <param name="ct">Cancels the union (see the interface remarks).</param>
    IReadOnlyList<ColumnSchema> Union(
        string intoId, IReadOnlyList<string> fromIds, bool byName, bool distinct,
        CancellationToken ct = default);

    /// <summary>Computes per-column statistics for the given columns (one result row per column).</summary>
    /// <param name="datasetId">Dataset to profile.</param>
    /// <param name="columns">Columns to profile.</param>
    /// <param name="quantiles">Quantiles (0..1) to compute for numeric columns.</param>
    /// <param name="ct">Cancels the profiling (checked between per-column statements too).</param>
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Profile(
        string datasetId, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<double> quantiles,
        CancellationToken ct = default);
}
