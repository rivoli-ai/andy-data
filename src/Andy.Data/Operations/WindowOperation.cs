using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_window</c> — applies window functions without collapsing rows.
/// See docs/operations.md#dataframe_window.
/// </summary>
public sealed class WindowOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> RankFns = new(StringComparer.Ordinal)
        { "row_number", "rank", "dense_rank", "percent_rank" };
    private static readonly HashSet<string> NtileFns = new(StringComparer.Ordinal) { "ntile" };
    private static readonly HashSet<string> OffsetFns = new(StringComparer.Ordinal) { "lag", "lead" };
    private static readonly HashSet<string> ValueFns = new(StringComparer.Ordinal) { "first_value", "last_value" };
    private static readonly HashSet<string> AggFns = new(StringComparer.Ordinal) { "sum", "avg", "min", "max", "count" };
    private static readonly Dictionary<string, string> FrameTokens = new(StringComparer.Ordinal)
    {
        ["unbounded_preceding"] = "UNBOUNDED PRECEDING",
        ["current_row"] = "CURRENT ROW",
        ["unbounded_following"] = "UNBOUNDED FOLLOWING",
    };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public WindowOperation() : this(null!, null!, null) { }

    public WindowOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<WindowOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_window",
        Name = "DataFrame Window",
        Description =
            "Adds window-function columns without collapsing rows. 'functions' is an array of " +
            "{ function, column?, alias, args? } with function in row_number, rank, dense_rank, percent_rank, " +
            "ntile, lag, lead, first_value, last_value, sum, avg, min, max, count. 'partition_by' and " +
            "'order_by' ({ column, direction, nulls }) define the window, where nulls is first or last; " +
            "optional 'frame' { start, end } with unbounded_preceding|current_row|unbounded_following or " +
            "{ preceding: N }|{ following: N }. Result is registered under 'into' (or replaces dataset_id).",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "functions", Type = "array", Required = true,
                Description = "Array of { function, column?, alias, args? }." },
            new DataFrameParam { Name = "partition_by", Type = "array", Required = false,
                Description = "Partition column names." },
            new DataFrameParam { Name = "order_by", Type = "array", Required = false,
                Description = "Array of { column, direction (asc|desc), nulls (first|last) }." },
            new DataFrameParam { Name = "frame", Type = "object", Required = false,
                Description = "{ start, end } window frame bounds; each may be a string token or { preceding: N } / { following: N }." },
            new DataFrameParam { Name = "explain", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Include the DuckDB query plan in stats.plan." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var fromId = GetParameter<string>(parameters, "dataset_id");
        var intoId = ResolveInto(parameters, fromId);

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, fromId);
            var over = BuildOverClause(parameters, entry.Schema);

            var funcs = ToObjectList(parameters.GetValueOrDefault("functions"));
            if (funcs.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "'functions' must be a non-empty array.");
            }

            var rendered = new List<string>();
            foreach (var item in funcs)
            {
                if (item is not IReadOnlyDictionary<string, object?> spec)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Each 'functions' entry must be a { function, column?, alias, args? } object.");
                }

                var fn = (spec.TryGetValue("function", out var f) ? f?.ToString() : null)?.ToLowerInvariant();
                var alias = spec.TryGetValue("alias", out var a) ? a?.ToString() : null;
                if (string.IsNullOrWhiteSpace(alias))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "Each window function requires an 'alias'.");
                }

                var head = RenderFunction(fn, spec, entry.Schema);
                rendered.Add($"{head} {over} AS {SqlText.QuoteIdent(alias!)}");
            }

            var selectClause = "*, " + string.Join(", ", rendered);

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, selectClause,
                $"window:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
        });
    }

    private static string RenderFunction(string? fn, IReadOnlyDictionary<string, object?> spec, IReadOnlyList<ColumnSchema> schema)
    {
        if (fn is null)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "Each window function requires a 'function'.");
        }

        if (RankFns.Contains(fn))
        {
            return $"{fn}()";
        }

        if (NtileFns.Contains(fn))
        {
            var args = ToObjectList(spec.GetValueOrDefault("args"));
            if (args.Count == 0 || !int.TryParse(args[0]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var buckets) || buckets < 1)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "Window function 'ntile' requires a positive integer in 'args' (e.g. args: [4]).");
            }

            return $"ntile({buckets})";
        }

        var column = spec.TryGetValue("column", out var c) ? c?.ToString() : null;
        if (string.IsNullOrWhiteSpace(column))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, $"Window function '{fn}' requires a 'column'.");
        }

        var col = SqlText.ResolveColumnQuoted(column, schema);

        if (OffsetFns.Contains(fn))
        {
            var args = ToObjectList(spec.GetValueOrDefault("args"));
            var offset = 1;
            if (args.Count > 0 && !int.TryParse(args[0]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Window function '{fn}' requires an integer offset in 'args' (e.g. args: [1]).");
            }

            return $"{fn}({col}, {offset})";
        }

        if (ValueFns.Contains(fn))
        {
            return $"{fn}({col})";
        }

        if (AggFns.Contains(fn))
        {
            return $"{fn}({col})";
        }

        throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, $"Unknown window function '{fn}'.");
    }

    private static string BuildOverClause(IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<ColumnSchema> schema)
    {
        var parts = new List<string>();

        var partition = ToStringList("partition_by", parameters.GetValueOrDefault("partition_by"))
            .Select(c => SqlText.ResolveColumnQuoted(c, schema)).ToList();
        if (partition.Count > 0)
        {
            parts.Add("PARTITION BY " + string.Join(", ", partition));
        }

        var orderKeys = new List<string>();
        foreach (var item in ToObjectList(parameters.GetValueOrDefault("order_by")))
        {
            if (item is not IReadOnlyDictionary<string, object?> spec)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "Each 'order_by' entry must be a { column, direction } object.");
            }

            var name = spec.TryGetValue("column", out var c) ? c?.ToString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "Each 'order_by' entry requires a 'column'.");
            }

            var col = SqlText.ResolveColumnQuoted(name, schema);
            var dir = string.Equals(spec.TryGetValue("direction", out var d) ? d?.ToString() : null, "desc",
                StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
            var nulls = spec.TryGetValue("nulls", out var n) ? n?.ToString() : null;
            var nullsSql = string.Equals(nulls, "first", StringComparison.OrdinalIgnoreCase) ? "NULLS FIRST"
                : string.Equals(nulls, "last", StringComparison.OrdinalIgnoreCase) ? "NULLS LAST"
                : null;
            if (nullsSql is null && !string.IsNullOrWhiteSpace(nulls))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "Window 'order_by' nulls must be 'first' or 'last'.");
            }

            orderKeys.Add(nullsSql is null ? $"{col} {dir}" : $"{col} {dir} {nullsSql}");
        }

        if (orderKeys.Count > 0)
        {
            parts.Add("ORDER BY " + string.Join(", ", orderKeys));
        }

        if (parameters.GetValueOrDefault("frame") is IReadOnlyDictionary<string, object?> frame)
        {
            var start = ResolveFrameBound(frame.TryGetValue("start", out var s) ? s : null, isStart: true);
            var end = ResolveFrameBound(frame.TryGetValue("end", out var e) ? e : null, isStart: false);
            parts.Add($"ROWS BETWEEN {start} AND {end}");
        }

        return "OVER (" + string.Join(" ", parts) + ")";
    }

    private static string ResolveFrameBound(object? value, bool isStart)
    {
        if (value is null)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "Frame 'start' and 'end' must be specified.");
        }

        // String token form.
        if (value is string token)
        {
            if (FrameTokens.TryGetValue(token.ToLowerInvariant(), out var sql))
            {
                return sql;
            }

            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "Frame bounds must be unbounded_preceding, current_row, unbounded_following, or { preceding: N } / { following: N }.");
        }

        // Numeric offset object form: { preceding: N } or { following: N }.
        if (value is IReadOnlyDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("preceding", out var pv) && pv is not null)
            {
                var n = ParseFrameOffset("preceding", pv);
                if (n < 0)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Frame 'preceding' value must be a non-negative integer.");
                }

                return n == 0 ? "CURRENT ROW" : $"{n} PRECEDING";
            }

            if (dict.TryGetValue("following", out var fv) && fv is not null)
            {
                var n = ParseFrameOffset("following", fv);
                if (n < 0)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Frame 'following' value must be a non-negative integer.");
                }

                return n == 0 ? "CURRENT ROW" : $"{n} FOLLOWING";
            }
        }

        throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
            "Frame bounds must be unbounded_preceding, current_row, unbounded_following, or { preceding: N } / { following: N }.");
    }

    /// <summary>
    /// Converts a frame offset (the <c>N</c> in <c>{ preceding: N }</c> / <c>{ following: N }</c>) to
    /// an int, mapping a non-numeric or out-of-range value to the documented <c>INVALID_TYPE</c>
    /// envelope rather than letting a raw <see cref="FormatException"/>/<see cref="OverflowException"/>
    /// escape to the catch-all <c>BACKEND_ERROR</c> handler.
    /// </summary>
    private static int ParseFrameOffset(string side, object value)
    {
        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Frame '{side}' must be an integer; got '{value}'.",
                new Dictionary<string, object?> { ["parameter"] = side });
        }
    }

    private static List<object?> ToObjectList(object? value)
    {
        if (value is null || value is string || value is not System.Collections.IEnumerable e)
        {
            return new List<object?>();
        }

        return e.Cast<object?>().ToList();
    }

}
