using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_list</c> — lists datasets registered in the current session.
/// See docs/operations.md#dataframe_list.
/// </summary>
public sealed class ListOperation : DataFrameOperationBase
{
    private static readonly IReadOnlyList<ColumnSchema> ListingSchema = new[]
    {
        new ColumnSchema("dataset_id", "VARCHAR", false),
        new ColumnSchema("row_count", "BIGINT", true),
        new ColumnSchema("column_count", "BIGINT", false),
        new ColumnSchema("source", "VARCHAR", false),
    };

    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public ListOperation() : this(null!, null) { }

    public ListOperation(IDatasetCatalog catalog, ILogger<ListOperation>? logger = null)
    {
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_list",
        Name = "DataFrame List",
        Description =
            "Lists the datasets registered in the current session. Returns the standard envelope " +
            "where preview_rows holds one row per dataset (dataset_id, row_count, column_count, source). " +
            "The envelope's top-level dataset_id is the literal \"session\" (this overview refers to " +
            "the whole session, not a single dataset).",
        Parameters = [],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        return Guard(parameters, () =>
        {
            var rows = _catalog.List()
                .OrderBy(e => e.DatasetId, StringComparer.Ordinal)
                .Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["dataset_id"] = e.DatasetId,
                    ["row_count"] = e.RowCount,
                    ["column_count"] = (long)e.Schema.Count,
                    ["source"] = e.Source,
                })
                .ToList();

            // "session" (not a real dataset id) keeps the envelope's dataset_id within the
            // documented id pattern; the description tells the model it refers to the session.
            return DataFrameResponse.Ok("session", ListingSchema, rows.Count, rows,
                new DataFrameStats(0, 0, rows.Count));
        });
    }
}
