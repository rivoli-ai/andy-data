using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_profile</c> — per-column statistics (null/distinct counts, min/max, quantiles).
/// See docs/operations.md#dataframe_profile.
/// </summary>
public sealed class ProfileOperation : DataFrameOperationBase
{
    private static readonly double[] DefaultQuantiles = { 0.25, 0.5, 0.75 };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public ProfileOperation() : this(null!, null!, null) { }

    public ProfileOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<ProfileOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_profile",
        Name = "DataFrame Profile",
        Description =
            "Computes per-column statistics for a dataset (a pandas describe()-style summary): " +
            "null_count, distinct_count, count (non-null), min, max, and — for numeric columns — " +
            "mean, std (sample standard deviation), and quantiles. Optional 'columns' subset and " +
            "'quantiles' (default [0.25,0.5,0.75]). Returns the standard envelope where preview_rows " +
            "holds one stats row per column.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Dataset to profile." },
            new DataFrameParam { Name = "columns", Type = "array", Required = false,
                Description = "Columns to profile (default: all)." },
            new DataFrameParam { Name = "quantiles", Type = "array", Required = false,
                DefaultValue = DefaultQuantiles,
                Description = "Quantiles in [0,1] for numeric columns (default [0.25,0.5,0.75])." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, datasetId);

            var requested = ToStringList("columns", parameters.GetValueOrDefault("columns"));
            var columns = requested.Count == 0
                ? entry.Schema
                : requested.Select(name =>
                {
                    var canon = SqlText.ResolveColumn(name, entry.Schema); // validates existence
                    return entry.Schema.First(c => string.Equals(c.Name, canon, StringComparison.OrdinalIgnoreCase));
                }).ToList();

            var quantiles = ParseQuantiles(parameters.GetValueOrDefault("quantiles"));

            var sw = Stopwatch.StartNew();
            var rows = _backend.Profile(datasetId, columns, quantiles, ct);
            sw.Stop();

            var schema = BuildStatsSchema(rows);
            var preview = rows.Take(PreviewLimit).ToList();
            return DataFrameResponse.Ok(datasetId, schema, rows.Count, preview,
                new DataFrameStats(sw.ElapsedMilliseconds, 0, rows.Count));
        });
    }

    private static IReadOnlyList<double> ParseQuantiles(object? value)
    {
        var list = ToStringList("quantiles", value);
        if (list.Count == 0)
        {
            return DefaultQuantiles;
        }

        var result = new List<double>();
        foreach (var s in list)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) || p < 0 || p > 1)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Quantile '{s}' must be a number in [0, 1].");
            }

            result.Add(p);
        }

        return result;
    }

    private static IReadOnlyList<ColumnSchema> BuildStatsSchema(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
        {
            return Array.Empty<ColumnSchema>();
        }

        var firstRow = rows[0];
        var columnType = firstRow.TryGetValue("type", out var t) ? t?.ToString() ?? "" : "";

        return firstRow.Keys.Select(k => new ColumnSchema(k, k switch
        {
            "column" or "type" => "VARCHAR",
            "null_count" or "distinct_count" or "count" => "BIGINT",
            "min" or "max" => MapMinMaxSchemaType(columnType),
            _ => "DOUBLE",
        })).ToList();
    }

    private static string MapMinMaxSchemaType(string duckType)
    {
        var t = duckType.Trim().ToUpperInvariant();

        if (IsNumeric(t))
        {
            return "DOUBLE";
        }

        if (t.StartsWith("VARCHAR") || t.StartsWith("CHAR") || t == "BOOLEAN" || t == "BOOL"
            || t.StartsWith("DATE") || t.StartsWith("TIME") || t.StartsWith("TIMESTAMP") || t == "UUID")
        {
            return duckType;
        }

        return duckType;
    }

    private static bool IsNumeric(string type)
    {
        var t = type.ToUpperInvariant();
        return t.Contains("INT") || t.StartsWith("DEC") || t.StartsWith("NUMERIC")
            || t is "FLOAT" or "DOUBLE" or "REAL";
    }
}
