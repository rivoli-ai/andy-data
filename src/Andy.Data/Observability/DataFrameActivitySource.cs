using System.Diagnostics;

namespace Andy.Data.Observability;

/// <summary>
/// Shared <see cref="ActivitySource"/> for the Andy.Data library. Activities are
/// produced only when a listener is subscribed; the library remains fully functional when no
/// OpenTelemetry/logging provider is configured.
/// </summary>
public static class DataFrameActivitySource
{
    /// <summary>
    /// The activity source name used for all dataframe traces.
    /// </summary>
    public const string Name = "Andy.Data";

    /// <summary>
    /// The shared activity source instance.
    /// </summary>
    public static ActivitySource Instance { get; } = new(Name);

    /// <summary>
    /// Activity name used around the execution of a dataframe tool.
    /// </summary>
    public const string ToolExecute = "dataframe.tool.execute";

    /// <summary>
    /// Activity name used around a single DuckDB SQL statement executed by the backend.
    /// </summary>
    public const string SqlExecute = "dataframe.sql.execute";
}
