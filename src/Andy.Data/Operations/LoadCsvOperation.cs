using System.Diagnostics;
using System.Text.RegularExpressions;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_load_csv</c> — loads a CSV file (or glob) into a named, session-scoped dataset.
/// See docs/operations.md#dataframe_load_csv.
/// </summary>
public sealed partial class LoadCsvOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;
    private readonly IPathPolicy? _pathPolicy;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public LoadCsvOperation() : this(null!, null!, null, null) { }

    public LoadCsvOperation(IDuckDbBackend backend, IDatasetCatalog catalog, IPathPolicy? pathPolicy = null, ILogger<LoadCsvOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        _pathPolicy = pathPolicy;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_load_csv",
        Name = "DataFrame Load CSV",
        Description =
            "Loads a CSV file (or glob such as data/*.csv) into a named, session-scoped dataset " +
            "referenced by dataset_id. Column types are inferred (sampling 'sample_size' rows) " +
            "unless overridden per column via 'columns' { name: type } hints. Does not require SQL " +
            "or code. Returns the standard dataframe envelope with schema, row_count, and a bounded " +
            "preview.",
        Parameters =
        [
            new DataFrameParam { Name = "path", Type = "string", Required = true,
                Description = "Path to the CSV file, or a glob such as data/*.csv." },
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern,
                Description = "Id to register the loaded dataset under (letters, digits, underscores)." },
            new DataFrameParam { Name = "header", Type = "boolean", Required = false,
                Description = "Whether the first row contains column names (default: auto-detect)." },
            new DataFrameParam { Name = "delimiter", Type = "string", Required = false,
                Description = "Field delimiter (default: auto-detect)." },
            new DataFrameParam { Name = "quote", Type = "string", Required = false,
                Description = "Quote character — exactly one character (default: auto-detect)." },
            new DataFrameParam { Name = "null_string", Type = "string", Required = false,
                Description = "Token to interpret as NULL (e.g. \"NA\")." },
            new DataFrameParam { Name = "columns", Type = "object", Required = false,
                Description = "Column-to-type map (e.g. { \"amount\": \"DECIMAL(12,2)\" }); overrides " +
                    "inference for those columns. Types are DuckDB type names." },
            new DataFrameParam { Name = "sample_size", Type = "integer", Required = false, DefaultValue = 20480,
                Description = "Rows sampled for type inference (default 20480; -1 reads the whole file)." },
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

            // Friendly error for a concrete (non-glob) path that doesn't exist.
            if (!path.Contains('*') && !File.Exists(path))
            {
                throw new DataFrameException(DataFrameErrorCodes.FileNotFound, $"File not found: {path}",
                    new Dictionary<string, object?> { ["path"] = path });
            }

            var quote = GetStringOrNull(parameters, "quote");
            if (quote is not null && quote.Length != 1)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"'quote' must be exactly one character; got '{quote}'.");
            }

            var sampleSize = GetLongOrNull(parameters, "sample_size");
            if (sampleSize is not null && sampleSize < 1 && sampleSize != -1)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"'sample_size' must be a positive row count, or -1 to read the whole file; got {sampleSize}.");
            }

            var sw = Stopwatch.StartNew();
            var options = new CsvLoadOptions(
                path,
                Header: GetBoolOrNull(parameters, "header"),
                Delimiter: GetStringOrNull(parameters, "delimiter"),
                NullString: GetStringOrNull(parameters, "null_string"),
                Quote: quote,
                Columns: ParseColumnHints(parameters.GetValueOrDefault("columns")),
                SampleSize: sampleSize);

            var bytesScanned = EstimateFileBytes(path);

            return _backend.RunExclusive(() =>
            {
                var schema = _backend.RegisterCsv(datasetId, options, ct);
                var rowCount = _backend.CountRows(datasetId, ct);
                _catalog.Register(new DatasetEntry(datasetId, schema, $"csv:{path}", rowCount));
                var preview = _backend.Preview(datasetId, "head", PreviewLimit, null, rowCount, ct);
                sw.Stop();

                return DataFrameResponse.Ok(datasetId, schema, rowCount, preview,
                    new DataFrameStats(sw.ElapsedMilliseconds, bytesScanned, rowCount));
            });
        });
    }

    /// <summary>
    /// Parses the 'columns' parameter — a { name: type } map — into a name → type dictionary,
    /// validating each type token against a conservative allow-list so only plain DuckDB type
    /// names reach the read_csv call. (The framework validator already rejects non-object shapes
    /// against the declared parameter type.)
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ParseColumnHints(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is not IReadOnlyDictionary<string, object?> map)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "'columns' must be a { name: type } map, e.g. { \"amount\": \"DECIMAL(12,2)\" }.");
        }

        var hints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, type) in map)
        {
            AddHint(hints, name, type?.ToString());
        }

        return hints.Count > 0 ? hints : null;
    }

    private static void AddHint(Dictionary<string, string> hints, string? name, string? type)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "Each 'columns' hint requires a column name and a type.");
        }

        if (!TypeTokenRegex().IsMatch(type))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                $"Invalid type '{type}' for column '{name}'. Use a plain DuckDB type name such as " +
                "VARCHAR, BIGINT, DOUBLE, DECIMAL(12,2), DATE, or TIMESTAMP.",
                new Dictionary<string, object?> { ["column"] = name });
        }

        hints[name] = type;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_(), ]+$")]
    private static partial Regex TypeTokenRegex();
}
