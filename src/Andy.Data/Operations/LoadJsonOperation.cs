using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_load_json</c> — loads a JSON file (or glob): NDJSON, a top-level array of objects,
/// or auto-detected. See docs/operations.md#dataframe_load_json.
/// </summary>
public sealed class LoadJsonOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal)
        { "auto", "newline_delimited", "array" };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;
    private readonly IPathPolicy? _pathPolicy;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public LoadJsonOperation() : this(null!, null!, null, null) { }

    public LoadJsonOperation(IDuckDbBackend backend, IDatasetCatalog catalog, IPathPolicy? pathPolicy = null, ILogger<LoadJsonOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        _pathPolicy = pathPolicy;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_load_json",
        Name = "DataFrame Load JSON",
        Description =
            "Loads a JSON file (or glob such as data/*.ndjson) into a named, session-scoped dataset " +
            "referenced by dataset_id. Reads newline-delimited JSON (NDJSON) or a top-level array of " +
            "objects; 'format' is auto (default, detects the layout), newline_delimited, or array. " +
            "Schema and column types are inferred from the JSON values. Returns the standard " +
            "dataframe envelope with schema, row_count, and a bounded preview.",
        Parameters =
        [
            new DataFrameParam { Name = "path", Type = "string", Required = true,
                Description = "Path to the JSON file, or a glob such as data/*.ndjson." },
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern,
                Description = "Id to register the loaded dataset under (letters, digits, underscores)." },
            new DataFrameParam { Name = "format", Type = "string", Required = false, DefaultValue = "auto",
                AllowedValues = new object[] { "auto", "newline_delimited", "array" },
                Description = "auto (default, detect the layout), newline_delimited (NDJSON), or array." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var path = GetParameter<string>(parameters, "path");
        var datasetId = GetParameter<string>(parameters, "dataset_id");
        var format = (GetStringOrNull(parameters, "format") ?? "auto").ToLowerInvariant();

        return Guard(parameters, options, _backend, ct =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, "A 'path' is required.");
            }

            EnforceReadPolicy(_pathPolicy, path);

            // Friendly error for a concrete (non-glob) path that doesn't exist.
            if (!path.Contains('*') && !File.Exists(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, $"File not found: {path}",
                    new Dictionary<string, object?> { ["path"] = path });
            }

            if (!Formats.Contains(format))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Unknown JSON format '{format}'. Use auto, newline_delimited, or array.");
            }

            var sw = Stopwatch.StartNew();
            var bytesScanned = EstimateFileBytes(path);
            var schema = _backend.RegisterJson(datasetId, path, format, ct);
            return Finish(_backend, _catalog, datasetId, schema, $"json:{path}", sw, ct: ct, bytesScanned: bytesScanned);
        });
    }
}
