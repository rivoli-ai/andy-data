using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data.Observability;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// Base class for every framework-independent dataframe operation. Centralizes parameter access,
/// schema validation, resource governance, and exception → <see cref="DataFrameResponse"/> mapping so
/// the documented envelope is produced identically by all operations and no exception escapes
/// <see cref="Execute"/>. A tool-framework adapter wraps these operations; the engine itself has no
/// dependency on any tool framework.
/// </summary>
public abstract class DataFrameOperationBase
{
    /// <summary>Default number of rows returned in a preview.</summary>
    protected const int PreviewLimit = 50;

    /// <summary>Pattern dataset ids must match.</summary>
    protected const string DatasetIdPattern = "^[A-Za-z_][A-Za-z0-9_]{0,127}$";

    /// <summary>Metadata (id, name, description, parameter schema) for this operation.</summary>
    public abstract OperationMetadata Metadata { get; }

    /// <summary>
    /// Validates <paramref name="parameters"/> against <see cref="Metadata"/>, applies the
    /// <paramref name="options"/> resource governance, runs the operation, and returns the response
    /// envelope. Never throws across the boundary.
    /// </summary>
    public abstract DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options);

    /// <summary>Optional logger; assigned via <see cref="UseLogger"/> in derived constructors.</summary>
    protected ILogger? Logger { get; private set; }

    /// <summary>Assigns the logger for this operation instance.</summary>
    protected void UseLogger(ILogger? logger) => Logger = logger;

    /// <summary>
    /// Validates parameters against the schema, then runs <paramref name="body"/> and maps the
    /// outcome to a <see cref="DataFrameResponse"/>. Schema violations and
    /// <see cref="DataFrameException"/> become their documented error envelopes; cancellation becomes
    /// <c>CANCELLED</c>; any other exception becomes <c>BACKEND_ERROR</c>.
    /// </summary>
    protected DataFrameResponse Guard(
        IReadOnlyDictionary<string, object?> parameters, Func<DataFrameResponse> body)
    {
        var opId = Metadata.Id;
        Logger?.LogInformation("Executing dataframe operation {OperationId}.", opId);

        using var activity = DataFrameActivitySource.Instance.StartActivity(DataFrameActivitySource.ToolExecute);
        activity?.SetTag("dataframe.operation.id", opId);

        try
        {
            DataFrameParameterValidator.Validate(parameters, Metadata.Parameters);
            var result = body();
            Logger?.LogInformation("Dataframe operation {OperationId} completed successfully.", opId);
            return result;
        }
        catch (DataFrameException ex)
        {
            Logger?.LogWarning(ex, "Dataframe operation {OperationId} failed with error code {ErrorCode}.", opId, ex.ErrorCode);
            activity?.SetTag("dataframe.error_code", ex.ErrorCode);
            return ex.ToResponse();
        }
        catch (OperationCanceledException)
        {
            Logger?.LogWarning("Dataframe operation {OperationId} was cancelled.", opId);
            activity?.SetTag("dataframe.error_code", DataFrameErrorCodes.Cancelled);
            return DataFrameResponse.Error(DataFrameErrorCodes.Cancelled,
                "The operation was cancelled (caller cancellation or MaxExecutionTimeMs exceeded).");
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Dataframe operation {OperationId} failed with an unexpected backend error.", opId);
            activity?.SetTag("dataframe.error_code", DataFrameErrorCodes.BackendError);
            return DataFrameResponse.Error(DataFrameErrorCodes.BackendError, ex.Message);
        }
    }

    /// <summary>
    /// <see cref="Guard(IReadOnlyDictionary{string, object?}, Func{DataFrameResponse})"/> plus resource
    /// governance: applies the memory limit to the backend, derives the effective cancellation token
    /// (caller token linked with <c>MaxExecutionTimeMs</c>), and hands it to <paramref name="body"/>.
    /// </summary>
    protected DataFrameResponse Guard(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options,
        IDuckDbBackend backend, Func<CancellationToken, DataFrameResponse> body)
    {
        return Guard(parameters, () =>
        {
            if (options.MaxMemoryBytes is > 0)
            {
                backend.ApplyResourceLimits(options.MaxMemoryBytes.Value);
            }

            if (options.MaxExecutionTimeMs is > 0)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
                timeout.CancelAfter(options.MaxExecutionTimeMs.Value);
                return body(timeout.Token);
            }

            return body(options.CancellationToken);
        });
    }

    /// <summary>Reads a required parameter and converts it to <typeparamref name="T"/>.</summary>
    protected static T GetParameter<T>(IReadOnlyDictionary<string, object?> p, string name)
    {
        if (!p.TryGetValue(name, out var v) || v is null)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                $"Required parameter '{name}' is missing.",
                new Dictionary<string, object?> { ["parameter"] = name });
        }

        if (v is T typed)
        {
            return typed;
        }

        try
        {
            return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Parameter '{name}' has the wrong type.",
                new Dictionary<string, object?> { ["parameter"] = name });
        }
    }

    /// <summary>Resolves the output dataset id: the 'into' parameter, or the input id if omitted.</summary>
    protected static string ResolveInto(IReadOnlyDictionary<string, object?> p, string fromId) =>
        GetStringOrNull(p, "into") ?? fromId;

    /// <summary>Looks up a dataset entry or throws DATASET_NOT_FOUND.</summary>
    protected static DatasetEntry RequireDataset(IDatasetCatalog catalog, string datasetId) =>
        catalog.Get(datasetId)
        ?? throw new DataFrameException(DataFrameErrorCodes.DatasetNotFound,
            $"Dataset '{datasetId}' is not registered.",
            new Dictionary<string, object?> { ["dataset_id"] = datasetId });

    /// <summary>
    /// Runs a SELECT-based transform: optionally captures the EXPLAIN plan, materializes the derived
    /// dataset, registers it, and builds the success envelope (with the plan in <c>stats.plan</c>).
    /// </summary>
    protected static DataFrameResponse Materialize(
        IDuckDbBackend backend, IDatasetCatalog catalog, string intoId, string fromId,
        string selectClause, string source, Stopwatch sw, bool explain,
        string? whereClause = null, string? groupByClause = null,
        string? orderByClause = null, int? limit = null,
        string? havingClause = null, IReadOnlyList<string>? warnings = null,
        CancellationToken ct = default)
    {
        return backend.RunExclusive(() =>
        {
            var plan = explain
                ? backend.Explain(fromId, selectClause, whereClause, groupByClause, orderByClause, limit, havingClause, ct)
                : null;
            var schema = backend.Derive(intoId, fromId, selectClause, whereClause, groupByClause, orderByClause, limit, havingClause, ct);
            var rowCount = backend.CountRows(intoId, ct);
            catalog.Register(new DatasetEntry(intoId, schema, source, rowCount));
            var preview = backend.Preview(intoId, "head", PreviewLimit, null, rowCount, ct);
            sw.Stop();
            return DataFrameResponse.Ok(intoId, schema, rowCount, preview,
                new DataFrameStats(sw.ElapsedMilliseconds, 0, rowCount, plan), warnings);
        });
    }

    /// <summary>
    /// Registers a freshly-derived dataset, collects a bounded preview, and builds the success
    /// envelope (with <paramref name="plan"/> in <c>stats.plan</c> when captured).
    /// </summary>
    protected static DataFrameResponse Finish(
        IDuckDbBackend backend, IDatasetCatalog catalog, string intoId,
        IReadOnlyList<ColumnSchema> schema, string source, Stopwatch sw,
        IReadOnlyList<string>? warnings = null, string? plan = null, CancellationToken ct = default,
        long bytesScanned = 0)
    {
        return backend.RunExclusive(() =>
        {
            var rowCount = backend.CountRows(intoId, ct);
            catalog.Register(new DatasetEntry(intoId, schema, source, rowCount));
            var preview = backend.Preview(intoId, "head", PreviewLimit, null, rowCount, ct);
            sw.Stop();
            return DataFrameResponse.Ok(intoId, schema, rowCount, preview,
                new DataFrameStats(sw.ElapsedMilliseconds, bytesScanned, rowCount, plan), warnings);
        });
    }

    /// <summary>Best-effort estimate of the bytes that will be read from a file path or glob.</summary>
    protected static long EstimateFileBytes(string path)
    {
        try
        {
            if (path.Contains('*') || path.Contains('?'))
            {
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) dir = ".";
                var pattern = Path.GetFileName(path);
                return Directory.Exists(dir)
                    ? Directory.GetFiles(dir, pattern).Sum(f => new FileInfo(f).Length)
                    : 0;
            }

            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    protected static string? GetStringOrNull(IReadOnlyDictionary<string, object?> p, string name) =>
        p.TryGetValue(name, out var v) && v is not null ? v.ToString() : null;

    protected static bool? GetBoolOrNull(IReadOnlyDictionary<string, object?> p, string name) =>
        p.TryGetValue(name, out var v) && v is not null ? Parse(name, v, "a boolean", x => Convert.ToBoolean(x, CultureInfo.InvariantCulture)) : null;

    protected static int? GetIntOrNull(IReadOnlyDictionary<string, object?> p, string name) =>
        p.TryGetValue(name, out var v) && v is not null ? Parse(name, v, "an integer", x => Convert.ToInt32(x, CultureInfo.InvariantCulture)) : null;

    protected static long? GetLongOrNull(IReadOnlyDictionary<string, object?> p, string name) =>
        p.TryGetValue(name, out var v) && v is not null ? Parse(name, v, "an integer", x => Convert.ToInt64(x, CultureInfo.InvariantCulture)) : null;

    private static T Parse<T>(string name, object value, string expected, Func<object, T> convert)
    {
        try
        {
            return convert(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Parameter '{name}' must be {expected}; got '{value}'.",
                new Dictionary<string, object?> { ["parameter"] = name });
        }
    }

    /// <summary>Reads a parameter as a list of strings; empty when absent, error when a scalar/dict.</summary>
    protected static List<string> ToStringList(string name, object? value)
    {
        if (value is null)
        {
            return new List<string>();
        }

        if (value is string)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Parameter '{name}' must be a list of strings; got a scalar string value.",
                new Dictionary<string, object?> { ["parameter"] = name });
        }

        if (value is System.Collections.IDictionary)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Parameter '{name}' must be a list of strings; got a dictionary.",
                new Dictionary<string, object?> { ["parameter"] = name });
        }

        if (value is not System.Collections.IEnumerable e)
        {
            return new List<string>();
        }

        return e.Cast<object?>().Where(o => o is not null).Select(o => o!.ToString()!).ToList();
    }

    /// <summary>
    /// Enforces a registered <see cref="IPathPolicy"/> for read operations. No-op when no policy is
    /// configured. The path is canonicalized (<c>..</c> resolved, symlinks resolved over the existing
    /// prefix) before the policy sees it, preventing traversal/symlink bypass of a prefix policy.
    /// </summary>
    protected static void EnforceReadPolicy(IPathPolicy? policy, string path)
    {
        if (policy is null)
        {
            return;
        }

        var canonical = CanonicalizePath(path);
        if (!policy.CanRead(canonical))
        {
            throw new DataFrameException(DataFrameErrorCodes.PermissionDenied,
                $"Read access to path '{canonical}' is denied by the configured path policy.",
                new Dictionary<string, object?> { ["path"] = canonical });
        }
    }

    /// <summary>Enforces a registered <see cref="IPathPolicy"/> for write operations.</summary>
    protected static void EnforceWritePolicy(IPathPolicy? policy, string path)
    {
        if (policy is null)
        {
            return;
        }

        var canonical = CanonicalizePath(path);
        if (!policy.CanWrite(canonical))
        {
            throw new DataFrameException(DataFrameErrorCodes.PermissionDenied,
                $"Write access to path '{canonical}' is denied by the configured path policy.",
                new Dictionary<string, object?> { ["path"] = canonical });
        }
    }

    /// <summary>
    /// Normalizes a (possibly-glob) path: resolves <c>..</c> and separators, and resolves symbolic
    /// links over the existing prefix, so an <see cref="IPathPolicy"/> sees the real target. For glob
    /// paths the concrete base before the first wildcard is resolved and recombined with the tail.
    /// </summary>
    internal static string CanonicalizePath(string path)
    {
        try
        {
            var wildcard = path.IndexOfAny(new[] { '*', '?', '[' });
            if (wildcard < 0)
            {
                return ResolveExistingSymlinks(Path.GetFullPath(path));
            }

            var lastSep = path.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, wildcard);
            if (lastSep < 0)
            {
                return path;
            }

            var basePart = path[..lastSep];
            var tail = path[(lastSep + 1)..];
            var canonicalBase = ResolveExistingSymlinks(
                string.IsNullOrEmpty(basePart) ? Path.GetFullPath(".") : Path.GetFullPath(basePart));
            return string.Concat(canonicalBase, Path.DirectorySeparatorChar.ToString(), tail);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string ResolveExistingSymlinks(string fullPath)
    {
        try
        {
            return Path.GetFullPath(RealPath(fullPath, depth: 0));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException)
        {
            return fullPath;
        }
    }

    private static string RealPath(string path, int depth)
    {
        if (depth > 64)
        {
            return path;
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            return path;
        }

        var realParent = RealPath(parent, depth + 1);
        var candidate = string.Equals(realParent, parent, StringComparison.Ordinal)
            ? path
            : Path.Combine(realParent, Path.GetFileName(path));

        FileSystemInfo? info =
            Directory.Exists(candidate) ? new DirectoryInfo(candidate)
            : File.Exists(candidate) ? new FileInfo(candidate)
            : null;

        if (info is null)
        {
            return candidate;
        }

        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
    }
}
