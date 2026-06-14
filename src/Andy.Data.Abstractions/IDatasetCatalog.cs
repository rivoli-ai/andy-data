namespace Andy.Data;

/// <summary>
/// Metadata recorded for a registered dataset. The backend relation/handle itself lives in the
/// backend layer keyed by <see cref="DatasetId"/>; this is the framework-independent metadata.
/// </summary>
/// <param name="DatasetId">Caller-supplied id the dataset is registered under.</param>
/// <param name="Schema">Ordered column schema.</param>
/// <param name="Source">Provenance, e.g. the load path or the producing operation.</param>
/// <param name="RowCount">Known row count, or null if not yet materialized.</param>
public sealed record DatasetEntry(
    string DatasetId,
    IReadOnlyList<ColumnSchema> Schema,
    string Source,
    long? RowCount = null);

/// <summary>
/// Session-scoped registry mapping a caller-supplied <c>dataset_id</c> to its schema/provenance.
/// Loads register a dataset; transformations register new ones. Released by <c>dataframe_drop</c>
/// or at session end.
/// </summary>
public interface IDatasetCatalog
{
    /// <summary>Registers (or replaces) a dataset. Returns the stored entry.</summary>
    DatasetEntry Register(DatasetEntry entry);

    /// <summary>Returns true if a dataset is registered under <paramref name="datasetId"/>.</summary>
    bool Contains(string datasetId);

    /// <summary>Gets the entry for a dataset, or null if it is not registered.</summary>
    DatasetEntry? Get(string datasetId);

    /// <summary>Gets the current schema for a dataset, or null if it is not registered.</summary>
    IReadOnlyList<ColumnSchema>? TryGetSchema(string datasetId);

    /// <summary>Lists all registered datasets.</summary>
    IReadOnlyCollection<DatasetEntry> List();

    /// <summary>Releases a dataset. Returns true if it existed.</summary>
    bool Drop(string datasetId);
}
