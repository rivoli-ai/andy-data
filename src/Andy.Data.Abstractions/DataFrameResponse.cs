namespace Andy.Data;

/// <summary>
/// The common response envelope returned by every dataframe operation. A single shape
/// covers success and failure so a model parses one structure across all operations.
/// See docs/tool-contract.md for the full contract.
/// </summary>
public sealed class DataFrameResponse
{
    public bool Success { get; init; }

    // Success payload ---------------------------------------------------------
    public string? DatasetId { get; init; }
    public IReadOnlyList<ColumnSchema> Schema { get; init; } = Array.Empty<ColumnSchema>();
    public long? RowCount { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> PreviewRows { get; init; }
        = Array.Empty<IReadOnlyDictionary<string, object?>>();
    public bool PreviewTruncated { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public DataFrameStats? Stats { get; init; }

    // Failure payload ---------------------------------------------------------
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public IReadOnlyDictionary<string, object?>? Details { get; init; }

    /// <summary>Builds a successful envelope.</summary>
    public static DataFrameResponse Ok(
        string datasetId,
        IReadOnlyList<ColumnSchema> schema,
        long rowCount,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> previewRows,
        DataFrameStats? stats = null,
        IReadOnlyList<string>? warnings = null) => new()
    {
        Success = true,
        DatasetId = datasetId,
        Schema = schema,
        RowCount = rowCount,
        PreviewRows = previewRows,
        PreviewTruncated = rowCount > previewRows.Count,
        Warnings = warnings ?? Array.Empty<string>(),
        Stats = stats,
    };

    /// <summary>Builds a failure envelope with a stable error code.</summary>
    public static DataFrameResponse Error(
        string errorCode,
        string message,
        IReadOnlyDictionary<string, object?>? details = null) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Message = message,
        Details = details,
    };

    /// <summary>
    /// Serializes to the snake_case dictionary placed in <c>ToolResult.Data</c>.
    /// Mirrors the envelope documented in docs/tool-contract.md exactly.
    /// </summary>
    public IDictionary<string, object?> ToEnvelope()
    {
        if (!Success)
        {
            var err = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["error_code"] = ErrorCode,
                ["message"] = Message,
            };
            if (Details is not null)
            {
                err["details"] = Details;
            }

            return err;
        }

        return new Dictionary<string, object?>
        {
            ["success"] = true,
            ["dataset_id"] = DatasetId,
            ["schema"] = Schema.Select(c => c.ToEnvelope()).ToList(),
            ["row_count"] = RowCount,
            ["preview_rows"] = PreviewRows,
            ["preview_truncated"] = PreviewTruncated,
            ["warnings"] = Warnings,
            ["stats"] = Stats?.ToEnvelope(),
        };
    }
}
