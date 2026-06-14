using System.Diagnostics;
using System.Globalization;
using Andy.Data.Backend;
using Andy.Data;
using Andy.Data.Sql;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_assert</c> — evaluates structured data-quality expectations against a dataset and
/// returns a per-expectation pass/fail report. A first-class equivalent of pandera / Great
/// Expectations checks an LLM pipeline can branch on. See docs/operations.md#dataframe_assert.
/// </summary>
public sealed class AssertOperation : DataFrameOperationBase
{
    private static readonly IReadOnlyList<ColumnSchema> ReportSchema = new[]
    {
        new ColumnSchema("expectation", "VARCHAR", false),
        new ColumnSchema("column", "VARCHAR", true),
        new ColumnSchema("passed", "BOOLEAN", false),
        new ColumnSchema("violations", "BIGINT", false),
        new ColumnSchema("details", "VARCHAR", true),
    };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public AssertOperation() : this(null!, null!, null) { }

    public AssertOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<AssertOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_assert",
        Name = "DataFrame Assert",
        Description =
            "Evaluates data-quality expectations against a dataset and returns a per-expectation " +
            "pass/fail report (it does not modify or register a dataset). 'expectations' is an array of " +
            "{ type, ... } objects; type is one of: not_null (column), unique (column), in_range " +
            "(column, min?, max?), in_set (column, values[]), matches (column, pattern), row_count " +
            "(min?, max?, equals?). Returns the standard envelope where preview_rows holds one row per " +
            "expectation (expectation, column, passed, violations, details); a warning summarizes any " +
            "failures so a caller can branch on data quality.",
        Parameters =
        [
            new DataFrameParam { Name = "dataset_id", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Dataset to check." },
            new DataFrameParam { Name = "expectations", Type = "array", Required = true,
                Description = "Array of { type, column?, min?, max?, equals?, values?, pattern? } expectation specs." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var datasetId = GetParameter<string>(parameters, "dataset_id");

        return Guard(parameters, options, _backend, ct =>
        {
            var entry = RequireDataset(_catalog, datasetId);
            var specs = ToObjectList(parameters.GetValueOrDefault("expectations"));
            if (specs.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'expectations' must contain at least one { type, ... } expectation spec.");
            }

            var sw = Stopwatch.StartNew();
            var results = _backend.RunExclusive(() =>
            {
                var rows = new List<IReadOnlyDictionary<string, object?>>(specs.Count);
                foreach (var spec in specs)
                {
                    if (spec is not IReadOnlyDictionary<string, object?> e)
                    {
                        throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                            "Each expectation must be a { type, ... } object.");
                    }

                    rows.Add(Evaluate(datasetId, entry.Schema, e, ct));
                }

                return rows;
            });
            sw.Stop();

            var failed = results.Count(r => r["passed"] is false);
            var warnings = failed > 0
                ? new[] { $"Assertion failed: {failed} of {results.Count} expectations did not hold (see preview_rows)." }
                : Array.Empty<string>();

            return DataFrameResponse.Ok(datasetId, ReportSchema, results.Count, results,
                new DataFrameStats(sw.ElapsedMilliseconds, 0, results.Count), warnings);
        });
    }

    private IReadOnlyDictionary<string, object?> Evaluate(
        string datasetId, IReadOnlyList<ColumnSchema> schema, IReadOnlyDictionary<string, object?> e,
        CancellationToken ct)
    {
        var type = (e.TryGetValue("type", out var t) ? t?.ToString() : null)?.ToLowerInvariant();

        switch (type)
        {
            case "not_null":
            {
                var (name, colQ) = ResolveCol(e, schema);
                var v = ViolationCount(datasetId, $"{colQ} IS NULL", ct);
                return Row("not_null", name, v, v == 0 ? "no NULLs" : $"{v} NULL value(s)");
            }

            case "unique":
            {
                var (name, colQ) = ResolveCol(e, schema);
                var dupGroups = DuplicateGroupCount(datasetId, colQ, ct);
                return Row("unique", name, dupGroups,
                    dupGroups == 0 ? "all values unique" : $"{dupGroups} value(s) occur more than once");
            }

            case "in_range":
            {
                var (name, colQ) = ResolveCol(e, schema);
                var hasMin = e.TryGetValue("min", out var min) && min is not null;
                var hasMax = e.TryGetValue("max", out var max) && max is not null;
                if (!hasMin && !hasMax)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'in_range' requires at least one of 'min' or 'max'.");
                }

                var conds = new List<string>();
                if (hasMin) conds.Add($"{colQ} < {SqlText.Literal(min)}");
                if (hasMax) conds.Add($"{colQ} > {SqlText.Literal(max)}");
                var where = $"{colQ} IS NOT NULL AND ({string.Join(" OR ", conds)})";
                var v = ViolationCount(datasetId, where, ct);
                var bounds = $"[{(hasMin ? min : "-inf")}, {(hasMax ? max : "+inf")}]";
                return Row("in_range", name, v, v == 0 ? $"all within {bounds}" : $"{v} value(s) outside {bounds}");
            }

            case "in_set":
            {
                var (name, colQ) = ResolveCol(e, schema);
                var values = ToObjectList(e.GetValueOrDefault("values"));
                if (values.Count == 0)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'in_set' requires a non-empty 'values' array.");
                }

                var literals = string.Join(", ", values.Select(SqlText.Literal));
                var v = ViolationCount(datasetId, $"{colQ} IS NOT NULL AND {colQ} NOT IN ({literals})", ct);
                return Row("in_set", name, v, v == 0 ? "all values in set" : $"{v} value(s) outside the set");
            }

            case "matches":
            {
                var (name, colQ) = ResolveCol(e, schema);
                var pattern = e.TryGetValue("pattern", out var p) ? p?.ToString() : null;
                if (string.IsNullOrEmpty(pattern))
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'matches' requires a non-empty 'pattern'.");
                }

                var v = ViolationCount(datasetId,
                    $"{colQ} IS NOT NULL AND NOT regexp_matches({colQ}, {SqlText.Literal(pattern)})", ct);
                return Row("matches", name, v, v == 0 ? "all values match" : $"{v} value(s) do not match");
            }

            case "row_count":
            {
                var n = _backend.CountRows(datasetId, ct);
                var passed = true;
                var parts = new List<string>();
                if (e.TryGetValue("equals", out var eq) && eq is not null)
                {
                    var target = AsLong(eq, "equals");
                    passed &= n == target;
                    parts.Add($"equals {target}");
                }

                if (e.TryGetValue("min", out var mn) && mn is not null)
                {
                    var target = AsLong(mn, "min");
                    passed &= n >= target;
                    parts.Add($"min {target}");
                }

                if (e.TryGetValue("max", out var mx) && mx is not null)
                {
                    var target = AsLong(mx, "max");
                    passed &= n <= target;
                    parts.Add($"max {target}");
                }

                if (parts.Count == 0)
                {
                    throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                        "'row_count' requires at least one of 'equals', 'min', or 'max'.");
                }

                return new Dictionary<string, object?>
                {
                    ["expectation"] = "row_count",
                    ["column"] = null,
                    ["passed"] = passed,
                    ["violations"] = passed ? 0L : 1L,
                    ["details"] = $"row_count={n}, expected {string.Join(", ", parts)}",
                };
            }

            default:
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Unknown expectation type '{type}'. Use not_null, unique, in_range, in_set, matches, or row_count.");
        }
    }

    private static long AsLong(object value, string field)
    {
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                $"'row_count' expectation '{field}' must be an integer.");
        }
    }

    private (string Name, string Quoted) ResolveCol(IReadOnlyDictionary<string, object?> e, IReadOnlyList<ColumnSchema> schema)
    {
        var col = e.TryGetValue("column", out var c) ? c?.ToString() : null;
        if (string.IsNullOrWhiteSpace(col))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                "This expectation requires a 'column'.");
        }

        var name = SqlText.ResolveColumn(col, schema); // validates existence (COLUMN_NOT_FOUND)
        return (name, SqlText.QuoteIdent(name));
    }

    /// <summary>Counts rows matching a violation predicate via a scratch relation that is dropped after.</summary>
    private long ViolationCount(string datasetId, string whereClause, CancellationToken ct)
    {
        var tmp = "_assert_" + Guid.NewGuid().ToString("N");
        try
        {
            _backend.Derive(tmp, datasetId, "count(*) AS violations", whereClause, ct: ct);
            var rows = _backend.Preview(tmp, "head", 1, null, 1, ct);
            return rows.Count == 0 ? 0L : Convert.ToInt64(rows[0]["violations"], CultureInfo.InvariantCulture);
        }
        finally
        {
            // Best-effort: the scratch relation may not have been created if Derive threw. Drop on an
            // unknown id is a no-op, so this both cleans up the success path and the partial-failure path.
            TryDrop(tmp);
        }
    }

    /// <summary>Counts the distinct values that occur more than once in a column.</summary>
    private long DuplicateGroupCount(string datasetId, string colQ, CancellationToken ct)
    {
        var tmp = "_assert_" + Guid.NewGuid().ToString("N");
        try
        {
            _backend.Derive(tmp, datasetId, colQ, groupByClause: colQ, havingClause: "count(*) > 1", ct: ct);
            return _backend.CountRows(tmp, ct);
        }
        finally
        {
            TryDrop(tmp);
        }
    }

    private void TryDrop(string id)
    {
        try { _backend.Drop(id); } catch { /* scratch cleanup is best-effort */ }
    }

    private static IReadOnlyDictionary<string, object?> Row(string type, string? column, long violations, string details) =>
        new Dictionary<string, object?>
        {
            ["expectation"] = type,
            ["column"] = column,
            ["passed"] = violations == 0,
            ["violations"] = violations,
            ["details"] = details,
        };

    private static List<object?> ToObjectList(object? value)
    {
        if (value is null)
        {
            return new List<object?>();
        }

        if (value is string || value is not System.Collections.IEnumerable e)
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "Expected an array.");
        }

        return e.Cast<object?>().ToList();
    }
}
