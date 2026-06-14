using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_sample</c> — materializes a deterministic reservoir sample of a dataset.
/// See docs/operations.md#dataframe_sample.
/// </summary>
public sealed class SampleOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public SampleOperation() : this(null!, null!, null) { }

    public SampleOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<SampleOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_sample",
        Name = "DataFrame Sample",
        Description =
            "Materializes a deterministic reservoir sample of a dataset. 'n' is the maximum number " +
            "of rows to keep; 'seed' is required and makes the sample repeatable. The result is " +
            "registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "n", Type = "integer", Required = true,
                Description = "Reservoir sample size (must be >= 1)." },
            new DataFrameParam { Name = "seed", Type = "integer", Required = true,
                Description = "Deterministic seed for repeatable sampling." },
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
            var entry = RequireDataset(_catalog, fromId);

            var n = GetIntOrNull(parameters, "n");
            if (n is null or < 1)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'n' must be a positive integer.");
            }

            var seedObj = parameters.GetValueOrDefault("seed");
            if (seedObj is null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'seed' is required for repeatable sampling.");
            }

            long seed;
            try
            {
                seed = Convert.ToInt64(seedObj, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'seed' must be an integer.");
            }

            var sw = Stopwatch.StartNew();
            var plan = GetBoolOrNull(parameters, "explain") ?? false
                ? _backend.ExplainSample(fromId, n.Value, seed, ct)
                : null;
            var schema = _backend.Sample(intoId, fromId, n.Value, seed, ct);
            return Finish(_backend, _catalog, intoId, schema, $"sample:{fromId}", sw, plan: plan, ct: ct);
        });
    }
}
