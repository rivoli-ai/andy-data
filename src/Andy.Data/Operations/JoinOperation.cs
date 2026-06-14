using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;
using Andy.Data.Sql;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_join</c> — joins two datasets on one or more keys.
/// See docs/operations.md#dataframe_join.
/// </summary>
public sealed class JoinOperation : DataFrameOperationBase
{
    private static readonly Dictionary<string, string> JoinTypes = new(StringComparer.Ordinal)
    {
        ["inner"] = "INNER", ["left"] = "LEFT", ["right"] = "RIGHT",
        ["full"] = "FULL", ["semi"] = "SEMI", ["anti"] = "ANTI",
        ["cross"] = "CROSS", ["asof"] = "ASOF",
    };

    private static readonly HashSet<string> AsofOps = new(StringComparer.Ordinal) { ">=", "<=" };

    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public JoinOperation() : this(null!, null!, null) { }

    public JoinOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<JoinOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_join",
        Name = "DataFrame Join",
        Description =
            "Joins two datasets ('left' and 'right') into 'into'. 'how' is inner|left|right|full|semi|anti|cross|asof " +
            "(default inner). Provide 'on' (key columns present in both) or 'left_on'/'right_on' (equal-length). " +
            "For 'asof', the last key is the inequality (as-of) column and any preceding keys are equality matches; " +
            "'asof_op' is >= (default) or <=. Overlapping non-key columns from the right get 'suffix' (default _right). " +
            "Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "left", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Left dataset id." },
            new DataFrameParam { Name = "right", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Right dataset id." },
            new DataFrameParam { Name = "into", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Output dataset id." },
            new DataFrameParam { Name = "how", Type = "string", Required = false, DefaultValue = "inner",
                AllowedValues = new object[] { "inner", "left", "right", "full", "semi", "anti", "cross", "asof" },
                Description = "inner | left | right | full | semi | anti | cross | asof." },
            new DataFrameParam { Name = "on", Type = "array", Required = false,
                Description = "Key column names present in both sides." },
            new DataFrameParam { Name = "left_on", Type = "array", Required = false,
                Description = "Left key columns (with right_on, equal length)." },
            new DataFrameParam { Name = "right_on", Type = "array", Required = false,
                Description = "Right key columns (with left_on, equal length)." },
            new DataFrameParam { Name = "asof_op", Type = "string", Required = false, DefaultValue = ">=",
                AllowedValues = new object[] { ">=", "<=" },
                Description = "Inequality operator for the as-of column when how=asof." },
            new DataFrameParam { Name = "suffix", Type = "string", Required = false, DefaultValue = "_right",
                Description = "Suffix for overlapping non-key right columns." },
            new DataFrameParam { Name = "explain", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Include the DuckDB query plan in stats.plan." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var leftId = GetParameter<string>(parameters, "left");
        var rightId = GetParameter<string>(parameters, "right");
        var intoId = GetParameter<string>(parameters, "into");
        var how = (GetStringOrNull(parameters, "how") ?? "inner").ToLowerInvariant();
        var suffix = GetStringOrNull(parameters, "suffix") ?? "_right";
        var asofOp = GetStringOrNull(parameters, "asof_op") ?? ">=";

        return Guard(parameters, options, _backend, ct =>
        {
            if (!JoinTypes.TryGetValue(how, out var joinType))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    $"Unknown join type '{how}'. Use inner, left, right, full, semi, anti, cross, or asof.");
            }

            if (how == "asof" && !AsofOps.Contains(asofOp))
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'asof_op' must be >= or <=.");
            }

            var left = RequireDataset(_catalog, leftId);
            var right = RequireDataset(_catalog, rightId);

            var on = ToStringList("on", parameters.GetValueOrDefault("on"));
            List<string> leftKeys, rightKeys;
            if (on.Count > 0)
            {
                leftKeys = on;
                rightKeys = on;
            }
            else
            {
                leftKeys = ToStringList("left_on", parameters.GetValueOrDefault("left_on"));
                rightKeys = ToStringList("right_on", parameters.GetValueOrDefault("right_on"));
            }

            if (how == "cross")
            {
                leftKeys = new List<string>();
                rightKeys = new List<string>();
            }
            else if (leftKeys.Count == 0 || leftKeys.Count != rightKeys.Count)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "Join requires 'on', or equal-length 'left_on' and 'right_on'.");
            }

            if (how == "asof" && leftKeys.Count == 0)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "Asof join requires at least one key column (the last key is the as-of column).");
            }

            var leftCanon = leftKeys.Select(k => SqlText.ResolveColumn(k, left.Schema)).ToList();
            var rightCanon = rightKeys.Select(k => SqlText.ResolveColumn(k, right.Schema)).ToList();

            string? onSql;
            if (how == "cross")
            {
                onSql = null;
            }
            else if (how == "asof")
            {
                var equality = leftCanon.Count > 1
                    ? string.Join(" AND ", leftCanon.Take(leftCanon.Count - 1).Select((lk, i) =>
                        $"l.{SqlText.QuoteIdent(lk)} = r.{SqlText.QuoteIdent(rightCanon[i])}"))
                    : null;
                var last = leftCanon.Count - 1;
                var inequality =
                    $"l.{SqlText.QuoteIdent(leftCanon[last])} {asofOp} r.{SqlText.QuoteIdent(rightCanon[last])}";
                onSql = equality is not null ? $"{equality} AND {inequality}" : inequality;
            }
            else
            {
                onSql = string.Join(" AND ", leftCanon.Select((lk, i) =>
                    $"l.{SqlText.QuoteIdent(lk)} = r.{SqlText.QuoteIdent(rightCanon[i])}"));
            }

            string selectClause;
            if (how is "semi" or "anti")
            {
                selectClause = "l.*";
            }
            else
            {
                var leftNames = left.Schema.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rightKeySet = rightCanon.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var rightExtra = right.Schema
                    .Where(c => !rightKeySet.Contains(c.Name))
                    .Select(c =>
                    {
                        var alias = leftNames.Contains(c.Name) ? c.Name + suffix : c.Name;
                        return $"r.{SqlText.QuoteIdent(c.Name)} AS {SqlText.QuoteIdent(alias)}";
                    })
                    .ToList();

                string leftClause;
                if (how is "right" or "full")
                {
                    // Right-only rows have NULL left columns, so taking keys from l.* alone would
                    // lose the very keys that identify them — coalesce each key with its right-side
                    // counterpart (the convention pandas/polars use for outer joins).
                    var keyIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < leftCanon.Count; i++)
                    {
                        keyIndex[leftCanon[i]] = i;
                    }

                    leftClause = string.Join(", ", left.Schema.Select(c =>
                        keyIndex.TryGetValue(c.Name, out var i)
                            ? $"COALESCE(l.{SqlText.QuoteIdent(c.Name)}, r.{SqlText.QuoteIdent(rightCanon[i])}) AS {SqlText.QuoteIdent(c.Name)}"
                            : $"l.{SqlText.QuoteIdent(c.Name)}"));
                }
                else
                {
                    leftClause = "l.*";
                }

                selectClause = rightExtra.Count > 0 ? leftClause + ", " + string.Join(", ", rightExtra) : leftClause;
            }

            var sw = Stopwatch.StartNew();
            var plan = GetBoolOrNull(parameters, "explain") ?? false
                ? _backend.ExplainJoin(leftId, rightId, joinType, onSql, selectClause, ct)
                : null;
            var schema = _backend.Join(intoId, leftId, rightId, joinType, onSql, selectClause, ct);
            return Finish(_backend, _catalog, intoId, schema, $"join:{leftId}+{rightId}", sw, plan: plan, ct: ct);
        });
    }
}
