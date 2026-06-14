using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_dropna</c> — removes rows containing NULL values.
/// See docs/operations.md#dataframe_dropna.
/// </summary>
public sealed class DropnaOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public DropnaOperation() : this(null!, null!, null) { }

    public DropnaOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<DropnaOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_dropna",
        Name = "DataFrame Drop NA",
        Description =
            "Removes rows with NULL values from a dataset. 'columns' restricts the check to a subset " +
            "of columns (default: all columns). 'how' decides whether to drop when any ('any', default) " +
            "or all ('all') of the checked columns are NULL. Result is registered under 'into' (or replaces " +
            "dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "columns", Type = "array", Required = false,
                Description = "Column names to check for NULLs (default: all columns)." },
            new DataFrameParam { Name = "how", Type = "string", Required = false, DefaultValue = "any",
                AllowedValues = new object[] { "any", "all" },
                Description = "Drop rows where any ('any') or all ('all') of the checked columns are NULL." },
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
            var requestedColumns = ToStringList("columns", parameters.GetValueOrDefault("columns"));
            var how = (GetStringOrNull(parameters, "how") ?? "any").ToLowerInvariant();

            var entry = RequireDataset(_catalog, fromId);
            var targetColumns = requestedColumns.Count > 0
                ? requestedColumns
                : entry.Schema.Select(c => c.Name).ToList();

            var quoted = targetColumns
                .Select(name => SqlText.ResolveColumnQuoted(name, entry.Schema))
                .ToList();

            string? whereClause = null;
            if (quoted.Count > 0)
            {
                whereClause = how == "all"
                    ? string.Join(" OR ", quoted.Select(q => $"{q} IS NOT NULL"))
                    : string.Join(" AND ", quoted.Select(q => $"{q} IS NOT NULL"));
            }

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, "*",
                $"dropna:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false,
                whereClause: whereClause, ct: ct);
        });
    }
}
