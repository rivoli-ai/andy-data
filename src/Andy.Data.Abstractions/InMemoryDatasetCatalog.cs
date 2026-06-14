using System.Collections.Concurrent;

namespace Andy.Data;

/// <summary>
/// Default thread-safe, in-memory <see cref="IDatasetCatalog"/>. One instance is scoped to an
/// execution session; datasets are isolated from other sessions and released at session end.
/// </summary>
public sealed class InMemoryDatasetCatalog : IDatasetCatalog
{
    private readonly ConcurrentDictionary<string, DatasetEntry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public DatasetEntry Register(DatasetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.DatasetId))
        {
            throw new DataFrameException(DataFrameErrorCodes.InvalidArgument, "dataset_id must be non-empty.");
        }

        _entries[entry.DatasetId] = entry;
        return entry;
    }

    /// <inheritdoc />
    public bool Contains(string datasetId) => _entries.ContainsKey(datasetId);

    /// <inheritdoc />
    public DatasetEntry? Get(string datasetId) =>
        _entries.TryGetValue(datasetId, out var e) ? e : null;

    /// <inheritdoc />
    public IReadOnlyList<ColumnSchema>? TryGetSchema(string datasetId) =>
        _entries.TryGetValue(datasetId, out var e) ? e.Schema : null;

    /// <inheritdoc />
    public IReadOnlyCollection<DatasetEntry> List() => _entries.Values.ToList();

    /// <inheritdoc />
    public bool Drop(string datasetId) => _entries.TryRemove(datasetId, out _);
}
