namespace Andy.Data;

/// <summary>
/// Execution statistics returned in every envelope's <c>stats</c> field.
/// <c>Plan</c> is populated only when an operation is called with <c>explain = true</c>.
/// <c>BytesScanned</c> is <c>0</c> for transform operations; for the four file loaders
/// (<c>load_csv</c>, <c>load_parquet</c>, <c>load_json</c>, <c>load_delta</c>) it is an on-disk
/// file-size estimate of the input scanned, not a profiler-measured byte count
/// (see docs/tool-contract.md).
/// </summary>
public sealed record DataFrameStats(
    long ElapsedMs,
    long BytesScanned,
    long RowsProduced,
    string? Plan = null)
{
    public IDictionary<string, object?> ToEnvelope()
    {
        var dict = new Dictionary<string, object?>
        {
            ["elapsed_ms"] = ElapsedMs,
            ["bytes_scanned"] = BytesScanned,
            ["rows_produced"] = RowsProduced,
        };
        if (Plan is not null)
        {
            dict["plan"] = Plan;
        }

        return dict;
    }
}
