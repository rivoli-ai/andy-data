using System.Globalization;
using System.Text.Json;
using Andy.Data;

namespace Andy.Data.Sql;

/// <summary>
/// Injection-safe SQL fragment helpers: identifier quoting, literal rendering (culture-invariant,
/// round-trippable doubles), and column resolution against a dataset schema.
/// </summary>
internal static class SqlText
{
    public static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>Renders a value as a safe DuckDB literal. Strings are escaped + single-quoted.</summary>
    public static string Literal(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        string s => Quote(s),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong
            => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        JsonElement je => FromJson(je),
        _ => Quote(value.ToString() ?? string.Empty),
    };

    /// <summary>Resolves a user column name against the schema (case-insensitive) and quotes it.</summary>
    public static string ResolveColumnQuoted(string name, IReadOnlyList<ColumnSchema> schema) =>
        QuoteIdent(ResolveColumn(name, schema));

    /// <summary>Resolves a user column name to its canonical schema name, or throws COLUMN_NOT_FOUND.</summary>
    public static string ResolveColumn(string name, IReadOnlyList<ColumnSchema> schema)
    {
        var match = schema.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.Name;
        }

        var details = new Dictionary<string, object?> { ["column"] = name };
        var suggestion = schema
            .Select(c => c.Name)
            .OrderBy(n => Levenshtein(n.ToLowerInvariant(), name.ToLowerInvariant()))
            .FirstOrDefault();
        if (suggestion is not null)
        {
            details["did_you_mean"] = suggestion;
        }

        throw new DataFrameException(DataFrameErrorCodes.ColumnNotFound,
            $"Column '{name}' does not exist in the dataset.", details);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string FromJson(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Number => je.GetRawText(),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.Null => "NULL",
        JsonValueKind.String => Quote(je.GetString() ?? string.Empty),
        _ => Quote(je.GetRawText()),
    };

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
