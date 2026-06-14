using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_schema</c> — returns the column schema of a registered dataset without scanning data.
/// See docs/operations.md#dataframe_schema.
/// </summary>
public sealed class SchemaOperation : DataFrameOperationBase
{
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public SchemaOperation() : this(null!, null) { }

    public SchemaOperation(IDatasetCatalog catalog, ILogger<SchemaOperation>? logger = null)
    {
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_schema",
        Name = "DataFrame Schema",
        Description =
            "Returns the schema (column names, types, nullability) of a dataset referenced by " +
            "dataset_id, without scanning data. Returns the standard dataframe envelope.",
        // Reads only the in-memory catalog (no file access), like dataframe_list/dataframe_drop.
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Id of a previously loaded dataset." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");

        return Guard(parameters, () =>
        {
            var entry = _catalog.Get(datasetId)
                ?? throw new DataFrameException(DataFrameErrorCodes.DatasetNotFound,
                    $"Dataset '{datasetId}' is not registered.",
                    new Dictionary<string, object?> { ["dataset_id"] = datasetId });

            return DataFrameResponse.Ok(
                datasetId, entry.Schema, entry.RowCount ?? 0,
                Array.Empty<IReadOnlyDictionary<string, object?>>(),
                new DataFrameStats(0, 0, 0));
        });
    }
}
