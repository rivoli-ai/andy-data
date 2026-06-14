using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_value_counts</c> — counts occurrences of each distinct value in a column, ordered by
/// frequency. The categorical-EDA equivalent of pandas <c>Series.value_counts()</c>.
/// See docs/operations.md#dataframe_value_counts.
/// </summary>
public sealed class ValueCountsOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public ValueCountsOperation() : this(null!, null!, null) { }

    public ValueCountsOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<ValueCountsOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_value_counts",
        Name = "DataFrame Value Counts",
        Description =
            "Counts how often each distinct value of 'column' occurs, returning a dataset of " +
            "{ <column>, count, proportion } ordered by count descending (ties broken by the value " +
            "ascending, for a deterministic order). 'proportion' is the fraction of counted rows. " +
            "NULLs are excluded unless 'dropna' = false. Optional 'limit' keeps the top-N values. " +
            "Result is registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "column", Type = "string", Required = true,
                Description = "Column whose value frequencies to count." },
            new DataFrameParam { Name = "limit", Type = "integer", Required = false, MinValue = 1,
                Description = "Keep only the top-N most frequent values." },
            new DataFrameParam { Name = "dropna", Type = "boolean", Required = false, DefaultValue = true,
                Description = "Exclude NULL values (default true), matching pandas value_counts." },
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

            var column = GetParameter<string>(parameters, "column");
            var canonical = SqlText.ResolveColumn(column, entry.Schema); // validates existence
            var colQ = SqlText.QuoteIdent(canonical);

            var dropna = GetBoolOrNull(parameters, "dropna") ?? true;
            var limit = GetIntOrNull(parameters, "limit");

            // count(*) and its share of the counted rows (a window total over the grouped result).
            var selectClause =
                $"{colQ}, count(*) AS {SqlText.QuoteIdent("count")}, " +
                $"(count(*) * 1.0 / sum(count(*)) OVER ()) AS {SqlText.QuoteIdent("proportion")}";

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, selectClause, $"value_counts:{fromId}", sw,
                explain: false,
                whereClause: dropna ? $"{colQ} IS NOT NULL" : null,
                groupByClause: colQ,
                orderByClause: $"count(*) DESC, {colQ} ASC",
                limit: limit,
                ct: ct);
        });
    }
}
