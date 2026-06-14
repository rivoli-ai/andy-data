using Andy.Data.Backend;
using Microsoft.Extensions.Logging;

namespace Andy.Data.Operations;

/// <summary>
/// Convenience facade over the framework-independent dataframe operations. Holds the DuckDB backend
/// and dataset catalog, constructs every operation once, and dispatches by operation id. Construct
/// one engine per session (one backend == one DuckDB connection used under a lock — safe to call
/// concurrently, but inter-query work serializes; use one engine per concurrent stream).
/// </summary>
public sealed class DataFrameEngine : IDisposable
{
    private readonly Dictionary<string, DataFrameOperationBase> _ops;
    private readonly bool _ownsBackend;

    /// <summary>The underlying DuckDB backend.</summary>
    public IDuckDbBackend Backend { get; }

    /// <summary>The dataset catalog (id → schema/provenance).</summary>
    public IDatasetCatalog Catalog { get; }

    /// <summary>Creates an engine with a fresh in-memory DuckDB backend and dataset catalog.</summary>
    public DataFrameEngine(IPathPolicy? pathPolicy = null, ILoggerFactory? loggerFactory = null)
        : this(new DuckDbBackend(loggerFactory?.CreateLogger<DuckDbBackend>()),
               new InMemoryDatasetCatalog(), pathPolicy, loggerFactory, ownsBackend: true)
    {
    }

    /// <summary>Creates an engine over an existing backend and catalog (the caller owns the backend).</summary>
    public DataFrameEngine(
        IDuckDbBackend backend, IDatasetCatalog catalog,
        IPathPolicy? pathPolicy = null, ILoggerFactory? loggerFactory = null)
        : this(backend, catalog, pathPolicy, loggerFactory, ownsBackend: false)
    {
    }

    private DataFrameEngine(
        IDuckDbBackend backend, IDatasetCatalog catalog,
        IPathPolicy? pathPolicy, ILoggerFactory? lf, bool ownsBackend)
    {
        Backend = backend;
        Catalog = catalog;
        _ownsBackend = ownsBackend;

        ILogger<T>? L<T>() => lf?.CreateLogger<T>();

        var ops = new DataFrameOperationBase[]
        {
            new LoadCsvOperation(backend, catalog, pathPolicy, L<LoadCsvOperation>()),
            new LoadJsonOperation(backend, catalog, pathPolicy, L<LoadJsonOperation>()),
            new LoadParquetOperation(backend, catalog, pathPolicy, L<LoadParquetOperation>()),
            new LoadDeltaOperation(backend, catalog, pathPolicy, L<LoadDeltaOperation>()),
            new ExportOperation(backend, catalog, pathPolicy, L<ExportOperation>()),
            new SchemaOperation(catalog, L<SchemaOperation>()),
            new ProfileOperation(backend, catalog, L<ProfileOperation>()),
            new PreviewOperation(backend, catalog, L<PreviewOperation>()),
            new ValueCountsOperation(backend, catalog, L<ValueCountsOperation>()),
            new AssertOperation(backend, catalog, L<AssertOperation>()),
            new SelectOperation(backend, catalog, L<SelectOperation>()),
            new FilterOperation(backend, catalog, L<FilterOperation>()),
            new WithColumnOperation(backend, catalog, L<WithColumnOperation>()),
            new RenameOperation(backend, catalog, L<RenameOperation>()),
            new GroupByOperation(backend, catalog, L<GroupByOperation>()),
            new WindowOperation(backend, catalog, L<WindowOperation>()),
            new PivotOperation(backend, catalog, L<PivotOperation>()),
            new UnpivotOperation(backend, catalog, L<UnpivotOperation>()),
            new UnnestOperation(backend, catalog, L<UnnestOperation>()),
            new JoinOperation(backend, catalog, L<JoinOperation>()),
            new SampleOperation(backend, catalog, L<SampleOperation>()),
            new SortOperation(backend, catalog, L<SortOperation>()),
            new DistinctOperation(backend, catalog, L<DistinctOperation>()),
            new UnionOperation(backend, catalog, L<UnionOperation>()),
            new FillnaOperation(backend, catalog, L<FillnaOperation>()),
            new DropnaOperation(backend, catalog, L<DropnaOperation>()),
            new ListOperation(catalog, L<ListOperation>()),
            new DropOperation(backend, catalog, L<DropOperation>()),
        };

        _ops = ops.ToDictionary(o => o.Metadata.Id, StringComparer.Ordinal);
    }

    /// <summary>The metadata of every registered operation.</summary>
    public IReadOnlyCollection<OperationMetadata> Operations => _ops.Values.Select(o => o.Metadata).ToList();

    /// <summary>Gets an operation by id, or throws <c>INVALID_ARGUMENT</c> if unknown.</summary>
    public DataFrameOperationBase Get(string operationId) =>
        _ops.TryGetValue(operationId, out var op)
            ? op
            : throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, $"Unknown operation '{operationId}'.");

    /// <summary>Executes an operation by id and returns the response envelope.</summary>
    public DataFrameResponse Execute(
        string operationId, IReadOnlyDictionary<string, object?> parameters, DataFrameExecuteOptions? options = null) =>
        Get(operationId).Execute(parameters, options ?? DataFrameExecuteOptions.Default);

    /// <summary>Disposes the backend when this engine created it.</summary>
    public void Dispose()
    {
        if (_ownsBackend && Backend is IDisposable d)
        {
            d.Dispose();
        }
    }
}
