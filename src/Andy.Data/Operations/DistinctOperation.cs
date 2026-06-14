using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_distinct</c> — removes duplicate rows over all or selected columns.
/// See docs/operations.md#dataframe_distinct.
/// </summary>
public sealed class DistinctOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public DistinctOperation() : this(null!, null!, null) { }

    public DistinctOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<DistinctOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_distinct",
        Name = "DataFrame Distinct",
        Description =
            "Removes duplicate rows. With no 'columns', dedupes whole rows. With 'columns', keeps one " +
            "row per distinct combination; 'keep' (first|last) plus 'order_by' decide which. Result is " +
            "registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "columns", Type = "array", Required = false,
                Description = "Columns to dedupe on (default: all columns)." },
            new DataFrameParam { Name = "keep", Type = "string", Required = false, DefaultValue = "first",
                AllowedValues = new object[] { "first", "last" },
                Description = "first | last: which row within each duplicate group survives under the " +
                    "supplied 'order_by'. 'first' keeps the earliest row in that ordering; 'last' keeps " +
                    "the latest. Requires 'order_by' to be meaningful. (Internally 'keep=last' flips the " +
                    "scan direction of each order_by key so DISTINCT ON retains the last row — your " +
                    "stated directions still describe the ordering, not the scan order.)" },
            new DataFrameParam { Name = "order_by", Type = "array", Required = false,
                Description = "Array of { column, direction (asc|desc) } defining the ordering within each " +
                    "duplicate group. 'keep' then selects the first or last row in that ordering; e.g. " +
                    "direction asc with keep=last keeps the row with the largest value." },
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
        var keepLast = string.Equals(GetStringOrNull(parameters, "keep"), "last", StringComparison.OrdinalIgnoreCase);

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, fromId);
            var columns = ToStringList("columns", parameters.GetValueOrDefault("columns"))
                .Select(c => SqlText.ResolveColumnQuoted(c, entry.Schema))
                .ToList();
            var warnings = new List<string>();

            string selectClause;
            string? orderBy = null;

            if (columns.Count == 0)
            {
                if (keepLast)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'keep' is not supported when 'columns' is omitted (whole-row distinct).");
                }

                if (ToObjectList(parameters.GetValueOrDefault("order_by")).Count > 0)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'order_by' is not supported when 'columns' is omitted (whole-row distinct).");
                }

                selectClause = "DISTINCT *";
            }
            else
            {
                selectClause = $"DISTINCT ON ({string.Join(", ", columns)}) *";

                // DISTINCT ON keeps the first row per group per ORDER BY; it must start with the keys.
                var orderKeys = new List<string>(columns.Select(c => $"{c} ASC"));
                var userOrder = new List<string>();
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

                    var col = SqlText.ResolveColumnQuoted(name, entry.Schema);
                    var asc = !string.Equals(spec.TryGetValue("direction", out var d) ? d?.ToString() : null, "desc",
                        StringComparison.OrdinalIgnoreCase);
                    // keep=last flips the effective direction so DISTINCT ON keeps the "last" row.
                    var dir = (asc ^ keepLast) ? "ASC" : "DESC";
                    userOrder.Add($"{col} {dir}");
                }

                if (userOrder.Count == 0)
                {
                    warnings.Add("distinct on a column subset without 'order_by' is non-deterministic about which row is kept.");
                }

                orderKeys.AddRange(userOrder);
                orderBy = string.Join(", ", orderKeys);
            }

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, selectClause, $"distinct:{fromId}", sw,
                GetBoolOrNull(parameters, "explain") ?? false,
                orderByClause: orderBy, warnings: warnings.Count > 0 ? warnings : null, ct: ct);
        });
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
