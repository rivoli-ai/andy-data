using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_unnest</c> — explodes a LIST column into one row per element.
/// See docs/operations.md#dataframe_unnest.
/// </summary>
public sealed class UnnestOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public UnnestOperation() : this(null!, null!, null) { }

    public UnnestOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<UnnestOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_unnest",
        Name = "DataFrame Unnest",
        Description =
            "Explodes a LIST column so that each element becomes its own row. Other columns are " +
            "replicated for each element. Result is registered under 'into' (or replaces dataset_id). " +
            "Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "column", Type = "string", Required = true,
                Description = "LIST column to explode." },
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
        var column = GetParameter<string>(parameters, "column");

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, fromId);
            var columnQuoted = SqlText.ResolveColumnQuoted(column, entry.Schema);

            // Build SELECT list: all columns except the unnested one, plus UNNEST(...) AS column.
            var projections = entry.Schema
                .Where(c => !string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))
                .Select(c => SqlText.ResolveColumnQuoted(c.Name, entry.Schema))
                .ToList();
            projections.Add($"UNNEST({columnQuoted}) AS {SqlText.QuoteIdent(column)}");

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections),
                $"unnest:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
        });
    }
}
