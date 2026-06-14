using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_load_delta</c> — loads a Delta Lake table via the DuckDB delta extension.
/// See docs/operations.md#dataframe_load_delta.
/// </summary>
public sealed class LoadDeltaOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;
    private readonly IPathPolicy? _pathPolicy;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public LoadDeltaOperation() : this(null!, null!, null, null) { }

    public LoadDeltaOperation(IDuckDbBackend backend, IDatasetCatalog catalog, IPathPolicy? pathPolicy = null, ILogger<LoadDeltaOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        _pathPolicy = pathPolicy;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_load_delta",
        Name = "DataFrame Load Delta",
        Description =
            "Loads a Delta Lake table into a named, session-scoped dataset. With no version/timestamp it " +
            "reads the latest snapshot via the DuckDB delta extension. Supplying 'version' (integer) or " +
            "'timestamp' (ISO-8601) performs time travel by replaying the transaction log directly. Time " +
            "travel supports unpartitioned tables without checkpoints, deletion vectors, or other reader " +
            "features; those return a clear error. Returns the standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "path", Type = "string", Required = true,
                Description = "Delta table root directory." },
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern,
                Description = "Id to register the loaded dataset under (letters, digits, underscores)." },
            new DataFrameParam { Name = "version", Type = "integer", Required = false,
                Description = "Time travel: load this snapshot version. Mutually exclusive with timestamp." },
            new DataFrameParam { Name = "timestamp", Type = "string", Required = false,
                Description = "Time travel: load the latest version at or before this ISO-8601 timestamp. " +
                    "Mutually exclusive with version." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var path = GetParameter<string>(parameters, "path");
        var datasetId = GetParameter<string>(parameters, "dataset_id");

        return Guard(parameters, options, _backend, ct =>
        {
            EnforceReadPolicy(_pathPolicy, path);

            var version = GetLongOrNull(parameters, "version");
            var timestampText = GetStringOrNull(parameters, "timestamp");

            if (version is not null && timestampText is not null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                    "Specify at most one of 'version' or 'timestamp' for Delta time travel, not both.");
            }

            DateTimeOffset? timestamp = null;
            if (timestampText is not null)
            {
                if (!DateTimeOffset.TryParse(timestampText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsed))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                        $"Invalid timestamp '{timestampText}'; use an ISO-8601 date-time (e.g. 2024-01-31T00:00:00Z).");
                }

                timestamp = parsed;
            }

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, $"Delta table not found: {path}",
                    new Dictionary<string, object?> { ["path"] = path });
            }

            var sw = Stopwatch.StartNew();
            // Latest snapshot uses the DuckDB delta extension when available; otherwise fall back to
            // replaying the transaction log via RegisterDeltaVersion (no extension required). Time
            // travel always uses the hand-rolled replay path because the extension exposes no
            // version/timestamp parameter.
            var schema = version is null && timestamp is null
                ? _backend.IsDeltaExtensionAvailable()
                    ? _backend.RegisterDelta(datasetId, path, ct)
                    : _backend.RegisterDeltaVersion(datasetId, path, null, null, ct)
                : _backend.RegisterDeltaVersion(datasetId, path, version, timestamp, ct);
            var bytesScanned = EstimateDeltaBytes(path);
            return Finish(_backend, _catalog, datasetId, schema, $"delta:{path}", sw, ct: ct, bytesScanned: bytesScanned);
        });
    }

    private static long EstimateDeltaBytes(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            return Directory.EnumerateFiles(path, "*.parquet", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }
}
