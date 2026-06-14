using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_preview</c> — head / tail / random sample of rows from a registered dataset.
/// See docs/operations.md#dataframe_preview.
/// </summary>
public sealed class PreviewOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "head", "tail", "sample" };
    private const int MaxLimit = 1000;

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public PreviewOperation() : this(null!, null!, null) { }

    public PreviewOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<PreviewOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_preview",
        Name = "DataFrame Preview",
        Description =
            "Returns a bounded set of rows from a dataset referenced by dataset_id: the first rows " +
            "(head), last rows (tail), or a random sample. 'sample' requires a 'seed' for repeatability.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Id of a previously loaded dataset." },
            new DataFrameParam { Name = "mode", Type = "string", Required = false, DefaultValue = "head",
                AllowedValues = new object[] { "head", "tail", "sample" },
                Description = "head (default), tail, or sample." },
            new DataFrameParam { Name = "limit", Type = "integer", Required = false, DefaultValue = PreviewLimit,
                MinValue = 1, MaxValue = MaxLimit, Description = "Number of rows (1..1000, default 50)." },
            new DataFrameParam { Name = "seed", Type = "integer", Required = false,
                Description = "Required when mode=sample; makes sampling repeatable." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");
        var mode = GetStringOrNull(parameters, "mode") ?? "head";

        return Guard(parameters, options, _backend, ct =>
        {
            var limit = Math.Clamp(GetIntOrNull(parameters, "limit") ?? PreviewLimit, 1, MaxLimit);
            var seed = GetLongOrNull(parameters, "seed");

            if (!Modes.Contains(mode))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Unknown preview mode '{mode}'. Use head, tail, or sample.");
            }

            if (mode == "sample" && seed is null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "mode=sample requires a 'seed' for repeatable sampling.");
            }

            var entry = _catalog.Get(datasetId)
                ?? throw new DataFrameException(DataFrameErrorCodes.DatasetNotFound,
                    $"Dataset '{datasetId}' is not registered.",
                    new Dictionary<string, object?> { ["dataset_id"] = datasetId });

            var sw = Stopwatch.StartNew();
            // tail computes its OFFSET from the total, so a stale catalog count would return the
            // wrong window — always count fresh for tail; head/sample only need a display total.
            var rowCount = mode == "tail"
                ? _backend.CountRows(datasetId, ct)
                : entry.RowCount ?? _backend.CountRows(datasetId, ct);
            var rows = _backend.Preview(datasetId, mode, limit, seed, rowCount, ct);
            sw.Stop();

            return DataFrameResponse.Ok(datasetId, entry.Schema, rowCount, rows,
                new DataFrameStats(sw.ElapsedMilliseconds, 0, rows.Count));
        });
    }
}
