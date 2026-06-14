using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_load_parquet</c> — loads a Parquet file, glob, or Hive-partitioned directory.
/// See docs/operations.md#dataframe_load_parquet.
/// </summary>
public sealed class LoadParquetOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;
    private readonly IPathPolicy? _pathPolicy;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public LoadParquetOperation() : this(null!, null!, null, null) { }

    public LoadParquetOperation(IDuckDbBackend backend, IDatasetCatalog catalog, IPathPolicy? pathPolicy = null, ILogger<LoadParquetOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        _pathPolicy = pathPolicy;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_load_parquet",
        Name = "DataFrame Load Parquet",
        Description =
            "Loads a Parquet file, a glob (e.g. data/*.parquet), or a Hive-partitioned directory " +
            "glob into a named, session-scoped dataset referenced by dataset_id. Schema and types " +
            "come from the file metadata. Returns the standard dataframe envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "path", Type = "string", Required = true,
                Description = "Parquet file, glob, or partitioned-directory glob (e.g. events/**/*.parquet)." },
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern,
                Description = "Id to register the loaded dataset under (letters, digits, underscores)." },
            new DataFrameParam { Name = "hive_partitioning", Type = "boolean", Required = false,
                Description = "Expose key=value/ directories as partition columns " +
                    "(default: DuckDB auto-detects from the path)." },
            new DataFrameParam { Name = "union_by_name", Type = "boolean", Required = false,
                Description = "Align columns by name across files with differing schemas (default false)." },
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
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, "A 'path' is required.");
            }

            EnforceReadPolicy(_pathPolicy, path);

            if (!path.Contains('*') && !File.Exists(path) && !Directory.Exists(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, $"Path not found: {path}",
                    new Dictionary<string, object?> { ["path"] = path });
            }

            var sw = Stopwatch.StartNew();
            var bytesScanned = EstimateFileBytes(path);
            var schema = _backend.RegisterParquet(datasetId, path,
                GetBoolOrNull(parameters, "hive_partitioning"),
                GetBoolOrNull(parameters, "union_by_name"), ct);
            return Finish(_backend, _catalog, datasetId, schema, $"parquet:{path}", sw, ct: ct, bytesScanned: bytesScanned);
        });
    }
}
