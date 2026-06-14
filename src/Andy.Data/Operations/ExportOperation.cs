using System.Diagnostics;
using System.Text.RegularExpressions;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_export</c> — writes a dataset to CSV, Parquet, JSON, or Delta.
/// See docs/operations.md#dataframe_export.
/// </summary>
public sealed partial class ExportOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal) { "csv", "parquet", "json" };
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "error", "append", "overwrite" };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;
    private readonly IPathPolicy? _pathPolicy;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public ExportOperation() : this(null!, null!, null, null) { }

    public ExportOperation(IDuckDbBackend backend, IDatasetCatalog catalog, IPathPolicy? pathPolicy = null, ILogger<ExportOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        _pathPolicy = pathPolicy;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_export",
        Name = "DataFrame Export",
        Description =
            "Writes a dataset referenced by dataset_id to disk. 'format' is csv, parquet, json, or delta. " +
            "'mode' is error (fail if target exists), append (Delta only; add a new commit), or overwrite " +
            "(replace the target atomically). Optional 'partition_by' (Parquet and Delta), 'compression' " +
            "(e.g. snappy, zstd for Parquet/JSON), CSV-only 'header', 'delimiter', 'quote', and 'escape', " +
            "and JSON-only 'array' (default false) to write a top-level JSON array instead of NDJSON. " +
            "Requires filesystem write permission. Returns the standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Dataset to export." },
            new DataFrameParam { Name = "path", Type = "string", Required = true,
                Description = "Output file or directory." },
            new DataFrameParam { Name = "format", Type = "string", Required = true,
                AllowedValues = new object[] { "csv", "parquet", "json", "delta" },
                Description = "Output format: csv, parquet, json, or delta." },
            new DataFrameParam { Name = "partition_by", Type = "array", Required = false,
                Description = "Partition columns (Parquet and Delta)." },
            new DataFrameParam { Name = "compression", Type = "string", Required = false,
                Description = "Compression codec (e.g. snappy, zstd, gzip for Parquet/JSON)." },
            new DataFrameParam { Name = "array", Type = "boolean", Required = false, DefaultValue = false,
                Description = "JSON only: write a top-level JSON array (true) or newline-delimited JSON (false, default)." },
            new DataFrameParam { Name = "mode", Type = "string", Required = false, DefaultValue = "error",
                AllowedValues = new object[] { "error", "append", "overwrite" },
                Description = "error | append (Delta only) | overwrite" },
            new DataFrameParam { Name = "header", Type = "boolean", Required = false,
                Description = "Write a header row (CSV only; default true)." },
            new DataFrameParam { Name = "delimiter", Type = "string", Required = false,
                Description = "Field delimiter (CSV only; default ',')." },
            new DataFrameParam { Name = "quote", Type = "string", Required = false,
                Description = "Quote character (CSV only; default '\"')." },
            new DataFrameParam { Name = "escape", Type = "string", Required = false,
                Description = "Escape character (CSV only; default '\"')." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");
        var path = GetParameter<string>(parameters, "path");
        var format = (GetStringOrNull(parameters, "format") ?? string.Empty).ToLowerInvariant();
        var compression = GetStringOrNull(parameters, "compression");
        var mode = (GetStringOrNull(parameters, "mode") ?? "error").ToLowerInvariant();
        var header = GetBoolOrNull(parameters, "header");
        var delimiter = GetStringOrNull(parameters, "delimiter");
        var quote = GetStringOrNull(parameters, "quote");
        var escape = GetStringOrNull(parameters, "escape");
        var array = GetBoolOrNull(parameters, "array") ?? false;

        var isDelta = string.Equals(format, "delta", StringComparison.OrdinalIgnoreCase);
        var isJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);

        return Guard(parameters, options, _backend, ct =>
        {
            if (!isDelta && !Formats.Contains(format))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                    $"Unknown export format '{format}'. Use csv, parquet, json, or delta.");
            }

            if (array && !isJson)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'array' is only supported for JSON export.");
            }

            if (!Modes.Contains(mode))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Unknown export mode '{mode}'. Use error, append, or overwrite.");
            }

            if (mode == "append" && !isDelta)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'append' mode is only supported for Delta export.");
            }

            if (compression is not null)
            {
                if (!CompressionRegex().IsMatch(compression))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                        $"Invalid compression '{compression}'.");
                }

                if (!isDelta && format is not "parquet" and not "json")
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        $"Compression is not supported for '{format}' export; use parquet, json, or delta.");
                }

                if (isDelta)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Delta export currently uses snappy compression; the 'compression' parameter is not supported for delta.");
                }
            }

            var hasPartitionBy = parameters.GetValueOrDefault("partition_by") is System.Collections.IEnumerable
                and not string;

            if (hasPartitionBy && format is "csv" or "json")
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"'partition_by' is not supported for {format.ToUpperInvariant()} export.");
            }

            CsvExportOptions? csvOptions = null;
            if (header is not null || delimiter is not null || quote is not null || escape is not null)
            {
                if (format != "csv")
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        $"CSV options (header, delimiter, quote, escape) are only supported for CSV export; got '{format}'.");
                }

                ValidateSingleCharacter(delimiter, "delimiter");
                ValidateSingleCharacter(quote, "quote");
                ValidateSingleCharacter(escape, "escape");

                csvOptions = new CsvExportOptions(header, delimiter, quote, escape);
            }

            var entry = RequireDataset(_catalog, datasetId);

            EnforceWritePolicy(_pathPolicy, path);

            if (mode == "error" && (File.Exists(path) || Directory.Exists(path)))
            {
                throw new DataFrameException(DataFrameErrorCodes.TargetExists,
                    $"Target '{path}' already exists; set mode=overwrite to replace it or mode=append to add to a Delta table.",
                    new Dictionary<string, object?> { ["path"] = path });
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            var sw = Stopwatch.StartNew();
            List<string>? partitionBy = null;
            if (hasPartitionBy)
            {
                partitionBy = ((System.Collections.IEnumerable)parameters["partition_by"]!).Cast<object?>()
                    .Select(o => SqlText.ResolveColumnQuoted(o?.ToString() ?? string.Empty, entry.Schema))
                    .ToList();
            }

            if (isDelta)
            {
                _backend.ExportDelta(datasetId, path, mode, partitionBy, ct);
            }
            else
            {
                _backend.Export(datasetId, path, format, partitionBy, compression, mode == "overwrite", csvOptions, array, ct);
            }

            sw.Stop();

            // The catalog row count can be null (e.g. registered without one); re-count so the
            // envelope and the warning never report 0/blank for a non-empty dataset.
            var rowCount = entry.RowCount ?? _backend.CountRows(datasetId, ct);
            return DataFrameResponse.Ok(datasetId, entry.Schema, rowCount,
                Array.Empty<IReadOnlyDictionary<string, object?>>(),
                new DataFrameStats(sw.ElapsedMilliseconds, 0, rowCount),
                warnings: new[] { $"Exported {rowCount} rows to {path} ({format})." });
        });
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
    private static partial Regex CompressionRegex();

    private static void ValidateSingleCharacter(string? value, string name)
    {
        if (value is not null && value.Length != 1)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                $"'{name}' must be exactly one character; got '{value}'.");
        }
    }
}
