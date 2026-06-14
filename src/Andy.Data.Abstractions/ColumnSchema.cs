namespace Andy.Data;

/// <summary>
/// One column in a dataset schema, as surfaced in the response envelope.
/// <c>Type</c> is the verbatim DuckDB type (e.g. <c>VARCHAR</c>, <c>DECIMAL(12,2)</c>).
/// </summary>
public sealed record ColumnSchema(string Name, string Type, bool Nullable = true)
{
    /// <summary>Maps to the snake_case shape documented in docs/tool-contract.md.</summary>
    public IDictionary<string, object?> ToEnvelope() => new Dictionary<string, object?>
    {
        ["name"] = Name,
        ["type"] = Type,
        ["nullable"] = Nullable,
    };
}
