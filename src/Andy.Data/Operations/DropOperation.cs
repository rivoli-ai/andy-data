using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_drop</c> — releases a dataset and frees its backend resources.
/// See docs/operations.md#dataframe_drop.
/// </summary>
public sealed class DropOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public DropOperation() : this(null!, null!, null) { }

    public DropOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<DropOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_drop",
        Name = "DataFrame Drop",
        Description =
            "Releases a dataset referenced by dataset_id; its backend resources are freed once no " +
            "remaining dataset depends on them. Returns the standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Id of the dataset to release." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");

        return Guard(parameters, () => _backend.RunExclusive(() =>
        {
            // Exclusive with Materialize/Finish so a drop can never interleave between a
            // transform's backend registration and its catalog registration.
            if (!_catalog.Contains(datasetId))
            {
                throw new DataFrameException(DataFrameErrorCodes.DatasetNotFound,
                    $"Dataset '{datasetId}' is not registered.",
                    new Dictionary<string, object?> { ["dataset_id"] = datasetId });
            }

            _catalog.Drop(datasetId);
            _backend.Drop(datasetId);

            return DataFrameResponse.Ok(datasetId,
                Array.Empty<ColumnSchema>(), 0,
                Array.Empty<IReadOnlyDictionary<string, object?>>(),
                new DataFrameStats(0, 0, 0),
                warnings: new[] { $"Dataset '{datasetId}' dropped." });
        }));
    }
}
