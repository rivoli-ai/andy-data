using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_select</c> — projects, renames, and reorders columns.
/// See docs/operations.md#dataframe_select.
/// </summary>
public sealed class SelectOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public SelectOperation() : this(null!, null!, null) { }

    public SelectOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<SelectOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_select",
        Name = "DataFrame Select",
        Description =
            "Projects, renames, and reorders columns of a dataset. 'columns' is an array whose " +
            "entries are either a column name (string) or an object { column, as } to rename. " +
            "Result is registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "columns", Type = "array", Required = true,
                Description = "Column names, or { column, as } objects to rename." },
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
            if (parameters.GetValueOrDefault("columns") is not System.Collections.IEnumerable items ||
                parameters["columns"] is string)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'columns' must be a non-empty array.");
            }

            var projections = new List<string>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case string col:
                        projections.Add(SqlText.ResolveColumnQuoted(col, entry.Schema));
                        break;
                    case IReadOnlyDictionary<string, object?> spec:
                        var name = spec.TryGetValue("column", out var c) ? c?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                                "Each column object requires a 'column'.");
                        }

                        var resolved = SqlText.ResolveColumnQuoted(name, entry.Schema);
                        var alias = spec.TryGetValue("as", out var a) ? a?.ToString() : null;
                        projections.Add(string.IsNullOrWhiteSpace(alias)
                            ? resolved
                            : $"{resolved} AS {SqlText.QuoteIdent(alias!)}");
                        break;
                    default:
                        throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                            "Each 'columns' entry must be a string or a { column, as } object.");
                }
            }

            if (projections.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'columns' must select at least one column.");
            }

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections),
                $"select:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
        });
    }
}
