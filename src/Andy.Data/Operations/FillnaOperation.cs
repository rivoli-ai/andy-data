using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_fillna</c> — replaces NULL values with a scalar or per-column replacements.
/// See docs/operations.md#dataframe_fillna.
/// </summary>
public sealed class FillnaOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public FillnaOperation() : this(null!, null!, null) { }

    public FillnaOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<FillnaOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_fillna",
        Name = "DataFrame Fill NA",
        Description =
            "Replaces NULL values in a dataset. In scalar mode, provide a global 'value' and/or a " +
            "per-column 'values' map (at least one required); 'value' is coerced to each column's type " +
            "and 'values' overrides it per column. In carry mode, set 'method' to 'ffill' (carry the " +
            "last non-null value forward) or 'bfill' (carry the next non-null value backward) along an " +
            "'order_by' ordering, optionally within 'partition_by' groups and limited to a 'columns' " +
            "subset — the pandas ffill/bfill equivalent. 'method' cannot be combined with 'value'/'values'. " +
            "Result is registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "value", Type = "string", Required = false,
                Description = "Global replacement value (scalar mode). Coerced to each column's type." },
            new DataFrameParam { Name = "values", Type = "object", Required = false,
                Description = "Column-to-replacement map (scalar mode) overriding 'value' for those columns." },
            new DataFrameParam { Name = "method", Type = "string", Required = false,
                AllowedValues = new object[] { "ffill", "bfill" },
                Description = "Carry mode: 'ffill' (forward-fill) or 'bfill' (backward-fill). Requires 'order_by'." },
            new DataFrameParam { Name = "order_by", Type = "array", Required = false,
                Description = "Ordering column(s) defining previous/next for 'method' (required with 'method')." },
            new DataFrameParam { Name = "partition_by", Type = "array", Required = false,
                Description = "Carry-mode grouping columns; the fill restarts within each group." },
            new DataFrameParam { Name = "columns", Type = "array", Required = false,
                Description = "Carry-mode subset of columns to fill (default: all columns)." },
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
            var globalValue = parameters.GetValueOrDefault("value");
            var valuesMap = ParseValuesMap(parameters.GetValueOrDefault("values"));
            var method = GetStringOrNull(parameters, "method")?.ToLowerInvariant();

            if (method is not null)
            {
                var entry0 = RequireDataset(_catalog, fromId);
                var sw0 = Stopwatch.StartNew();
                var projections0 = BuildCarryProjections(method, parameters, entry0.Schema);
                return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections0),
                    $"fillna:{fromId}", sw0, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
            }

            if (globalValue is null && valuesMap.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "At least one of 'value', 'values', or 'method' must be provided.");
            }

            var entry = RequireDataset(_catalog, fromId);
            ValidateValuesMapKeys(valuesMap, entry.Schema);
            var projections = new List<string>(entry.Schema.Count);

            foreach (var column in entry.Schema)
            {
                var quoted = SqlText.ResolveColumnQuoted(column.Name, entry.Schema);
                var rawReplacement = valuesMap.TryGetValue(column.Name, out var perColumn)
                    ? perColumn
                    : globalValue;

                var replacement = CoerceFillValue(rawReplacement, column);
                if (replacement is null)
                {
                    projections.Add(quoted);
                }
                else
                {
                    projections.Add($"COALESCE({quoted}, {SqlText.Literal(replacement)}) AS {quoted}");
                }
            }

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections),
                $"fillna:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
        });
    }

    /// <summary>
    /// Builds the carry-mode (ffill/bfill) projections: each filled column becomes
    /// <c>COALESCE(col, last_value/first_value(col IGNORE NULLS) OVER (PARTITION BY ... ORDER BY ... frame))</c>.
    /// </summary>
    private static List<string> BuildCarryProjections(
        string method, IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<ColumnSchema> schema)
    {
        if (parameters.GetValueOrDefault("value") is not null ||
            ParseValuesMap(parameters.GetValueOrDefault("values")).Count > 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "'method' (ffill/bfill) cannot be combined with 'value' or 'values'.");
        }

        var orderBy = ToStringList("order_by", parameters.GetValueOrDefault("order_by"));
        if (orderBy.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "'method' (ffill/bfill) requires 'order_by' to define previous/next.");
        }

        var orderQuoted = orderBy.Select(c => SqlText.ResolveColumnQuoted(c, schema)).ToList();
        var partitionBy = ToStringList("partition_by", parameters.GetValueOrDefault("partition_by"));
        var partitionQuoted = partitionBy.Select(c => SqlText.ResolveColumnQuoted(c, schema)).ToList();

        var requested = ToStringList("columns", parameters.GetValueOrDefault("columns"));
        var fillTargets = requested.Count == 0
            ? schema.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : requested.Select(c => SqlText.ResolveColumn(c, schema)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fn = method == "ffill" ? "last_value" : "first_value";
        var frame = method == "ffill"
            ? "ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW"
            : "ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING";
        var over =
            (partitionQuoted.Count > 0 ? $"PARTITION BY {string.Join(", ", partitionQuoted)} " : "") +
            $"ORDER BY {string.Join(", ", orderQuoted)} {frame}";

        var projections = new List<string>(schema.Count);
        foreach (var column in schema)
        {
            var quoted = SqlText.ResolveColumnQuoted(column.Name, schema);
            if (fillTargets.Contains(column.Name))
            {
                projections.Add($"COALESCE({quoted}, {fn}({quoted} IGNORE NULLS) OVER ({over})) AS {quoted}");
            }
            else
            {
                projections.Add(quoted);
            }
        }

        return projections;
    }

    private static Dictionary<string, object?> ParseValuesMap(object? value)
    {
        if (value is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        if (value is not IReadOnlyDictionary<string, object?> dict)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "'values' must be a { column: replacement } object.");
        }

        return new Dictionary<string, object?>(dict, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateValuesMapKeys(
        Dictionary<string, object?> valuesMap, IReadOnlyList<ColumnSchema> schema)
    {
        var schemaNames = new HashSet<string>(schema.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var key in valuesMap.Keys)
        {
            if (!schemaNames.Contains(key))
            {
                SqlText.ResolveColumn(key, schema); // throws COLUMN_NOT_FOUND with did_you_mean
            }
        }
    }

    /// <summary>
    /// Coerces a replacement value to the target column's type. String values are parsed for numeric
    /// and boolean columns so that, for example, filling an integer column with "0" keeps the column
    /// integer rather than widening it to VARCHAR.
    /// </summary>
    private static object? CoerceFillValue(object? value, ColumnSchema column)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not string s)
        {
            return value;
        }

        var type = column.Type.ToUpperInvariant();
        if (type.Contains("INT") || type == "BIGINT" || type == "SMALLINT" || type == "TINYINT" ||
            type == "UBIGINT" || type == "USMALLINT" || type == "UTINYINT")
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return l;
            }
        }
        else if (type == "DOUBLE" || type == "FLOAT" || type == "REAL")
        {
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }
        }
        else if (type.StartsWith("DECIMAL", StringComparison.Ordinal) ||
                 type.StartsWith("NUMERIC", StringComparison.Ordinal))
        {
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
            {
                return m;
            }
        }
        else if (type == "BOOLEAN" || type == "BOOL")
        {
            if (bool.TryParse(s, out var b))
            {
                return b;
            }
        }

        return s;
    }
}
