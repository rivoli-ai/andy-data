using Andy.Data;
using Andy.Data.Predicates;

namespace Andy.Data.Expressions;

/// <summary>
/// Parses and validates the structured expression tree supplied to <c>dataframe_with_column</c>
/// into a typed <see cref="ExprNode"/>. Leaves are <c>{ "column": ... }</c> or <c>{ "literal": ... }</c>;
/// operator nodes are <c>{ "op": ..., "args": [...] }</c> plus special <c>cast</c> and <c>try_cast</c>.
/// The function vocabulary is enumerated and closed — no SQL/code is accepted from input. Malformed
/// input throws <see cref="DataFrameException"/>. See docs/operations.md#expression-trees.
/// </summary>
public static class ExpressionParser
{
    private static readonly Dictionary<string, (int Min, int Max)> Functions = new(StringComparer.Ordinal)
    {
        // arithmetic
        ["add"] = (2, int.MaxValue), ["subtract"] = (2, 2), ["multiply"] = (2, int.MaxValue),
        ["divide"] = (2, 2), ["modulo"] = (2, 2),
        ["round"] = (1, 2), ["abs"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1),
        ["power"] = (2, 2), ["ln"] = (1, 1),
        // string
        ["concat"] = (2, int.MaxValue), ["upper"] = (1, 1), ["lower"] = (1, 1), ["trim"] = (1, 1),
        ["substring"] = (2, 3), ["length"] = (1, 1),
        ["replace"] = (3, 3), ["split_part"] = (3, 3), ["lpad"] = (2, 3), ["rpad"] = (2, 3),
        ["regexp_replace"] = (3, 3), ["regexp_extract"] = (2, 3), ["regexp_matches"] = (2, 2),
        // conditional
        ["coalesce"] = (1, int.MaxValue), ["nullif"] = (2, 2),
        ["greatest"] = (2, int.MaxValue), ["least"] = (2, int.MaxValue), ["clip"] = (3, 3),
        // temporal
        ["date_trunc"] = (2, 2), ["date_part"] = (2, 2), ["date_diff"] = (3, 3),
        ["strptime"] = (2, 2), ["date_add"] = (3, 3),
        // list
        ["list_length"] = (1, 1), ["list_contains"] = (2, 2), ["array"] = (1, int.MaxValue),
        // misc
        ["hash"] = (1, 1),
    };

    public static ExprNode Parse(IReadOnlyDictionary<string, object?> node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.TryGetValue("column", out var col))
        {
            if (col is string s && !string.IsNullOrWhiteSpace(s))
            {
                return new ColumnExpr(s);
            }

            throw Invalid("'column' must be a non-empty string.");
        }

        if (node.ContainsKey("literal"))
        {
            node.TryGetValue("literal", out var lit);
            return new LiteralExpr(lit);
        }

        var op = node.TryGetValue("op", out var o) && o is string os && !string.IsNullOrWhiteSpace(os) ? os : null;
        if (op is null)
        {
            throw Invalid("Expression node must have 'column', 'literal', or 'op'.");
        }

        if (op == "cast")
        {
            return ParseCast(node, tryCast: false);
        }

        if (op == "try_cast")
        {
            return ParseCast(node, tryCast: true);
        }

        if (op == "case")
        {
            return ParseCase(node);
        }

        if (op == "struct_field")
        {
            return ParseStructField(node);
        }

        if (!Functions.TryGetValue(op, out var arity))
        {
            throw Invalid($"Unknown expression operator '{op}'.");
        }

        var args = ParseArgs(node);
        if (args.Count < arity.Min || args.Count > arity.Max)
        {
            var range = arity.Max == int.MaxValue ? $"at least {arity.Min}" : $"{arity.Min}..{arity.Max}";
            throw Invalid($"Operator '{op}' expects {range} arguments but got {args.Count}.");
        }

        return new FuncExpr(op, args);
    }

    private static ExprNode ParseCast(IReadOnlyDictionary<string, object?> node, bool tryCast)
    {
        var to = node.TryGetValue("to", out var t) && t is string ts && !string.IsNullOrWhiteSpace(ts) ? ts : null;
        if (to is null)
        {
            throw Invalid($"'{(tryCast ? "try_cast" : "cast")}' requires a 'to' target type.");
        }

        var castArgs = ParseArgs(node);
        if (castArgs.Count != 1)
        {
            throw Invalid($"'{(tryCast ? "try_cast" : "cast")}' requires exactly one argument.");
        }

        return tryCast ? new TryCastExpr(to, castArgs[0]) : new CastExpr(to, castArgs[0]);
    }

    private static ExprNode ParseCase(IReadOnlyDictionary<string, object?> node)
    {
        if (!node.TryGetValue("when", out var whenRaw) || whenRaw is null || whenRaw is string)
        {
            throw Invalid("'case' requires a 'when' array of { predicate, then } objects.");
        }

        if (whenRaw is not System.Collections.IEnumerable whenEnum)
        {
            throw Invalid("'case' requires a 'when' array of { predicate, then } objects.");
        }

        var whens = new List<WhenClause>();
        foreach (var item in whenEnum)
        {
            if (item is not IReadOnlyDictionary<string, object?> whenNode)
            {
                throw Invalid("Each 'when' entry must be an object with 'predicate' and 'then'.");
            }

            if (!whenNode.TryGetValue("predicate", out var predObj) ||
                predObj is not IReadOnlyDictionary<string, object?> predDict)
            {
                throw Invalid("Each 'when' entry must have a 'predicate' object.");
            }

            if (!whenNode.TryGetValue("then", out var thenObj) ||
                thenObj is not IReadOnlyDictionary<string, object?> thenDict)
            {
                throw Invalid("Each 'when' entry must have a 'then' expression object.");
            }

            whens.Add(new WhenClause(PredicateParser.Parse(predDict), Parse(thenDict)));
        }

        if (whens.Count == 0)
        {
            throw Invalid("'case' requires at least one 'when' clause.");
        }

        ExprNode? elseExpr = null;
        if (node.TryGetValue("else", out var elseObj) && elseObj is not null)
        {
            if (elseObj is not IReadOnlyDictionary<string, object?> elseDict)
            {
                throw Invalid("'case' 'else' must be an expression object.");
            }

            elseExpr = Parse(elseDict);
        }

        return new CaseExpr(whens, elseExpr);
    }

    private static ExprNode ParseStructField(IReadOnlyDictionary<string, object?> node)
    {
        if (!node.TryGetValue("field", out var fieldObj) || fieldObj is not string field || string.IsNullOrWhiteSpace(field))
        {
            throw Invalid("'struct_field' requires a non-empty 'field' string.");
        }

        var args = ParseArgs(node);
        if (args.Count != 1)
        {
            throw Invalid("'struct_field' requires exactly one argument.");
        }

        return new StructFieldExpr(args[0], field);
    }

    private static List<ExprNode> ParseArgs(IReadOnlyDictionary<string, object?> node)
    {
        if (!node.TryGetValue("args", out var v) || v is null || v is string ||
            v is not System.Collections.IEnumerable e)
        {
            throw Invalid("Operator node requires an 'args' array of expression nodes.");
        }

        var result = new List<ExprNode>();
        foreach (var item in e)
        {
            if (item is IReadOnlyDictionary<string, object?> d)
            {
                result.Add(Parse(d));
            }
            else
            {
                throw Invalid("Each entry in 'args' must be an expression node object.");
            }
        }

        return result;
    }

    private static DataFrameException Invalid(string message) =>
        new(DataFrameErrorCodes.InvalidPredicate, message);
}
