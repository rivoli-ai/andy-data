using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Expressions;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_with_column</c> — adds or replaces a column computed from an expression tree.
/// See docs/operations.md#dataframe_with_column and docs/operations.md#expression-trees.
/// </summary>
public sealed class WithColumnOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public WithColumnOperation() : this(null!, null!, null) { }

    public WithColumnOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<WithColumnOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_with_column",
        Name = "DataFrame With Column",
        Description =
            "Adds or replaces a column computed from a structured expression tree (no SQL). Leaves are " +
            "{ column } or { literal }; operator nodes are { op, args } with op in add, subtract, " +
            "multiply, divide, modulo, round, abs, floor, ceil, power, ln, concat, upper, lower, trim, " +
            "substring, length, replace, split_part, lpad, rpad, regexp_replace, regexp_extract, " +
            "regexp_matches, coalesce, case, date_trunc, date_part, date_diff, strptime, date_add, hash, " +
            "plus { op: cast|try_cast, to, args }. Result is registered under 'into' (or replaces dataset_id). " +
            "Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "name", Type = "string", Required = true,
                Description = "Name of the new or replaced column." },
            new DataFrameParam { Name = "expression", Type = "object", Required = true,
                Description = "An expression tree." },
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
        var name = GetParameter<string>(parameters, "name");

        return Guard(parameters, options, _backend, ct =>
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "A non-empty 'name' is required.");
            }

            if (parameters.GetValueOrDefault("expression") is not IReadOnlyDictionary<string, object?> exprNode)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                    "'expression' must be an expression-tree object.");
            }

            var entry = RequireDataset(_catalog, fromId);
            var expr = ExpressionSqlRenderer.Render(ExpressionParser.Parse(exprNode), entry.Schema);
            var quotedName = SqlText.QuoteIdent(name);

            // Replace an existing column in place, or append a new one.
            var exists = entry.Schema.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            var selectClause = exists
                ? $"* REPLACE ({expr} AS {quotedName})"
                : $"*, {expr} AS {quotedName}";

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, selectClause,
                $"with_column:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false, ct: ct);
        });
    }
}
