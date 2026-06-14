using Andy.Data;

namespace Andy.Data.Predicates;

/// <summary>
/// Parses and validates the structured predicate tree supplied to <c>dataframe_filter</c> into a
/// typed <see cref="PredicateNode"/>. The operator vocabulary is enumerated and closed — there is
/// no path from input to executed SQL/code. Malformed input throws <see cref="DataFrameException"/>
/// with <see cref="DataFrameErrorCodes.InvalidPredicate"/>. See docs/operations.md#predicate-trees.
/// </summary>
public static class PredicateParser
{
    private static readonly HashSet<string> Comparison = new(StringComparer.Ordinal)
        { "eq", "neq", "gt", "gte", "lt", "lte" };
    private static readonly HashSet<string> NullOps = new(StringComparer.Ordinal)
        { "is_null", "is_not_null" };
    private static readonly HashSet<string> TextOps = new(StringComparer.Ordinal)
        { "like", "ilike", "starts_with", "ends_with", "contains", "matches" };
    private static readonly HashSet<string> LogicalNary = new(StringComparer.Ordinal)
        { "and", "or" };

    public static PredicateNode Parse(IReadOnlyDictionary<string, object?> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var op = GetString(node, "op");

        // Logical nodes are distinguished by NOT having a "column" key.
        if (!node.ContainsKey("column"))
        {
            if (op is null)
            {
                throw Invalid("Predicate node must have an 'op' (logical) or a 'column' (condition).");
            }

            if (LogicalNary.Contains(op))
            {
                var children = GetList(node, "conditions")
                    ?? throw Invalid($"Logical '{op}' node requires a non-empty 'conditions' array.");
                if (children.Count == 0)
                {
                    throw Invalid($"Logical '{op}' node requires at least one condition.");
                }

                return new LogicalNode(op, children.Select(ParseChild).ToList());
            }

            if (op == "not")
            {
                if (node.TryGetValue("condition", out var inner) && inner is IReadOnlyDictionary<string, object?> d)
                {
                    return new NotNode(Parse(d));
                }

                throw Invalid("'not' node requires a single 'condition' object.");
            }

            throw Invalid($"Unknown logical operator '{op}'.");
        }

        // Condition node.
        var column = GetString(node, "column")
            ?? throw Invalid("Condition 'column' must be a non-empty string.");
        if (op is null)
        {
            throw Invalid($"Condition on '{column}' requires an 'op'.");
        }

        if (Comparison.Contains(op) || TextOps.Contains(op))
        {
            var hasValue = node.TryGetValue("value", out var v);
            var valueColumn = GetString(node, "value_column");

            if (valueColumn is not null)
            {
                if (hasValue)
                {
                    throw Invalid($"Operator '{op}' on '{column}' cannot have both 'value' and 'value_column'.");
                }

                return new ConditionNode(column, op, ValueColumn: valueColumn);
            }

            if (!hasValue)
            {
                throw Invalid($"Operator '{op}' on '{column}' requires a 'value' or 'value_column'.");
            }

            return new ConditionNode(column, op, Value: v);
        }

        if (NullOps.Contains(op))
        {
            return new ConditionNode(column, op);
        }

        if (op == "in")
        {
            var values = GetList(node, "values")
                ?? throw Invalid($"'in' on '{column}' requires a 'values' array.");
            if (values.Count == 0)
            {
                throw Invalid($"'in' on '{column}' requires a non-empty 'values' array.");
            }

            return new ConditionNode(column, op, Values: values);
        }

        if (op == "between")
        {
            if (!node.TryGetValue("low", out var low) || !node.TryGetValue("high", out var high))
            {
                throw Invalid($"'between' on '{column}' requires 'low' and 'high'.");
            }

            return new ConditionNode(column, op, Low: low, High: high);
        }

        throw Invalid($"Unknown operator '{op}' on column '{column}'.");
    }

    private static PredicateNode ParseChild(object? child)
    {
        if (child is IReadOnlyDictionary<string, object?> d)
        {
            return Parse(d);
        }

        throw Invalid("Each entry in 'conditions' must be a predicate node object.");
    }

    private static string? GetString(IReadOnlyDictionary<string, object?> node, string key) =>
        node.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static IReadOnlyList<object?>? GetList(IReadOnlyDictionary<string, object?> node, string key)
    {
        if (!node.TryGetValue(key, out var v) || v is null)
        {
            return null;
        }

        // Accept any non-string enumerable (object[], List<object?>, etc.).
        if (v is string)
        {
            return null;
        }

        if (v is System.Collections.IEnumerable e)
        {
            return e.Cast<object?>().ToList();
        }

        return null;
    }

    private static DataFrameException Invalid(string message) =>
        new(DataFrameErrorCodes.InvalidPredicate, message);
}
