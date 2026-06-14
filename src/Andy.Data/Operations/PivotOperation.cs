using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_pivot</c> — reshapes long data to wide.
/// See docs/operations.md#dataframe_pivot.
/// </summary>
public sealed class PivotOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> Aggregations = new(StringComparer.Ordinal)
        { "sum", "avg", "min", "max", "count" };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public PivotOperation() : this(null!, null!, null) { }

    public PivotOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<PivotOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_pivot",
        Name = "DataFrame Pivot",
        Description =
            "Reshapes long data to wide. 'index' columns remain rows; the distinct values of the " +
            "'columns' column become new columns. 'values' may be a single column name with " +
            "'aggregation' (sum|avg|min|max|count, default sum), or an array of " +
            "{ column, aggregation, alias? } to produce one column per (value, aggregation, pivot key). " +
            "Result is registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "index", Type = "array", Required = true,
                Description = "Columns that remain rows." },
            new DataFrameParam { Name = "columns", Type = "string", Required = true,
                Description = "Column whose distinct values become new columns." },
            new DataFrameParam { Name = "values", Type = "string", Required = true,
                Description = "Column name (scalar form) or array of { column, aggregation, alias? } objects." },
            new DataFrameParam { Name = "aggregation", Type = "string", Required = false, DefaultValue = "sum",
                AllowedValues = new object[] { "sum", "avg", "min", "max", "count" },
                Description = "sum | avg | min | max | count (default sum). Used only with scalar 'values'." },
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

        // The framework schema validator accepts only a single parameter type. To support both
        // scalar (string) and array forms for 'values' we validate against a local copy so the
        // caller's dictionary is never mutated.
        var localParameters = new Dictionary<string, object?>(parameters);
        var originalValues = localParameters.GetValueOrDefault("values");
        localParameters["values"] = "__placeholder__";

        return Guard(localParameters, options, _backend, ct =>
        {
            localParameters["values"] = originalValues;

            var entry = RequireDataset(_catalog, fromId);

            var index = ToStringList("index", localParameters.GetValueOrDefault("index"))
                .Select(c => SqlText.ResolveColumnQuoted(c, entry.Schema)).ToList();
            if (index.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "'index' must list at least one column.");
            }

            var columnsCol = GetParameter<string>(localParameters, "columns");
            var columnQuoted = SqlText.ResolveColumnQuoted(columnsCol, entry.Schema);

            var aggExpr = BuildAggregateExpression(localParameters, entry.Schema);

            var sw = Stopwatch.StartNew();
            var plan = GetBoolOrNull(localParameters, "explain") ?? false
                ? _backend.ExplainPivot(fromId, index, columnQuoted, aggExpr, ct)
                : null;
            var schema = _backend.Pivot(intoId, fromId, index, columnQuoted, aggExpr, ct);
            return Finish(_backend, _catalog, intoId, schema, $"pivot:{fromId}", sw, plan: plan, ct: ct);
        });
    }

    private static string BuildAggregateExpression(
        IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<ColumnSchema> schema)
    {
        var values = parameters.GetValueOrDefault("values");

        // Scalar form: values is a string and aggregation is used.
        if (values is string valuesCol)
        {
            var aggregation = (GetStringOrNull(parameters, "aggregation") ?? "sum").ToLowerInvariant();
            if (!Aggregations.Contains(aggregation))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                    $"Unknown pivot aggregation '{aggregation}'. Use sum, avg, min, max, or count.");
            }

            var valueQuoted = SqlText.ResolveColumnQuoted(valuesCol, schema);
            return $"{aggregation}({valueQuoted})";
        }

        // Array form: values is an array of { column, aggregation, alias? }.
        if (values is not null and not string and System.Collections.IEnumerable enumerable)
        {
            if (GetStringOrNull(parameters, "aggregation") is not null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'aggregation' cannot be used when 'values' is an array. Set aggregation inside each values entry.");
            }

            var specs = enumerable.Cast<object?>().Where(o => o is not null).ToList();
            if (specs.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'values' array must contain at least one { column, aggregation, alias? } entry.");
            }

            var rendered = new List<string>();
            foreach (var item in specs)
            {
                if (item is not IReadOnlyDictionary<string, object?> spec)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Each 'values' entry must be a { column, aggregation, alias? } object.");
                }

                var column = spec.TryGetValue("column", out var c) ? c?.ToString() : null;
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Each 'values' entry requires a 'column'.");
                }

                var aggregation = (spec.TryGetValue("aggregation", out var a) ? a?.ToString() : null)?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(aggregation) || !Aggregations.Contains(aggregation))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        $"Unknown pivot aggregation '{aggregation}'. Use sum, avg, min, max, or count.");
                }

                var alias = spec.TryGetValue("alias", out var al) ? al?.ToString() : null;
                var valueQuoted = SqlText.ResolveColumnQuoted(column!, schema);
                var expr = $"{aggregation}({valueQuoted})";
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    expr += $" AS {SqlText.QuoteIdent(alias!)}";
                }

                rendered.Add(expr);
            }

            return string.Join(", ", rendered);
        }

        throw new DataFrameException(DataFrameErrorCodes.InvalidType,
            "'values' must be either a column name string or an array of { column, aggregation, alias? } objects.");
    }
}
