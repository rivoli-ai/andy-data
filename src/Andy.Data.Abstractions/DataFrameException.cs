namespace Andy.Data;

/// <summary>
/// Thrown by the parsing/validation layer with a stable error code from
/// <see cref="DataFrameErrorCodes"/>. Operations catch this at the tool boundary and turn it into
/// the failure envelope via <see cref="ToResponse"/>, so no exception crosses the tool boundary.
/// </summary>
public sealed class DataFrameException : Exception
{
    public string ErrorCode { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }

    public DataFrameException(string errorCode, string message,
        IReadOnlyDictionary<string, object?>? details = null) : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
    }

    public DataFrameException(string errorCode, string message,
        IReadOnlyDictionary<string, object?>? details, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Details = details;
    }

    /// <summary>Converts this exception into the standard failure envelope.</summary>
    public DataFrameResponse ToResponse() => DataFrameResponse.Error(ErrorCode, Message, Details);
}
