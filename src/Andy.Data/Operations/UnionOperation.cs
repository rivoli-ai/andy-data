using System.Diagnostics;
using Andy.Data.Backend;
using Andy.Data;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// <c>dataframe_union</c> — concatenates two or more schema-compatible datasets.
/// See docs/operations.md#dataframe_union.
/// </summary>
public sealed class UnionOperation : DataFrameOperationBase
{
    private readonly IDuckDbBackend _backend;
    private readonly IDatasetCatalog _catalog;

    /// <summary>Parameterless ctor used by the registry only to read <see cref="Metadata"/>.</summary>
    public UnionOperation() : this(null!, null!, null) { }

    public UnionOperation(IDuckDbBackend backend, IDatasetCatalog catalog, ILogger<UnionOperation>? logger = null)
    {
        _backend = backend;
        _catalog = catalog;
        UseLogger(logger);
    }

    /// <inheritdoc />
    public override OperationMetadata Metadata { get; } = new()
    {
        Id = "dataframe_union",
        Name = "DataFrame Union",
        Description =
            "Concatenates two or more datasets into 'into'. 'datasets' is an ordered array of dataset " +
            "ids. 'by_name' aligns columns by name (else by position); 'distinct' drops duplicate rows. " +
            "Standard envelope.",
        Parameters =
        [
            new DataFrameParam { Name = "datasets", Type = "array", Required = true,
                Description = "Ordered array of dataset ids to concatenate (at least two)." },
            new DataFrameParam { Name = "into", Type = "string", Required = true,
                Pattern = DatasetIdPattern, Description = "Output dataset id." },
            new DataFrameParam { Name = "by_name", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Align columns by name rather than position." },
            new DataFrameParam { Name = "distinct", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Drop duplicate rows across the union." },
            new DataFrameParam { Name = "explain", Type = "boolean", Required = false, DefaultValue = false,
                Description = "Include the DuckDB query plan in stats.plan." },
        ],
    };

    /// <inheritdoc />
    public override DataFrameResponse Execute(
        IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions options)
    {
        var intoId = GetParameter<string>(parameters, "into");

        return Guard(parameters, options, _backend, ct =>
        {
            var byName = GetBoolOrNull(parameters, "by_name") ?? false;
            var distinct = GetBoolOrNull(parameters, "distinct") ?? false;
            var ids = ToStringList("datasets", parameters.GetValueOrDefault("datasets"));
            if (ids.Count < 2)
            {
                throw new DataFrameException(DataFrameErrorCodes.InvalidArgument,
                    "'datasets' must list at least two dataset ids.");
            }

            foreach (var id in ids)
            {
                _ = RequireDataset(_catalog, id); // DATASET_NOT_FOUND for any missing input
            }

            var sw = Stopwatch.StartNew();
            IReadOnlyList<ColumnSchema> schema;
            string? plan;
            try
            {
                plan = GetBoolOrNull(parameters, "explain") ?? false
                    ? _backend.ExplainUnion(ids, byName, distinct, ct)
                    : null;
                schema = _backend.Union(intoId, ids, byName, distinct, ct);
            }
            catch (DataFrameException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Incompatible column counts/types surface as a DuckDB binder error.
                throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                    $"Datasets are not union-compatible: {ex.Message}");
            }

            return Finish(_backend, _catalog, intoId, schema, $"union:{string.Join("+", ids)}", sw, plan: plan, ct: ct);
        });
    }
}
