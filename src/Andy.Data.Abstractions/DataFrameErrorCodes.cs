namespace Andy.Data;

/// <summary>
/// Stable error codes shared by every dataframe operation. These are a contract
/// (see docs/tool-contract.md); values are safe for programmatic branching by a model.
/// </summary>
public static class DataFrameErrorCodes
{
    public const string DatasetNotFound = "DATASET_NOT_FOUND";
    public const string ColumnNotFound = "COLUMN_NOT_FOUND";
    public const string InvalidType = "INVALID_TYPE";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string InvalidAggregation = "INVALID_AGGREGATION";
    public const string InvalidPredicate = "INVALID_PREDICATE";
    public const string SchemaMismatch = "SCHEMA_MISMATCH";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string TargetExists = "TARGET_EXISTS";
    public const string Cancelled = "CANCELLED";
    public const string BackendError = "BACKEND_ERROR";
}
