using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Predicates;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_filter</c> — selects rows matching a structured predicate tree.
/// See docs/operations.md#dataframe_filter and docs/operations.md#predicate-trees.
/// </summary>
public sealed class FilterOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public FilterOperation() : this(null!, null!, null) { }

    public FilterOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<FilterOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_filter",
        Name = "DataFrame Filter",
        Description =
            "Selects rows from a dataset that match a structured predicate tree (no SQL). A condition " +
            "node is { column, op, value|value_column|values|low/high }; op is one of eq, neq, gt, gte, " +
            "lt, lte, in, between, is_null, is_not_null, like, ilike, starts_with, ends_with, contains, " +
            "matches. Logical nodes are { op: and|or, conditions: [...] } and { op: not, condition: {...} }. " +
            "Result is registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "predicate", Type = "object", Required = true,
                Description = "A predicate tree of condition/logical nodes." },
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
            if (parameters.GetValueOrDefault("predicate") is not IReadOnlyDictionary<string, object?> predicateNode)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                    "'predicate' must be a predicate-tree object.");
            }

            var entry = RequireDataset(_catalog, fromId);
            var node = PredicateParser.Parse(predicateNode);
            var where = PredicateSqlRenderer.Render(node, entry.Schema);

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, "*", $"filter:{fromId}", sw,
                GetBoolOrNull(parameters, "explain") ?? false, whereClause: where, ct: ct);
        });
    }
}
