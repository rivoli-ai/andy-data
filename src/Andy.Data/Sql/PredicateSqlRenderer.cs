using Andy.Data;
using Andy.Data.Predicates;

namespace Andy.Data.Sql;

/// <summary>
/// Renders a validated <see cref="PredicateNode"/> tree into a DuckDB WHERE-clause fragment.
/// Columns are resolved/validated against the dataset schema (COLUMN_NOT_FOUND otherwise) and
/// quoted; values are rendered as escaped literals. The operator set is closed.
/// </summary>
internal static class PredicateSqlRenderer
{
    public static string Render(PredicateNode node, IReadOnlyList<ColumnSchema> schema) => node switch
    {
        LogicalNode l => "(" + string.Join(
            l.Op == "or" ? " OR " : " AND ",
            l.Conditions.Select(c => Render(c, schema))) + ")",
        NotNode n => "(NOT " + Render(n.Condition, schema) + ")",
        ConditionNode c => RenderCondition(c, schema),
        _ => throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate, "Unknown predicate node."),
    };

    private static string RenderCondition(ConditionNode c, IReadOnlyList<ColumnSchema> schema)
    {
        var col = SqlText.ResolveColumnQuoted(c.Column, schema);
        var right = c.ValueColumn is not null
            ? SqlText.ResolveColumnQuoted(c.ValueColumn, schema)
            : SqlText.Literal(c.Value);

        return c.Op switch
        {
            "eq" => $"{col} = {right}",
            "neq" => $"{col} <> {right}",
            "gt" => $"{col} > {right}",
            "gte" => $"{col} >= {right}",
            "lt" => $"{col} < {right}",
            "lte" => $"{col} <= {right}",
            "is_null" => $"{col} IS NULL",
            "is_not_null" => $"{col} IS NOT NULL",
            "in" => $"{col} IN ({string.Join(", ", c.Values!.Select(SqlText.Literal))})",
            "between" => $"{col} BETWEEN {SqlText.Literal(c.Low)} AND {SqlText.Literal(c.High)}",
            "like" => $"{col} LIKE {right}",
            "ilike" => $"{col} ILIKE {right}",
            "starts_with" => $"starts_with({col}, {right})",
            "ends_with" => $"ends_with({col}, {right})",
            "contains" => $"contains({col}, {right})",
            "matches" => $"regexp_matches({col}, {right})",
            _ => throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"Unknown operator '{c.Op}'."),
        };
    }
}
