using System.Text.RegularExpressions;
using Andy.Data;
using Andy.Data.Expressions;
using Andy.Data.Predicates;

namespace Andy.Data.Sql;

/// <summary>
/// Renders a validated <see cref="ExprNode"/> tree into a DuckDB scalar-expression fragment.
/// Columns are resolved/quoted against the schema, literals escaped, and the function set is closed.
/// Cast target types are validated against a conservative pattern; strptime formats and date_add
/// units are checked against a closed vocabulary.
/// </summary>
internal static partial class ExpressionSqlRenderer
{
    private static readonly HashSet<string> StrptimeFormats = new(StringComparer.Ordinal)
    {
        // Date-only
        "%Y-%m-%d",
        "%Y/%m/%d",
        "%Y.%m.%d",
        "%Y%m%d",
        "%m/%d/%Y",
        "%m-%d-%Y",
        "%d/%m/%Y",
        "%d-%m-%Y",
        "%d.%m.%Y",
        "%d-%b-%y",
        "%d-%b-%Y",
        "%d %b %Y",
        "%d %B %Y",
        "%b %d, %Y",
        "%B %d, %Y",
        // Date + time
        "%Y-%m-%d %H:%M",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%d %H:%M:%S.%f",
        "%Y-%m-%dT%H:%M",
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%dT%H:%M:%S.%f",
        "%Y-%m-%dT%H:%M:%SZ",
        "%Y-%m-%dT%H:%M:%S.%fZ",
        "%m/%d/%Y %H:%M",
        "%m/%d/%Y %H:%M:%S",
        "%d/%m/%Y %H:%M:%S",
        "%Y%m%d%H%M%S",
        // Time-only
        "%H:%M",
        "%H:%M:%S",
        "%H:%M:%S.%f",
    };

    private static readonly HashSet<string> DateAddUnits = new(StringComparer.Ordinal)
    {
        "year", "years",
        "month", "months",
        "day", "days",
        "hour", "hours",
        "minute", "minutes",
        "second", "seconds",
    };

    private static readonly HashSet<string> DatePartUnits = new(StringComparer.Ordinal)
    {
        "year", "years",
        "month", "months",
        "day", "days",
        "hour", "hours",
        "minute", "minutes",
        "second", "seconds",
        "quarter", "quarters",
        "week", "weeks",
        "millisecond", "milliseconds",
        "microsecond", "microseconds",
        "nanosecond", "nanoseconds",
        "decade", "decades",
        "century", "centuries",
        "millennium", "millenniums",
    };

    public static string Render(ExprNode node, IReadOnlyList<ColumnSchema> schema) => node switch
    {
        ColumnExpr c => SqlText.ResolveColumnQuoted(c.Column, schema),
        LiteralExpr l => SqlText.Literal(l.Value),
        CastExpr ca => $"CAST({Render(ca.Arg, schema)} AS {ValidateType(ca.ToType)})",
        TryCastExpr tc => $"TRY_CAST({Render(tc.Arg, schema)} AS {ValidateType(tc.ToType)})",
        CaseExpr ca => RenderCase(ca, schema),
        StructFieldExpr s => RenderStructField(s, schema),
        FuncExpr f => RenderFunc(f, schema),
        _ => throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate, "Unknown expression node."),
    };

    private static string RenderStructField(StructFieldExpr s, IReadOnlyList<ColumnSchema> schema)
    {
        var parentSql = Render(s.Expr, schema);
        var parentType = ResolveType(s.Expr, schema);
        if (parentType is not null)
        {
            if (!TryGetStructFieldType(parentType, s.Field, out _))
            {
                throw new DataFrameException(DataFrameErrorCodes.ColumnNotFound,
                    $"Field '{s.Field}' does not exist in struct of type '{parentType}'.");
            }
        }

        return $"{parentSql}.{SqlText.QuoteIdent(s.Field)}";
    }

    /// <summary>
    /// Resolves the DuckDB type string for an expression when it can be determined from the schema.
    /// Returns null for expressions whose type cannot be statically inferred.
    /// </summary>
    private static string? ResolveType(ExprNode node, IReadOnlyList<ColumnSchema> schema) => node switch
    {
        ColumnExpr c => schema.FirstOrDefault(col =>
            string.Equals(col.Name, c.Column, StringComparison.OrdinalIgnoreCase))?.Type,
        StructFieldExpr s => ResolveType(s.Expr, schema) is string parentType &&
            TryGetStructFieldType(parentType, s.Field, out var fieldType)
                ? fieldType
                : null,
        _ => null,
    };

    /// <summary>
    /// Parses a DuckDB STRUCT(...) type string and extracts the type of the named field.
    /// </summary>
    private static bool TryGetStructFieldType(string structType, string field, out string fieldType)
    {
        fieldType = null!;
        if (!structType.StartsWith("STRUCT(", StringComparison.OrdinalIgnoreCase) ||
            !structType.EndsWith(")"))
        {
            return false;
        }

        var inner = structType[7..^1];
        var fields = SplitStructFields(inner);
        foreach (var f in fields)
        {
            var (name, type) = ParseStructField(f);
            if (string.Equals(name, field, StringComparison.OrdinalIgnoreCase))
            {
                fieldType = type;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SplitStructFields(string inner)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch == '(' || ch == '<')
            {
                depth++;
            }
            else if (ch == ')' || ch == '>')
            {
                depth--;
            }
            else if (ch == ',' && depth == 0)
            {
                result.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        var last = inner[start..].Trim();
        if (last.Length > 0)
        {
            result.Add(last);
        }

        return result;
    }

    private static (string Name, string Type) ParseStructField(string fieldSpec)
    {
        // Field specs look like: "name TYPE" or ""name with spaces" TYPE".
        // Find the first unquoted space that separates the name from the type.
        var inQuotes = false;
        for (var i = 0; i < fieldSpec.Length; i++)
        {
            var ch = fieldSpec[i];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                var name = fieldSpec[..i].Trim().Trim('"');
                var type = fieldSpec[(i + 1)..].Trim();
                return (name, type);
            }
        }

        // No space found — malformed, but return the whole thing as a name.
        return (fieldSpec.Trim().Trim('"'), string.Empty);
    }

    private static string RenderFunc(FuncExpr f, IReadOnlyList<ColumnSchema> schema)
    {
        var a = f.Args.Select(arg => Render(arg, schema)).ToList();
        return f.Op switch
        {
            // arithmetic
            "add" => "(" + string.Join(" + ", a) + ")",
            "subtract" => $"({a[0]} - {a[1]})",
            "multiply" => "(" + string.Join(" * ", a) + ")",
            "divide" => $"({a[0]} / {a[1]})",
            "modulo" => $"({a[0]} % {a[1]})",
            "round" => $"round({string.Join(", ", a)})",
            "abs" => $"abs({a[0]})",
            "floor" => $"floor({a[0]})",
            "ceil" => $"ceil({a[0]})",
            "power" => $"power({a[0]}, {a[1]})",
            "ln" => $"ln({a[0]})",
            // string
            "concat" => $"concat({string.Join(", ", a)})",
            "upper" => $"upper({a[0]})",
            "lower" => $"lower({a[0]})",
            "trim" => $"trim({a[0]})",
            "length" => $"length({a[0]})",
            "substring" => $"substring({string.Join(", ", a)})",
            "replace" => $"replace({a[0]}, {a[1]}, {a[2]})",
            "split_part" => $"split_part({a[0]}, {a[1]}, {a[2]})",
            "lpad" => $"lpad({string.Join(", ", a)})",
            "rpad" => $"rpad({string.Join(", ", a)})",
            "regexp_replace" => $"regexp_replace({a[0]}, {a[1]}, {a[2]})",
            "regexp_extract" => $"regexp_extract({string.Join(", ", a)})",
            "regexp_matches" => $"regexp_matches({a[0]}, {a[1]})",
            // conditional
            "coalesce" => $"coalesce({string.Join(", ", a)})",
            "nullif" => $"nullif({a[0]}, {a[1]})",
            "greatest" => $"greatest({string.Join(", ", a)})",
            "least" => $"least({string.Join(", ", a)})",
            // clip(x, lo, hi) bounds x into [lo, hi] (pandas Series.clip).
            "clip" => $"least(greatest({a[0]}, {a[1]}), {a[2]})",
            // list
            "list_length" => $"length({a[0]})",
            "list_contains" => $"list_contains({a[0]}, {a[1]})",
            "array" => $"[{string.Join(", ", a)}]",
            // temporal
            "date_trunc" => $"date_trunc({SqlText.Literal(ValidateDatePartUnit(f.Args[0]))}, {a[1]})",
            "date_part" => $"date_part({SqlText.Literal(ValidateDatePartUnit(f.Args[0]))}, {a[1]})",
            "date_diff" => $"date_diff({SqlText.Literal(ValidateDatePartUnit(f.Args[0]))}, {a[1]}, {a[2]})",
            "strptime" => $"strptime({a[0]}, {SqlText.Literal(ValidateStrptimeFormat(f.Args[1]))})",
            "date_add" => RenderDateAdd(f, schema),
            // misc
            "hash" => $"hash({a[0]})",
            _ => throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"Unknown expression operator '{f.Op}'."),
        };
    }

    private static string RenderCase(CaseExpr c, IReadOnlyList<ColumnSchema> schema)
    {
        var sb = new System.Text.StringBuilder("CASE");
        foreach (var w in c.Whens)
        {
            sb.Append(" WHEN ")
                .Append(PredicateSqlRenderer.Render(w.Predicate, schema))
                .Append(" THEN ")
                .Append(Render(w.Then, schema));
        }

        if (c.Else is not null)
        {
            sb.Append(" ELSE ").Append(Render(c.Else, schema));
        }

        sb.Append(" END");
        return sb.ToString();
    }

    private static string RenderDateAdd(FuncExpr f, IReadOnlyList<ColumnSchema> schema)
    {
        if (f.Args[0] is not LiteralExpr unitLit || unitLit.Value is not string unitStr)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                "date_add requires its first argument to be a literal unit string.");
        }

        if (!DateAddUnits.Contains(unitStr))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"date_add unit '{unitStr}' is not in the closed vocabulary.");
        }

        var unit = unitStr.ToUpperInvariant();
        var n = Render(f.Args[1], schema);
        var expr = Render(f.Args[2], schema);
        // Multiply an interval by the numeric amount so negative values work without parser ambiguity.
        return $"date_add({expr}, {n} * INTERVAL 1 {unit})";
    }

    private static string ValidateDatePartUnit(ExprNode unitNode)
    {
        if (unitNode is not LiteralExpr lit || lit.Value is not string unit || string.IsNullOrWhiteSpace(unit))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                "date_trunc/date_part/date_diff requires its first argument to be a literal unit string.");
        }

        if (!DatePartUnits.Contains(unit))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"Date/time unit '{unit}' is not in the closed vocabulary.");
        }

        return unit;
    }

    private static string ValidateStrptimeFormat(ExprNode formatNode)
    {
        if (formatNode is not LiteralExpr lit || lit.Value is not string format ||
            string.IsNullOrWhiteSpace(format))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                "strptime requires its second argument to be a literal format string.");
        }

        if (!StrptimeFormats.Contains(format))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidPredicate,
                $"strptime format '{format}' is not in the closed vocabulary.");
        }

        return format;
    }

    private static string ValidateType(string type)
    {
        // Allow only letters/digits/underscore/space/parens/comma (covers DECIMAL(12,2) etc.).
        if (string.IsNullOrWhiteSpace(type) || !TypeRegex().IsMatch(type))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType, $"Invalid cast target type '{type}'.");
        }

        return type.ToUpperInvariant();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_ ,()]{1,64}$")]
    private static partial Regex TypeRegex();
}
