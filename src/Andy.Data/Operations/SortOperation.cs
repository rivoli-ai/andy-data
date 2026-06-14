using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_sort</c> — orders rows by one or more keys, with optional top-N limit.
/// See docs/operations.md#dataframe_sort.
/// </summary>
public sealed class SortOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public SortOperation() : this(null!, null!, null) { }

    public SortOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<SortOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_sort",
        Name = "DataFrame Sort",
        Description =
            "Orders rows of a dataset by one or more keys. 'by' is an array of { column, direction " +
            "(asc|desc), nulls (first|last) } entries; ties break by the order of keys. Optional " +
            "'limit' keeps the first N rows (top-N). Result is registered under 'into' (or replaces " +
            "dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "by", Type = "array", Required = true,
                Description = "Array of { column, direction (asc|desc), nulls (first|last) }." },
            new DataFrameParam { Name = "limit", Type = "integer", Required = false, MinValue = 1,
                Description = "Keep only the first N rows after sorting (top-N)." },
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
            var limit = GetIntOrNull(parameters, "limit");
            var entry = RequireDataset(_catalog, fromId);
            if (parameters.GetValueOrDefault("by") is not System.Collections.IEnumerable items ||
                parameters["by"] is string)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "'by' must be a non-empty array.");
            }

            var keys = new List<string>();
            foreach (var item in items)
            {
                if (item is not IReadOnlyDictionary<string, object?> spec)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Each 'by' entry must be a { column, direction, nulls } object.");
                }

                var name = spec.TryGetValue("column", out var c) ? c?.ToString() : null;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "Each 'by' entry requires a 'column'.");
                }

                var col = SqlText.ResolveColumnQuoted(name, entry.Schema);
                var dir = string.Equals(spec.TryGetValue("direction", out var d) ? d?.ToString() : null, "desc",
                    StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC";
                var nulls = string.Equals(spec.TryGetValue("nulls", out var n) ? n?.ToString() : null, "first",
                    StringComparison.OrdinalIgnoreCase) ? "NULLS FIRST" : "NULLS LAST";
                keys.Add($"{col} {dir} {nulls}");
            }

            if (keys.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "'by' must specify at least one key.");
            }

            // Append a hidden stable tie-breaker so duplicate keys produce a deterministic order.
            keys.Add("row_number() OVER () ASC");

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, "*", $"sort:{fromId}", sw,
                GetBoolOrNull(parameters, "explain") ?? false,
                orderByClause: string.Join(", ", keys), limit: limit, ct: ct);
        });
    }
}
