using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_rename</c> — renames one or more columns while keeping all other columns unchanged.
/// See docs/operations.md#dataframe_rename.
/// </summary>
public sealed class RenameOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public RenameOperation() : this(null!, null!, null) { }

    public RenameOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<RenameOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_rename",
        Name = "DataFrame Rename",
        Description =
            "Renames one or more columns of a dataset. 'columns' is an object mapping existing column " +
            "names to new names; all unmentioned columns are kept unchanged and column order is preserved. " +
            "Result is registered under 'into'. Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Output dataset id." },
            new DataFrameParam { Name = "columns", Type = "object", Required = true,
                Description = "Map of old column name → new column name." },
            new DataFrameParam { Name = "explain", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Include the DuckDB query plan in stats.plan." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var fromId = GetParameter<string>(parameters, "dataset_id");
        var intoId = GetParameter<string>(parameters, "into");

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, fromId);

            if (parameters.GetValueOrDefault("columns") is not IReadOnlyDictionary<string, object?> renameMap)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'columns' must be an object mapping old column names to new column names.");
            }

            if (renameMap.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'columns' must contain at least one rename.");
            }

            // Validate the rename map: no empty names, and no two old columns may map to the same new name.
            var newNames = new HashSet<string>(StringComparer.Ordinal);
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in renameMap)
            {
                var oldName = kvp.Key;
                var newName = kvp.Value?.ToString();
                if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "Each 'columns' entry must have a non-empty old and new column name.");
                }

                if (!newNames.Add(newName))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        $"Multiple columns cannot be renamed to '{newName}'.",
                        new Dictionary<string, object?> { ["new_name"] = newName });
                }

                renames[oldName] = newName;
            }

            // Verify every old column exists in the source schema.
            foreach (var oldName in renames.Keys)
            {
                if (!entry.Schema.Any(c => string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DataFrameException(DataFrameErrorCodes.ColumnNotFound,
                        $"Column '{oldName}' does not exist in the dataset.",
                        new Dictionary<string, object?> { ["column"] = oldName });
                }
            }

            // Build a projection that preserves schema order and renames the requested columns.
            var projections = new List<string>();
            foreach (var col in entry.Schema)
            {
                var matchedOldName = renames.Keys.FirstOrDefault(k =>
                    string.Equals(k, col.Name, StringComparison.OrdinalIgnoreCase));
                if (matchedOldName is not null)
                {
                    projections.Add($"{SqlText.QuoteIdent(col.Name)} AS {SqlText.QuoteIdent(renames[matchedOldName])}");
                }
                else
                {
                    projections.Add(SqlText.QuoteIdent(col.Name));
                }
            }

            var sw = Stopwatch.StartNew();
            var explain = GetBoolOrNull(parameters, "explain") ?? false;
            return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections),
                $"rename:{fromId}", sw, explain, ct: ct);
        });
    }
}
