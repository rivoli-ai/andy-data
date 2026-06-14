using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Predicates;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_group_by</c> — groups rows and computes aggregates.
/// See docs/operations.md#dataframe_group_by.
/// </summary>
public sealed class GroupByOperation : DataFrameOperationBase
{
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "count", "count_distinct", "sum", "avg", "min", "max", "median", "stddev", "var",
        "stddev_pop", "stddev_samp", "var_pop", "var_samp", "mode", "product",
        "bool_and", "bool_or", "approx_count_distinct", "approx_quantile",
        "arg_min", "arg_max",
        "first", "last", "list", "quantile", "corr", "covar",
    };

    private static readonly HashSet<string> TwoColumnFunctions = new(StringComparer.Ordinal)
        { "corr", "covar", "arg_min", "arg_max" };

    private static readonly HashSet<string> QuantileFunctions = new(StringComparer.Ordinal)
        { "quantile", "approx_quantile" };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public GroupByOperation() : this(null!, null!, null) { }

    public GroupByOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<GroupByOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_group_by",
        Name = "DataFrame Group By",
        Description =
            "Groups rows by zero or more columns and computes aggregates. 'group_by' is an array of " +
            "column names (empty for a grand total). 'aggregations' is an array of { column, function, " +
            "alias, q?, column2? }; function is one of count, count_distinct, approx_count_distinct, sum, " +
            "product, avg, min, max, median, mode, stddev, stddev_pop, stddev_samp, var, var_pop, var_samp, " +
            "bool_and, bool_or, first, last, list, quantile, approx_quantile, corr, covar, arg_min, arg_max. " +
            "Use column \"*\" with count for a row count. 'quantile'/'approx_quantile' require 'q' in [0,1]; " +
            "'corr', 'covar', 'arg_min', 'arg_max' require 'column2' (for arg_min/arg_max, 'column' is the " +
            "value returned and 'column2' is the column whose min/max selects the row). Result is " +
            "registered under 'into' (or replaces dataset_id). Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Input dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = false,
                Pattern = DatasetIdPattern, Description = "Output dataset id (defaults to dataset_id)." },
            new DataFrameParam { Name = "group_by", Type = "array", Required = true,
                Description = "Grouping column names (may be empty for a grand total)." },
            new DataFrameParam { Name = "aggregations", Type = "array", Required = true,
                Description = "Array of { column, function, alias, q?, column2? } aggregate specs." },
            new DataFrameParam { Name = "having", Type = "object", Required = false,
                Description = "Optional predicate tree applied to aggregated results (SQL HAVING). " +
                    "Referenced columns must be group keys or declared aggregate aliases." },
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

            var groupKeys = ToList(parameters.GetValueOrDefault("group_by"))
                .Select(o => SqlText.ResolveColumn(AsString(o, "group_by entry"), entry.Schema))
                .ToList();
            var groupCols = groupKeys.Select(SqlText.QuoteIdent).ToList();

            var aggSpecs = ToList(parameters.GetValueOrDefault("aggregations"));
            if (aggSpecs.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                    "'aggregations' must contain at least one { column, function, alias, q?, column2? }.");
            }

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var projections = new List<string>(groupCols);
            foreach (var spec in aggSpecs)
            {
                if (spec is not IReadOnlyDictionary<string, object?> agg)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        "Each aggregation must be a { column, function, alias, q?, column2? } object.");
                }

                var func = (agg.TryGetValue("function", out var fn) ? fn?.ToString() : null)?.ToLowerInvariant();
                if (func is null || !Functions.Contains(func))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        $"Unknown aggregate function '{func}'.");
                }

                var column = agg.TryGetValue("column", out var col) ? col?.ToString() : null;
                if (string.IsNullOrWhiteSpace(column))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        $"Aggregation '{func}' requires a 'column' (use \"*\" for count).");
                }

                if (func == "count" && column == "*")
                {
                    // count(*) is the only wildcard aggregate; no q/column2 allowed.
                }
                else if (TwoColumnFunctions.Contains(func))
                {
                    // Resolve column to validate schema membership, but RenderAggregate needs the raw names.
                    _ = SqlText.ResolveColumnQuoted(column, entry.Schema);
                }
                else
                {
                    _ = SqlText.ResolveColumnQuoted(column, entry.Schema);
                }

                var column2 = agg.TryGetValue("column2", out var c2) ? c2?.ToString() : null;
                if (TwoColumnFunctions.Contains(func))
                {
                    if (string.IsNullOrWhiteSpace(column2))
                    {
                        throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                            $"Aggregation '{func}' requires a 'column2'.");
                    }

                    _ = SqlText.ResolveColumnQuoted(column2, entry.Schema);
                }
                else if (!string.IsNullOrWhiteSpace(column2))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        $"Aggregation '{func}' does not accept 'column2'.");
                }

                double? q = null;
                if (QuantileFunctions.Contains(func))
                {
                    if (!agg.TryGetValue("q", out var qv) || qv is null)
                    {
                        throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                            $"Aggregation '{func}' requires a 'q' value in [0,1].");
                    }

                    try
                    {
                        q = Convert.ToDouble(qv, CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                            $"Aggregation '{func}' requires 'q' to be a number in [0,1].");
                    }

                    if (q is < 0 or > 1)
                    {
                        throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                            $"Aggregation '{func}' requires 'q' in [0,1].");
                    }
                }
                else if (agg.TryGetValue("q", out var unusedQ) && unusedQ is not null)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                        $"Aggregation '{func}' does not accept 'q'.");
                }

                var alias = agg.TryGetValue("alias", out var al) ? al?.ToString() : null;
                if (string.IsNullOrWhiteSpace(alias))
                {
                    alias = TwoColumnFunctions.Contains(func)
                        ? $"{func}_{column}_{column2}"
                        : $"{func}_{column}";
                }

                var expr = RenderAggregate(func, column!, column2, q, entry.Schema);
                projections.Add($"{expr} AS {SqlText.QuoteIdent(alias!)}");
                aliases.Add(alias!);
            }

            string? havingClause = null;
            if (parameters.GetValueOrDefault("having") is IReadOnlyDictionary<string, object?> havingNode)
            {
                havingClause = RenderHaving(havingNode, groupKeys, aliases);
            }
            else if (parameters.ContainsKey("having") && parameters.GetValueOrDefault("having") is not null)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                    "'having' must be a predicate-tree object.");
            }

            var sw = Stopwatch.StartNew();
            return Materialize(_backend, _catalog, intoId, fromId, string.Join(", ", projections),
                $"group_by:{fromId}", sw, GetBoolOrNull(parameters, "explain") ?? false,
                groupByClause: groupCols.Count > 0 ? string.Join(", ", groupCols) : null,
                havingClause: havingClause, ct: ct);
        });
    }

    private static string RenderAggregate(
        string func,
        string column,
        string? column2,
        double? q,
        IReadOnlyList<ColumnSchema> schema)
    {
        if (func == "count" && column == "*")
        {
            return "count(*)";
        }

        var col = SqlText.ResolveColumnQuoted(column, schema);

        return func switch
        {
            "count" => $"count({col})",
            "count_distinct" => $"count(DISTINCT {col})",
            "var" => $"variance({col})",
            "quantile" => $"quantile_cont({col}, {q!.Value.ToString(CultureInfo.InvariantCulture)})",
            "approx_quantile" => $"approx_quantile({col}, {q!.Value.ToString(CultureInfo.InvariantCulture)})",
            "corr" => $"corr({col}, {SqlText.ResolveColumnQuoted(column2!, schema)})",
            "covar" => $"covar_samp({col}, {SqlText.ResolveColumnQuoted(column2!, schema)})",
            "arg_min" => $"arg_min({col}, {SqlText.ResolveColumnQuoted(column2!, schema)})",
            "arg_max" => $"arg_max({col}, {SqlText.ResolveColumnQuoted(column2!, schema)})",
            // sum, avg, min, max, median, stddev[_pop|_samp], var_pop, var_samp, mode, product,
            // bool_and, bool_or, approx_count_distinct, first, last, list render as func(col).
            _ => $"{func}({col})",
        };
    }

    private static List<object?> ToList(object? value)
    {
        if (value is null || value is string || value is not System.Collections.IEnumerable e)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation,
                "'group_by' and 'aggregations' must be arrays.");
        }

        return e.Cast<object?>().ToList();
    }

    private static string AsString(object? o, string what) =>
        o?.ToString() ?? throw new DataFrameException(DataFrameErrorCodes.InvalidAggregation, $"Invalid {what}.");

    private static string RenderHaving(
        IReadOnlyDictionary<string, object?> havingNode,
        IReadOnlyList<string> groupKeys,
        IReadOnlySet<string> aliases)
    {
        PredicateNode node;
        try
        {
            node = PredicateParser.Parse(havingNode);
        }
        catch (DataFrameException ex) when (ex.ErrorCode == DataFrameErrorCodes.InvalidPredicate)
        {
            throw;
        }
        catch (DataFrameException)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                "Unable to parse 'having' predicate.");
        }

        // HAVING may only reference group keys or aggregate aliases.
        var availableColumns = groupKeys.Concat(aliases)
            .Select(name => new ColumnSchema(name, "UNKNOWN"))
            .ToList();

        try
        {
            return PredicateSqlRenderer.Render(node, availableColumns);
        }
        catch (DataFrameException ex) when (ex.ErrorCode == DataFrameErrorCodes.ColumnNotFound)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"HAVING references column '{ex.Details?.GetValueOrDefault("column")}' which is neither a group key nor an aggregate alias.");
        }
    }
}
