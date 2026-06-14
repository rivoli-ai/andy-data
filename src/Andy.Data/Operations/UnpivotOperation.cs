using System.Diagnostics;
using System.Text.RegularExpressions;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_unpivot</c> — reshapes wide data to long by unpivoting value columns.
/// See docs/operations.md#dataframe_unpivot.
/// </summary>
public sealed class UnpivotOperation : DataFrameOperationBase
{
    private static readonly Regex IdentPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public UnpivotOperation() : this(null!, null!, null) { }

    public UnpivotOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<UnpivotOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_unpivot",
        Name = "DataFrame Unpivot",
        Description =
            "Reshapes a dataset from wide to long. 'id_columns' are kept as row identifiers; " +
            "'value_columns' are stacked into two output columns: 'name_to' (the former column name, " +
            "default 'name') and 'value_to' (the value, default 'value'). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "id_columns", Type = "array", Required = true,
                Description = "Columns to keep as row identifiers (may be empty)." },
            new DataFrameParam { Name = "value_columns", Type = "array", Required = true,
                Description = "Columns to unpivot into rows." },
            new DataFrameParam { Name = "name_to", Type = "string", Required = false, DefaultValue = "name",
                Description = "Output column name for the former value column name." },
            new DataFrameParam { Name = "value_to", Type = "string", Required = false, DefaultValue = "value",
                Description = "Output column name for the unpivoted value." },
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
        var nameTo = GetStringOrNull(parameters, "name_to") ?? "name";
        var valueTo = GetStringOrNull(parameters, "value_to") ?? "value";

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, fromId);

            if (!IdentPattern.IsMatch(nameTo))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"'name_to' must be a valid identifier, got '{nameTo}'.");
            }

            if (!IdentPattern.IsMatch(valueTo))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"'value_to' must be a valid identifier, got '{valueTo}'.");
            }

            var idColumns = ToStringList("id_columns", parameters.GetValueOrDefault("id_columns"));
            var valueColumns = ToStringList("value_columns", parameters.GetValueOrDefault("value_columns"));

            if (valueColumns.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'value_columns' must contain at least one column name.");
            }

            var idSet = idColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var overlap = valueColumns.FirstOrDefault(c => idSet.Contains(c));
            if (overlap is not null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Column '{overlap}' appears in both 'id_columns' and 'value_columns'.");
            }

            var idQuoted = idColumns.Select(c => SqlText.ResolveColumnQuoted(c, entry.Schema)).ToList();
            var valueQuoted = valueColumns.Select(c => SqlText.ResolveColumnQuoted(c, entry.Schema)).ToList();

            var sw = Stopwatch.StartNew();
            var plan = GetBoolOrNull(parameters, "explain") ?? false
                ? _backend.ExplainUnpivot(fromId, idQuoted, valueQuoted, SqlText.QuoteIdent(nameTo), SqlText.QuoteIdent(valueTo), ct)
                : null;
            var schema = _backend.Unpivot(intoId, fromId, idQuoted, valueQuoted, SqlText.QuoteIdent(nameTo), SqlText.QuoteIdent(valueTo), ct);
            return Finish(_backend, _catalog, intoId, schema, $"unpivot:{fromId}", sw, plan: plan, ct: ct);
        });
    }
}
