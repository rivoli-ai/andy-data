using System.Data;
using System.Diagnostics;
using System.Numerics;
using System.Text.RegularExpressions;
using Andy.Data;
using Andy.Data.Observability;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Backend;

/// <summary>
/// Default <see cref="IDuckDbBackend"/> over a single embedded in-memory DuckDB connection,
/// shared (under a lock) across operations in a session.
/// </summary>
/// <remarks>
/// Each logical <c>dataset_id</c> maps to a unique, internally-generated physical relation name, so
/// the caller-supplied id never reaches SQL, and a transform whose output id equals its input id
/// (<c>into == dataset_id</c>) is not a circular reference. Loaders AND transforms register lazy
/// <c>VIEW</c>s, so a chain (filter → select → group_by) folds into a single DuckDB plan with
/// predicates/projections pushed down to the scan instead of materializing each step. Because views
/// can depend on the relation they were derived from, every physical's dependencies are tracked and
/// a drop/remap sweeps only relations no longer reachable from any mapped id (views are cheap
/// metadata, but join/union/pivot create materialized <c>TABLE</c>s whose memory must actually be
/// reclaimed). File paths and option values are inlined as escaped string literals (DuckDB cannot
/// prepare DDL); a DuckDB string literal cannot break out of its quotes, so this is injection-safe.
/// </remarks>
public sealed class DuckDbBackend : IDuckDbBackend
{
    private static readonly Regex DatasetIdRegex = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    /// <summary>
    /// Lazy-view chains deeper than this are checkpointed: the next derivation materializes a
    /// TABLE snapshot instead of stacking another view, so re-executing the chain for each
    /// count/preview stays O(depth-bounded) instead of re-scanning the whole history every step.
    /// </summary>
    private const int ChainCheckpointDepth = 8;

    private readonly DuckDBConnection _connection;
    private readonly Dictionary<string, string> _physical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelationInfo> _relations = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ILogger<DuckDbBackend>? _logger;
    private long _counter;
    private bool _deltaLoaded;
    private long _appliedMemoryBytes;
    private string? _spillDir;
    private bool _disposed;

    /// <summary>Lifecycle bookkeeping for one physical relation (see <see cref="SweepLocked"/>).
    /// <c>Depth</c> counts stacked views above the nearest materialized/loaded relation.</summary>
    private sealed record RelationInfo(long Seq, bool IsTable, IReadOnlyList<string> Parents, int Depth);

    /// <summary>
    /// Creates a new in-memory DuckDB backend. Logging is optional; pass <c>null</c> (or omit the
    /// parameter) to run without logging.
    /// </summary>
    public DuckDbBackend(ILogger<DuckDbBackend>? logger = null)
    {
        _logger = logger;
        _connection = new DuckDBConnection("Data Source=:memory:");
        _connection.Open();
    }

    public IReadOnlyList<ColumnSchema> RegisterCsv(
        string datasetId, CsvLoadOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateId(datasetId);
        lock (_gate)
        {
            var phys = NewName();
            Exec($"CREATE VIEW {Quote(phys)} AS SELECT * FROM {BuildCsvRead(options)}", ct);
            Track(phys, isTable: false);
            Map(datasetId, phys);
            return DescribeLocked(phys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> RegisterParquet(
        string datasetId, string path, bool? hivePartitioning = null, bool? unionByName = null,
        CancellationToken ct = default)
    {
        ValidateId(datasetId);
        lock (_gate)
        {
            var opts = new List<string>();
            if (hivePartitioning is not null) opts.Add($"hive_partitioning={(hivePartitioning.Value ? "true" : "false")}");
            if (unionByName is not null) opts.Add($"union_by_name={(unionByName.Value ? "true" : "false")}");
            var args = opts.Count > 0 ? $"{Lit(path)}, {string.Join(", ", opts)}" : Lit(path);

            var phys = NewName();
            Exec($"CREATE VIEW {Quote(phys)} AS SELECT * FROM read_parquet({args})", ct);
            Track(phys, isTable: false);
            Map(datasetId, phys);
            return DescribeLocked(phys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> RegisterJson(
        string datasetId, string path, string? format = null, CancellationToken ct = default)
    {
        ValidateId(datasetId);
        lock (_gate)
        {
            // format is a fixed token validated by LoadJsonTool; the path is an escaped literal.
            var read = format is null or "auto"
                ? $"read_json_auto({Lit(path)})"
                : $"read_json({Lit(path)}, auto_detect=true, format={Lit(format)})";
            var phys = NewName();
            Exec($"CREATE VIEW {Quote(phys)} AS SELECT * FROM {read}", ct);
            Track(phys, isTable: false);
            Map(datasetId, phys);
            return DescribeLocked(phys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> Derive(
        string intoId, string fromId, string selectClause,
        string? whereClause = null, string? groupByClause = null,
        string? orderByClause = null, int? limit = null,
        string? havingClause = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var fromPhys = Require(fromId);
            var intoPhys = NewName();
            ValidateId(intoId);
            var select = BuildSelect(fromPhys, selectClause, whereClause, groupByClause, orderByClause, limit, havingClause);
            // Lazy view (not CREATE TABLE … AS): a chain of transforms folds into one DuckDB plan
            // with predicates/projections pushed down to the underlying scan. intoPhys is a fresh
            // unique name, so this is correct even when intoId == fromId (in-place replace):
            // intoPhys = a view SELECTing FROM fromPhys, and Map below remaps the id WITHOUT
            // dropping fromPhys (which the new view still depends on). Past ChainCheckpointDepth
            // the step is materialized instead, bounding how much of the chain every later
            // count/preview re-executes (sources are files, so the snapshot stays correct).
            var fromDepth = _relations.TryGetValue(fromPhys, out var fromInfo) ? fromInfo.Depth : 0;
            if (fromDepth + 1 > ChainCheckpointDepth)
            {
                Exec($"CREATE TABLE {Quote(intoPhys)} AS {select}", ct);
                Track(intoPhys, isTable: true);
            }
            else
            {
                Exec($"CREATE VIEW {Quote(intoPhys)} AS {select}", ct);
                Track(intoPhys, isTable: false, fromPhys);
            }

            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public string Explain(
        string fromId, string selectClause,
        string? whereClause = null, string? groupByClause = null,
        string? orderByClause = null, int? limit = null,
        string? havingClause = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(
                BuildSelect(Require(fromId), selectClause, whereClause, groupByClause, orderByClause, limit, havingClause), ct);
        }
    }

    public string ExplainJoin(
        string leftId, string rightId, string joinTypeSql, string? onSql, string selectClause,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(BuildJoinSelect(Require(leftId), Require(rightId), joinTypeSql, onSql, selectClause), ct);
        }
    }

    public string ExplainUnion(IReadOnlyList<string> fromIds, bool byName, bool distinct, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(BuildUnionSelectLocked(fromIds, byName, distinct), ct);
        }
    }

    public string ExplainPivot(
        string fromId, IReadOnlyList<string> indexQuoted, string columnQuoted, string aggExpr,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(BuildPivotSelect(Require(fromId), indexQuoted, columnQuoted, aggExpr), ct);
        }
    }

    /// <summary>Runs EXPLAIN over a rendered statement and returns the plan tree text.</summary>
    private string ExplainLocked(string sql, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"EXPLAIN {sql}";
        return ExecuteCancellable(cmd, ct, () =>
        {
            using var reader = cmd.ExecuteReader();
            var lines = new List<string>();
            while (reader.Read())
            {
                // EXPLAIN yields rows of (explain_key, explain_value); the value holds the plan tree.
                lines.Add(reader[reader.FieldCount - 1]?.ToString() ?? string.Empty);
            }

            return string.Join("\n", lines);
        });
    }

    private string BuildSelect(
        string fromPhys, string selectClause, string? whereClause,
        string? groupByClause, string? orderByClause, int? limit,
        string? havingClause = null)
    {
        var sql = $"SELECT {selectClause} FROM {Quote(fromPhys)}";
        if (whereClause is not null) sql += $" WHERE {whereClause}";
        if (groupByClause is not null) sql += $" GROUP BY {groupByClause}";
        if (havingClause is not null) sql += $" HAVING {havingClause}";
        if (orderByClause is not null) sql += $" ORDER BY {orderByClause}";
        if (limit is not null) sql += $" LIMIT {limit.Value}";
        return sql;
    }

    /// <summary>Renders the SELECT both Join and ExplainJoin use (physical names, aliases l/r).</summary>
    private static string BuildJoinSelect(
        string leftPhys, string rightPhys, string joinTypeSql, string? onSql, string selectClause)
    {
        var sql = $"SELECT {selectClause} FROM {Quote(leftPhys)} AS l {joinTypeSql} JOIN {Quote(rightPhys)} AS r";
        if (onSql is not null) sql += $" ON {onSql}";
        return sql;
    }

    private static string BuildUnpivotSelect(
        string fromPhys, IReadOnlyList<string> idColumnsQuoted, IReadOnlyList<string> valueColumnsQuoted,
        string nameToQuoted, string valueToQuoted)
    {
        var projections = idColumnsQuoted.Concat(valueColumnsQuoted).ToList();
        var select = projections.Count > 0 ? string.Join(", ", projections) : "*";
        return $"UNPIVOT (SELECT {select} FROM {Quote(fromPhys)}) ON {string.Join(", ", valueColumnsQuoted)} INTO NAME {nameToQuoted} VALUE {valueToQuoted}";    
    }

    private static string BuildSampleSelect(string fromPhys, int n, long seed)
        => $"SELECT * FROM {Quote(fromPhys)} USING SAMPLE reservoir({n}) REPEATABLE({seed})";

    /// <summary>Renders the UNION both Union and ExplainUnion use. Must be called under _gate.</summary>
    private string BuildUnionSelectLocked(IReadOnlyList<string> fromIds, bool byName, bool distinct)
    {
        var op = (distinct ? "UNION" : "UNION ALL") + (byName ? " BY NAME" : string.Empty);
        var selects = fromIds.Select(id => $"SELECT * FROM {Quote(Require(id))}");
        return string.Join($" {op} ", selects);
    }

    /// <summary>Renders the PIVOT both Pivot and ExplainPivot use (physical name).</summary>
    private static string BuildPivotSelect(
        string fromPhys, IReadOnlyList<string> indexQuoted, string columnQuoted, string aggExpr)
        => $"PIVOT {Quote(fromPhys)} ON {columnQuoted} USING {aggExpr} GROUP BY {string.Join(", ", indexQuoted)}";

    public IReadOnlyList<ColumnSchema> Describe(string datasetId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return DescribeLocked(Require(datasetId), ct);
        }
    }

    public long CountRows(string datasetId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {Quote(Require(datasetId))}";
            return Convert.ToInt64(ExecuteCancellable(cmd, ct, cmd.ExecuteScalar));
        }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Preview(
        string datasetId, string mode, int limit, long? seed, long rowCount,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = Quote(Require(datasetId));
            // limit/offset/seed are integers (validated upstream), safe to embed.
            var sql = mode switch
            {
                "tail" => $"SELECT * FROM {q} OFFSET {Math.Max(0, rowCount - limit)} LIMIT {limit}",
                "sample" => $"SELECT * FROM {q} USING SAMPLE reservoir({limit} ROWS) REPEATABLE({seed ?? 0})",
                _ => $"SELECT * FROM {q} LIMIT {limit}",
            };

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            return ExecuteCancellable(cmd, ct, () =>
            {
                using var reader = cmd.ExecuteReader();
                return ReadAll(reader);
            });
        }
    }

    public void Drop(string datasetId)
    {
        lock (_gate)
        {
            // Unmap the id, then reclaim every physical no longer reachable from a mapped id.
            // Reachability (not CASCADE) is what keeps datasets derived from this one working:
            // their views' ancestors stay alive; only truly orphaned relations are dropped.
            _physical.Remove(datasetId);
            SweepLocked();
        }
    }

    public void Export(string datasetId, string path, string format,
        IReadOnlyList<string>? partitionByQuoted, string? compression, bool overwrite,
        CsvExportOptions? csvOptions = null, bool array = false,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var phys = Require(datasetId);
            var options = new List<string> { $"FORMAT {format.ToUpperInvariant()}" };
            if (partitionByQuoted is { Count: > 0 })
            {
                options.Add($"PARTITION_BY ({string.Join(", ", partitionByQuoted)})");
            }

            if (!string.IsNullOrEmpty(compression))
            {
                options.Add($"COMPRESSION {compression.ToUpperInvariant()}");
            }

            if (csvOptions is not null && format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                if (csvOptions.Header is not null)
                {
                    options.Add($"HEADER {(csvOptions.Header.Value ? "true" : "false")}");
                }

                if (csvOptions.Delimiter is not null)
                {
                    options.Add($"DELIM {Lit(csvOptions.Delimiter)}");
                }

                if (csvOptions.Quote is not null)
                {
                    options.Add($"QUOTE {Lit(csvOptions.Quote)}");
                }

                if (csvOptions.Escape is not null)
                {
                    options.Add($"ESCAPE {Lit(csvOptions.Escape)}");
                }
            }

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) && array)
            {
                options.Add("ARRAY true");
            }

            if (overwrite)
            {
                options.Add("OVERWRITE_OR_IGNORE");
            }

            Exec($"COPY (SELECT * FROM {Quote(phys)}) TO {Lit(path)} ({string.Join(", ", options)})", ct);
        }
    }

    public IReadOnlyList<ColumnSchema> Join(
        string intoId, string leftId, string rightId, string joinTypeSql, string? onSql, string selectClause,
        CancellationToken ct = default)
    {
        ValidateId(intoId);
        lock (_gate)
        {
            var l = Require(leftId);
            var r = Require(rightId);
            var intoPhys = NewName();
            Exec($"CREATE TABLE {Quote(intoPhys)} AS {BuildJoinSelect(l, r, joinTypeSql, onSql, selectClause)}", ct);
            Track(intoPhys, isTable: true); // materialized: independent of its inputs from here on
            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> Union(
        string intoId, IReadOnlyList<string> fromIds, bool byName, bool distinct,
        CancellationToken ct = default)
    {
        ValidateId(intoId);
        lock (_gate)
        {
            var intoPhys = NewName();
            Exec($"CREATE TABLE {Quote(intoPhys)} AS {BuildUnionSelectLocked(fromIds, byName, distinct)}", ct);
            Track(intoPhys, isTable: true);
            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Profile(
        string datasetId, IReadOnlyList<ColumnSchema> columns, IReadOnlyList<double> quantiles,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            var phys = Quote(Require(datasetId));
            var rows = new List<IReadOnlyDictionary<string, object?>>(columns.Count);
            foreach (var col in columns)
            {
                var q = Quote(col.Name);
                var numeric = IsNumeric(col.Type);
                var minMax = numeric || CanMinMax(col.Type);

                var exprs = new List<string>
                {
                    "count(*) AS n",
                    $"count({q}) AS non_null",
                    $"approx_count_distinct({q}) AS distinct_count",
                };
                if (minMax)
                {
                    exprs.Add($"min({q}) AS mn");
                    exprs.Add($"max({q}) AS mx");
                }

                if (numeric)
                {
                    exprs.Add($"avg({q}) AS mean_v");
                    exprs.Add($"stddev_samp({q}) AS std_v");
                }

                var quantileAliases = new List<(string alias, double p)>();
                if (numeric)
                {
                    for (var i = 0; i < quantiles.Count; i++)
                    {
                        var alias = $"q{i}";
                        quantileAliases.Add((alias, quantiles[i]));
                        exprs.Add($"quantile_cont({q}, {quantiles[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture)}) AS {alias}");
                    }
                }

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = $"SELECT {string.Join(", ", exprs)} FROM {phys}";
                // ExecuteCancellable also checks ct between per-column statements.
                var stats = ExecuteCancellable(cmd, ct, () =>
                {
                    using var reader = cmd.ExecuteReader();
                    reader.Read();

                    var n = Convert.ToInt64(reader["n"]);
                    var nonNull = Convert.ToInt64(reader["non_null"]);
                    var row = new Dictionary<string, object?>
                    {
                        ["column"] = col.Name,
                        ["type"] = col.Type,
                        ["null_count"] = n - nonNull,
                        ["distinct_count"] = Normalize(reader["distinct_count"]),
                        ["count"] = nonNull,
                        ["mean"] = numeric ? Normalize(reader["mean_v"]) : null,
                        ["std"] = numeric ? Normalize(reader["std_v"]) : null,
                        ["min"] = minMax ? Normalize(reader["mn"]) : null,
                        ["max"] = minMax ? Normalize(reader["mx"]) : null,
                    };
                    foreach (var (alias, p) in quantileAliases)
                    {
                        // "R" (round-trippable) keys: "0.###" would collide distinct quantiles
                        // (e.g. 0.25 and 0.2501 both rendered "q_0.25").
                        row[$"q_{p.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"] = Normalize(reader[alias]);
                    }

                    return row;
                });

                rows.Add(stats);
            }

            return rows;
        }
    }

    public IReadOnlyList<ColumnSchema> RegisterDelta(string datasetId, string path, CancellationToken ct = default)
    {
        ValidateId(datasetId);
        lock (_gate)
        {
            EnsureDeltaExtension();
            var phys = NewName();
            // delta_scan reads the latest snapshot. This DuckDB delta extension exposes no
            // version/timestamp parameter, so time travel is not available (see LoadDeltaTool).
            Exec($"CREATE VIEW {Quote(phys)} AS SELECT * FROM delta_scan({Lit(path)})", ct);
            Track(phys, isTable: false);
            Map(datasetId, phys);
            return DescribeLocked(phys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> RegisterDeltaVersion(
        string datasetId, string path, long? version, DateTimeOffset? timestamp,
        CancellationToken ct = default)
    {
        ValidateId(datasetId);
        lock (_gate)
        {
            // Resolve the active data files at the requested version/timestamp by replaying the
            // transaction log ourselves, then read them as plain Parquet. This needs no delta
            // extension (it works offline) because the delta extension exposes no time travel.
            var snapshot = DeltaLog.ReadSnapshot(path, version, timestamp, ct);
            var phys = NewName();
            var fileList = string.Join(", ", snapshot.AbsoluteFilePaths.Select(Lit));
            Exec($"CREATE VIEW {Quote(phys)} AS SELECT * FROM read_parquet([{fileList}], union_by_name=true)", ct);
            Track(phys, isTable: false);
            Map(datasetId, phys);
            return DescribeLocked(phys, ct);
        }
    }

    public void ExportDelta(
        string datasetId, string path, string mode, IReadOnlyList<string>? partitionByQuoted, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var phys = Require(datasetId);
            var schema = DescribeLocked(phys, ct);

            // Fail before any I/O if the schema cannot be expressed in Delta — exporting first and
            // discovering this while writing the log would leave a corrupt half-table behind.
            DeltaLog.EnsureExportable(schema);

            var target = Path.GetFullPath(path);

            switch (mode.ToLowerInvariant())
            {
                case "error":
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        throw new DataFrameException(DataFrameErrorCodes.TargetExists,
                            $"Target '{path}' already exists; set mode=overwrite to replace it or mode=append to add to a Delta table.",
                            new Dictionary<string, object?> { ["path"] = path });
                    }

                    WriteNewDeltaTable(target, phys, schema, partitionByQuoted, ct);
                    break;

                case "overwrite":
                    WriteNewDeltaTable(target, phys, schema, partitionByQuoted, ct);
                    break;

                case "append":
                    AppendToDeltaTable(target, phys, schema, partitionByQuoted, ct);
                    break;

                default:
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        $"Unknown Delta export mode '{mode}'. Use error, append, or overwrite.");
            }
        }
    }

    private void WriteNewDeltaTable(
        string target, string phys, IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string>? partitionByQuoted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Build the complete table in a temp sibling, then swap it in. The existing target is
        // only removed after the new table is fully written, so a failure mid-export (disk
        // full, interrupted write) never destroys the original or leaves a half-table at path.
        var staging = $"{target}.tmp-{Guid.NewGuid():N}";
        var displaced = $"{target}.old-{Guid.NewGuid():N}";
        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(staging);

            var partitionColumns = partitionByQuoted ?? Array.Empty<string>();
            var adds = partitionColumns.Count > 0
                ? WritePartitionedDataFiles(staging, phys, partitionColumns, schema, ct)
                : new[] { WriteUnpartitionedDataFile(staging, phys, schema, ct) };

            ct.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            var partitionNames = partitionColumns.Select(UnquoteIdent).ToList();
            DeltaLog.WriteNewTable(staging, schema, partitionNames, adds, now, ct);

            ct.ThrowIfCancellationRequested();

            if (Path.GetDirectoryName(target) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent); // Directory.Move requires an existing parent
            }

            using var lease = DeltaLog.AcquireTableLock(target);
            lock (lease.Gate)
            {
                if (Directory.Exists(target))
                {
                    Directory.Move(target, displaced);
                    try
                    {
                        Directory.Move(staging, target);
                    }
                    catch
                    {
                        Directory.Move(displaced, target); // restore the original table
                        try { Directory.Delete(displaced, recursive: true); } catch { /* best effort */ }
                        throw;
                    }

                    Directory.Delete(displaced, recursive: true);
                }
                else
                {
                    Directory.Move(staging, target);
                }
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private void AppendToDeltaTable(
        string target, string phys, IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string>? partitionByQuoted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var lease = DeltaLog.AcquireTableLock(target);
        lock (lease.Gate)
        {
            ct.ThrowIfCancellationRequested();

            var partitionColumns = partitionByQuoted ?? Array.Empty<string>();
            var adds = partitionColumns.Count > 0
                ? WritePartitionedDataFiles(target, phys, partitionColumns, schema, ct)
                : new[] { WriteUnpartitionedDataFile(target, phys, schema, ct) };

            ct.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;
            DeltaLog.AppendCommit(target, schema, adds, now, partitionColumns, ct);
        }
    }

    private DeltaLog.AddFile WriteUnpartitionedDataFile(
        string tableRoot, string phys, IReadOnlyList<ColumnSchema> schema, CancellationToken ct, string? dataFile = null)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(tableRoot);

        dataFile ??= $"part-00000-{Guid.NewGuid()}-c000.snappy.parquet";
        var dataPath = Path.Combine(tableRoot, dataFile);
        Exec($"COPY (SELECT * FROM {Quote(phys)}) TO {Lit(dataPath)} (FORMAT PARQUET, COMPRESSION SNAPPY)", ct);

        ct.ThrowIfCancellationRequested();
        var stats = ComputeStats(Quote(phys), schema, ct, out var rows);

        return new DeltaLog.AddFile(
            dataFile,
            new FileInfo(dataPath).Length,
            rows,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Stats: stats);
    }

    private IReadOnlyList<DeltaLog.AddFile> WritePartitionedDataFiles(
        string tableRoot, string phys, IReadOnlyList<string> partitionColumnsQuoted,
        IReadOnlyList<ColumnSchema> schema, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(tableRoot);

        var tmp = Path.Combine(tableRoot, $".tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);

        try
        {
            var cols = string.Join(", ", partitionColumnsQuoted);
            Exec($"COPY (SELECT * FROM {Quote(phys)}) TO {Lit(tmp)} (FORMAT PARQUET, PARTITION_BY ({cols}), COMPRESSION SNAPPY)", ct);

            ct.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(tmp, "*.parquet", SearchOption.AllDirectories).ToList();
            var adds = new List<DeltaLog.AddFile>(files.Count);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(tmp, file).Replace(Path.DirectorySeparatorChar, '/');
                var partitionValues = ParsePartitionValues(rel, partitionColumnsQuoted);
                var stats = ComputeStats($"read_parquet({Lit(file)})", schema, ct, out var rowCount);
                adds.Add(new DeltaLog.AddFile(
                    rel,
                    new FileInfo(file).Length,
                    rowCount,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    partitionValues,
                    stats));
            }

            ct.ThrowIfCancellationRequested();

            // Move the new files into the table root, preserving the Hive directory structure.
            // If a generated filename collides with an existing file (appends), rename it uniquely.
            for (var i = 0; i < adds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var add = adds[i];
                var src = Path.Combine(tmp, add.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var dest = Path.Combine(tableRoot, add.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (File.Exists(dest))
                {
                    var dir = Path.GetDirectoryName(dest)!;
                    var ext = Path.GetExtension(dest);
                    var name = Path.GetFileNameWithoutExtension(dest);
                    var unique = $"{name}_{Guid.NewGuid():N}{ext}";
                    dest = Path.Combine(dir, unique);

                    // Patch the recorded relative path to match the renamed file.
                    var relDir = Path.GetDirectoryName(add.RelativePath)?.Replace(Path.DirectorySeparatorChar, '/');
                    var newRel = string.IsNullOrEmpty(relDir) ? unique : $"{relDir}/{unique}";
                    adds[i] = add with { RelativePath = newRel };
                }

                File.Move(src, dest);
            }

            return adds;
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private IReadOnlyDictionary<string, DeltaLog.ColumnStats> ComputeStats(
        string fromSql, IReadOnlyList<ColumnSchema> schema, CancellationToken ct, out long rowCount)
    {
        var aggCols = new List<string> { $"count(*) AS {Quote("__rows__")}" };
        var columnInfo = new List<(ColumnSchema column, int nullIdx, int? minIdx, int? maxIdx)>();
        var idx = 1;

        foreach (var column in schema)
        {
            aggCols.Add($"count(*) - count({Quote(column.Name)}) AS {Quote(column.Name + "_nulls")}");
            var nullIdx = idx++;
            int? minIdx = null;
            int? maxIdx = null;

            if (SupportsStatsMinMax(column.Type))
            {
                aggCols.Add($"min({Quote(column.Name)}) AS {Quote(column.Name + "_min")}");
                minIdx = idx++;
                aggCols.Add($"max({Quote(column.Name)}) AS {Quote(column.Name + "_max")}");
                maxIdx = idx++;
            }

            columnInfo.Add((column, nullIdx, minIdx, maxIdx));
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", aggCols)} FROM {fromSql}";
        ct.ThrowIfCancellationRequested();
        using var reader = cmd.ExecuteReader();
        reader.Read();

        rowCount = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0));
        var stats = new Dictionary<string, DeltaLog.ColumnStats>(schema.Count);
        foreach (var (column, nullIdx, minIdx, maxIdx) in columnInfo)
        {
            var nullCount = reader.IsDBNull(nullIdx) ? 0L : Convert.ToInt64(reader.GetValue(nullIdx));
            object? min = null;
            object? max = null;
            if (minIdx.HasValue && !reader.IsDBNull(minIdx.Value))
            {
                min = FormatStatsValue(reader.GetValue(minIdx.Value), column.Type);
            }

            if (maxIdx.HasValue && !reader.IsDBNull(maxIdx.Value))
            {
                max = FormatStatsValue(reader.GetValue(maxIdx.Value), column.Type);
            }

            stats[column.Name] = new DeltaLog.ColumnStats(min, max, nullCount);
        }

        return stats;
    }

    private static bool SupportsStatsMinMax(string type) => IsNumeric(type) || CanMinMax(type);

    private static object? FormatStatsValue(object? value, string type)
    {
        value = Normalize(value);
        var t = type.Trim().ToUpperInvariant();
        return value switch
        {
            DateTime dt when t.StartsWith("TIMESTAMP") => dt.ToString("O"),
            DateTime dt when t.StartsWith("DATE") => dt.ToString("yyyy-MM-dd"),
            TimeSpan ts when t.StartsWith("TIME") => ts.ToString("c"),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            DateTimeOffset dto => t.StartsWith("TIMESTAMP") ? dto.ToString("O") : dto.ToString("yyyy-MM-dd"),
            _ => value,
        };
    }

    private static Dictionary<string, string> ParsePartitionValues(string relativePath, IReadOnlyList<string> partitionColumnsQuoted)
    {
        var parts = relativePath.Split('/');
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < partitionColumnsQuoted.Count && i < parts.Length; i++)
        {
            var part = parts[i];
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.BackendError,
                    $"Unexpected partition directory '{part}' in exported Delta path '{relativePath}'.");
            }

            var name = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            values[name] = value;
        }

        var expected = partitionColumnsQuoted.Select(UnquoteIdent).ToList();
        foreach (var name in expected)
        {
            if (!values.ContainsKey(name))
            {
                throw new DataFrameException(DataFrameErrorCodes.BackendError,
                    $"Missing partition value for column '{name}' in exported Delta path '{relativePath}'.");
            }
        }

        return values;
    }

    private static string UnquoteIdent(string quoted)
    {
        if (quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"')
        {
            return quoted[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return quoted;
    }

    public bool IsDeltaExtensionAvailable()
    {
        lock (_gate)
        {
            try
            {
                EnsureDeltaExtension();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public IReadOnlyList<ColumnSchema> Pivot(
        string intoId, string fromId, IReadOnlyList<string> indexQuoted, string columnQuoted, string aggExpr,
        CancellationToken ct = default)
    {
        ValidateId(intoId);
        lock (_gate)
        {
            var from = Require(fromId);
            var intoPhys = NewName();
            Exec($"CREATE TABLE {Quote(intoPhys)} AS {BuildPivotSelect(from, indexQuoted, columnQuoted, aggExpr)}", ct);
            Track(intoPhys, isTable: true);
            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public IReadOnlyList<ColumnSchema> Unpivot(
        string intoId, string fromId,
        IReadOnlyList<string> idColumnsQuoted, IReadOnlyList<string> valueColumnsQuoted,
        string nameToQuoted, string valueToQuoted,
        CancellationToken ct = default)
    {
        ValidateId(intoId);
        lock (_gate)
        {
            var from = Require(fromId);
            var intoPhys = NewName();
            Exec($"CREATE TABLE {Quote(intoPhys)} AS {BuildUnpivotSelect(from, idColumnsQuoted, valueColumnsQuoted, nameToQuoted, valueToQuoted)}", ct);
            Track(intoPhys, isTable: true);
            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public string ExplainUnpivot(
        string fromId,
        IReadOnlyList<string> idColumnsQuoted, IReadOnlyList<string> valueColumnsQuoted,
        string nameToQuoted, string valueToQuoted, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(BuildUnpivotSelect(Require(fromId), idColumnsQuoted, valueColumnsQuoted, nameToQuoted, valueToQuoted), ct);
        }
    }

    public IReadOnlyList<ColumnSchema> Sample(
        string intoId, string fromId, int n, long seed, CancellationToken ct = default)
    {
        ValidateId(intoId);
        lock (_gate)
        {
            var from = Require(fromId);
            var intoPhys = NewName();
            Exec($"CREATE TABLE {Quote(intoPhys)} AS {BuildSampleSelect(from, n, seed)}", ct);
            Track(intoPhys, isTable: true);
            Map(intoId, intoPhys);
            return DescribeLocked(intoPhys, ct);
        }
    }

    public string ExplainSample(string fromId, int n, long seed, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return ExplainLocked(BuildSampleSelect(Require(fromId), n, seed), ct);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Dispose the connection under _gate, not after releasing it. Every statement runs while
            // holding _gate for its full duration (the result set is materialized before the lock is
            // released), so tearing the connection down inside the lock cannot race a query still
            // executing on another thread — disposal waits for the in-flight statement to finish. A
            // call that arrives after disposal acquires _gate, finds the connection disposed, and
            // surfaces ObjectDisposedException, the expected contract for using a disposed backend.
            _connection.Dispose();
            if (_spillDir is not null)
            {
                try { Directory.Delete(_spillDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private void EnsureDeltaExtension()
    {
        if (_deltaLoaded)
        {
            return;
        }

        try { Exec("INSTALL delta"); } catch { /* may already be installed/bundled, or offline */ }
        try
        {
            Exec("LOAD delta");
        }
        catch (Exception ex)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "The DuckDB 'delta' extension is unavailable (could not INSTALL/LOAD it). " +
                "It must be installed once with network access, or be bundled with the DuckDB build.",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
        }

        _deltaLoaded = true;
    }

    private static bool IsNumeric(string type)
    {
        var t = type.ToUpperInvariant();
        return t.Contains("INT") || t.StartsWith("DEC") || t.StartsWith("NUMERIC")
            || t is "FLOAT" or "DOUBLE" or "REAL";
    }

    private static bool CanMinMax(string type)
    {
        var t = type.ToUpperInvariant();
        return t.StartsWith("VARCHAR") || t.StartsWith("CHAR") || t == "BOOLEAN"
            || t.StartsWith("DATE") || t.StartsWith("TIME") || t.StartsWith("TIMESTAMP") || t == "UUID";
    }

    // ---- helpers (all called under _gate) --------------------------------

    private string Require(string datasetId) =>
        _physical.TryGetValue(datasetId, out var phys)
            ? phys
            : throw new DataFrameException(DataFrameErrorCodes.DatasetNotFound,
                $"Dataset '{datasetId}' is not registered.",
                new Dictionary<string, object?> { ["dataset_id"] = datasetId });

    private void Map(string datasetId, string newPhysical)
    {
        // The previous physical is not dropped eagerly on remap: with lazy views, the new physical
        // may reference the old one (the common case for in-place replace: into == fromId, where
        // the new view SELECTs FROM the old one). The sweep reclaims it only once nothing mapped
        // depends on it anymore — which matters for the materialized TABLEs join/union/pivot create.
        _physical[datasetId] = newPhysical;
        SweepLocked();
    }

    /// <summary>Records lifecycle info for a freshly created physical relation.</summary>
    private void Track(string phys, bool isTable, params string[] parents)
    {
        var depth = isTable || parents.Length == 0
            ? 0
            : parents.Max(p => _relations.TryGetValue(p, out var info) ? info.Depth : 0) + 1;
        _relations[phys] = new RelationInfo(_counter, isTable, parents, depth);
    }

    /// <inheritdoc />
    public T RunExclusive<T>(Func<T> body)
    {
        // Monitor is reentrant, so backend methods called inside body re-acquire _gate safely.
        lock (_gate)
        {
            return body();
        }
    }

    /// <inheritdoc />
    public void ApplyResourceLimits(long maxMemoryBytes)
    {
        if (maxMemoryBytes <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_appliedMemoryBytes == maxMemoryBytes)
            {
                return;
            }

            // Spilling from an in-memory database needs an explicit temp_directory; without one,
            // hitting memory_limit fails the query instead of spilling.
            if (_spillDir is null)
            {
                _spillDir = Path.Combine(Path.GetTempPath(), "andf_spill_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_spillDir);
                Exec($"SET temp_directory={Lit(_spillDir)}");
            }

            var mb = Math.Max(1, maxMemoryBytes / (1024 * 1024));
            Exec($"SET memory_limit='{mb}MB'");
            _appliedMemoryBytes = maxMemoryBytes;
        }
    }

    /// <summary>
    /// Drops every physical relation not reachable (via view dependencies) from a mapped dataset id.
    /// Dependents are dropped before their parents (descending creation order — a view is always
    /// created after the relations it references).
    /// </summary>
    private void SweepLocked()
    {
        var alive = new HashSet<string>(_physical.Values, StringComparer.Ordinal);
        var pending = new Stack<string>(alive);
        while (pending.Count > 0)
        {
            if (_relations.TryGetValue(pending.Pop(), out var info))
            {
                foreach (var parent in info.Parents)
                {
                    if (alive.Add(parent))
                    {
                        pending.Push(parent);
                    }
                }
            }
        }

        var dead = _relations.Where(kv => !alive.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value.Seq)
            .ToList();
        foreach (var (phys, info) in dead)
        {
            Exec($"DROP {(info.IsTable ? "TABLE" : "VIEW")} IF EXISTS {Quote(phys)}");
            _relations.Remove(phys);
        }
    }

    /// <summary>Test hook: the engine's current memory_limit setting, as DuckDB renders it.</summary>
    internal string CurrentMemoryLimit()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT current_setting('memory_limit')";
            return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
        }
    }

    /// <summary>Test hook: physical relations (tables + non-internal views) live in the connection.</summary>
    internal long CountBackendRelations()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT (SELECT count(*) FROM duckdb_tables()) + (SELECT count(*) FROM duckdb_views() WHERE NOT internal)";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    private static void ValidateId(string datasetId)
    {
        if (string.IsNullOrEmpty(datasetId) || !DatasetIdRegex.IsMatch(datasetId))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                $"Invalid dataset id '{datasetId}'. Use letters, digits, and underscores; must start with a letter or underscore.",
                new Dictionary<string, object?> { ["dataset_id"] = datasetId });
        }
    }

    private string NewName() => $"df_{Interlocked.Increment(ref _counter):x}";

    private void Exec(string sql, CancellationToken ct = default)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        ExecuteCancellable(cmd, ct, cmd.ExecuteNonQuery);
    }

    /// <summary>
    /// Runs one statement under cancellation: checks the token at entry, and arranges in-flight
    /// interruption via DuckDB.NET's <see cref="DuckDBCommand.Cancel"/> (DuckDB's interrupt API).
    /// An interrupted statement surfaces as a DuckDB error, so any failure while the token is
    /// cancelled is rethrown as <see cref="OperationCanceledException"/>. The registration is
    /// disposed as soon as the statement completes so a late-firing token cannot interrupt a
    /// later statement.
    /// <para>
    /// Thread-safety: the cancellation callback runs <see cref="DuckDBCommand.Cancel"/> on the
    /// thread that cancels the token, while the statement itself runs on the thread holding
    /// <c>_gate</c>. This is safe by design — <c>Cancel()</c> maps to the C-API
    /// <c>duckdb_interrupt</c>, which only sets an atomic interrupt flag on the running query's
    /// context (the executing thread polls it); it is the documented way to interrupt a query
    /// from another thread and does not mutate connection/command state shared with the executor.
    /// </para>
    /// </summary>
    private T ExecuteCancellable<T>(DuckDBCommand cmd, CancellationToken ct, Func<T> run)
    {
        ct.ThrowIfCancellationRequested();

        var sql = cmd.CommandText;
        using var activity = DataFrameActivitySource.Instance.StartActivity(DataFrameActivitySource.SqlExecute);
        activity?.SetTag("dataframe.sql.statement", sql);
        _logger?.LogDebug("Executing DuckDB SQL: {Sql}", sql);

        using var registration = ct.CanBeCanceled
            ? ct.Register(static c => { try { ((DuckDBCommand)c!).Cancel(); } catch { /* best effort */ } }, cmd)
            : default;
        try
        {
            var result = run();
            _logger?.LogDebug("DuckDB SQL completed successfully.");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            _logger?.LogWarning("DuckDB SQL was cancelled: {Sql}", sql);
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw new OperationCanceledException("The DuckDB statement was interrupted by cancellation.", ex, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DuckDB SQL execution failed: {Sql}", sql);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private IReadOnlyList<ColumnSchema> DescribeLocked(string phys, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"DESCRIBE SELECT * FROM {Quote(phys)}";
        return ExecuteCancellable(cmd, ct, () =>
        {
            using var reader = cmd.ExecuteReader();
            var columns = new List<ColumnSchema>();
            while (reader.Read())
            {
                var name = reader["column_name"]?.ToString() ?? string.Empty;
                var type = reader["column_type"]?.ToString() ?? string.Empty;
                var nullable = (reader["null"]?.ToString() ?? "YES").Equals("YES", StringComparison.OrdinalIgnoreCase);
                columns.Add(new ColumnSchema(name, type, nullable));
            }

            return (IReadOnlyList<ColumnSchema>)columns;
        });
    }

    private static string BuildCsvRead(CsvLoadOptions o)
    {
        if (o.Header is null && o.Delimiter is null && o.NullString is null
            && o.Quote is null && o.Columns is null && o.SampleSize is null)
        {
            return $"read_csv_auto({Lit(o.Path)})";
        }

        var opts = new List<string> { "auto_detect=true" };
        if (o.Header is not null) opts.Add($"header={(o.Header.Value ? "true" : "false")}");
        if (o.Delimiter is not null) opts.Add($"delim={Lit(o.Delimiter)}");
        if (o.NullString is not null) opts.Add($"nullstr={Lit(o.NullString)}");
        if (o.Quote is not null) opts.Add($"quote={Lit(o.Quote)}");
        if (o.SampleSize is not null) opts.Add($"sample_size={o.SampleSize.Value}");
        if (o.Columns is { Count: > 0 })
        {
            // types={'name': 'TYPE', ...} overrides inference per column while keeping
            // auto-detection for the rest. Names and type tokens are rendered as escaped string
            // literals (and the tokens are regex-validated by LoadCsvTool), so nothing can break
            // out into SQL.
            var entries = o.Columns.Select(kv => $"{Lit(kv.Key)}: {Lit(kv.Value)}");
            opts.Add($"types={{{string.Join(", ", entries)}}}");
        }

        return $"read_csv({Lit(o.Path)}, {string.Join(", ", opts)})";
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadAll(IDataReader reader)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                row[reader.GetName(i)] = value is DBNull ? null : Normalize(value);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Normalizes backend values to JSON-serializable, round-trippable forms. DuckDB HUGEINT comes
    /// back as <see cref="BigInteger"/> (not JSON-serializable), so map it to long/decimal when it
    /// fits, else to its decimal string.
    /// </summary>
    private static object? Normalize(object? value) => value switch
    {
        BigInteger bi when bi >= long.MinValue && bi <= long.MaxValue => (long)bi,
        BigInteger bi when bi >= new BigInteger(decimal.MinValue) && bi <= new BigInteger(decimal.MaxValue) => (decimal)bi,
        BigInteger bi => bi.ToString(),
        _ => value,
    };

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>Renders a value as an injection-safe DuckDB single-quoted string literal.</summary>
    private static string Lit(string value) => "'" + value.Replace("'", "''") + "'";
}
