namespace Andy.Data.Operations;

/// <summary>
/// Framework-independent description of a single operation parameter. Mirrors the fields a tool
/// framework needs to build its own parameter schema, but carries no framework dependency. A
/// tool-framework adapter maps this to its own parameter type.
/// </summary>
public sealed class DataFrameParam
{
    /// <summary>Parameter name as it appears in the parameters dictionary.</summary>
    public string Name { get; init; } = "";

    /// <summary>One of: string, integer, number, boolean, array, object.</summary>
    public string Type { get; init; } = "string";

    /// <summary>Whether the parameter must be present and non-null.</summary>
    public bool Required { get; init; }

    /// <summary>Human/model-facing description.</summary>
    public string? Description { get; init; }

    /// <summary>Optional regular expression a string value must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>Optional inclusive minimum for numeric values.</summary>
    public object? MinValue { get; init; }

    /// <summary>Optional inclusive maximum for numeric values.</summary>
    public object? MaxValue { get; init; }

    /// <summary>Optional closed set of allowed values (compared by string form).</summary>
    public IReadOnlyList<object>? AllowedValues { get; init; }

    /// <summary>Optional default value (informational; not applied by the validator).</summary>
    public object? DefaultValue { get; init; }
}

/// <summary>
/// Framework-independent metadata for a dataframe operation: its id, display name, description, and
/// declared parameter schema. A tool-framework adapter derives its own tool metadata from this.
/// </summary>
public sealed class OperationMetadata
{
    /// <summary>Stable operation id, e.g. <c>dataframe_filter</c>.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-readable name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Model-facing description of what the operation does and its parameters.</summary>
    public string Description { get; init; } = "";

    /// <summary>Declared parameter schema, validated before the operation body runs.</summary>
    public IReadOnlyList<DataFrameParam> Parameters { get; init; } = Array.Empty<DataFrameParam>();
}

/// <summary>
/// Per-call execution options: resource governance and cancellation. Framework-independent — a host
/// or tool-framework adapter populates this from its own execution context. A memory limit of
/// <c>null</c> or non-positive means "unset" (the engine default applies); a positive
/// <see cref="MaxExecutionTimeMs"/> cancels the operation after that many milliseconds.
/// </summary>
public sealed class DataFrameExecuteOptions
{
    /// <summary>Engine memory cap (DuckDB <c>memory_limit</c>) in bytes; null/non-positive = unset.</summary>
    public long? MaxMemoryBytes { get; init; }

    /// <summary>Wall-clock execution limit in milliseconds; null/non-positive = no limit.</summary>
    public int? MaxExecutionTimeMs { get; init; }

    /// <summary>Caller cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Options with no resource limits and no cancellation.</summary>
    public static readonly DataFrameExecuteOptions Default = new();
}
