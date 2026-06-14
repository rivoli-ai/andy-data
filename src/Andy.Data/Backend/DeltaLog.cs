using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Data;

namespace Andy.Data.Backend;

/// <summary>
/// A minimal, DuckDB-independent reader and writer of the Delta Lake transaction log
/// (<c>_delta_log/</c>). The DuckDB <c>delta</c> extension only reads a table's latest snapshot and
/// cannot write at all, so time travel and export are implemented here by replaying / emitting the
/// log directly.
/// </summary>
/// <remarks>
/// This is a deliberately <b>happy-path</b> implementation of the Delta protocol. It supports
/// unpartitioned tables whose history is expressed as plain JSON commits (the shape written by
/// delta-rs / Spark for simple tables). It returns a clear <see cref="DataFrameException"/> — never a
/// silent wrong answer — for anything outside that envelope: checkpoints, partition columns, reader
/// features (deletion vectors, column mapping, …), or a requested version/timestamp that the log
/// cannot satisfy. See docs/operations.md#dataframe_load_delta for the documented limits.
/// </remarks>
internal static class DeltaLog
{
    private const string LogDirName = "_delta_log";

    /// <summary>
    /// Per-table-root serialization registry. Concurrent appends to the same Delta table path are
    /// serialized (via the entry's <see cref="LockEntry.Gate"/>) so that data-file writes and JSON
    /// commit emission do not interleave. Entries are reference-counted: an entry is created on the
    /// first acquire for a path and removed once the last holder releases it, so the registry does
    /// not leak a lock object per distinct path for the process lifetime (issue #191). All mutation
    /// of <see cref="LockEntry.RefCount"/> and the dictionary itself happens under
    /// <see cref="RegistryGate"/>, so "acquire an existing entry" and "remove on last release" cannot
    /// race: a releaser that drops the count to zero removes the entry under the same gate that an
    /// acquirer must hold to observe or increment it.
    /// </summary>
    private static readonly Dictionary<string, LockEntry> TableLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="TableLocks"/> and every <see cref="LockEntry.RefCount"/> update.</summary>
    private static readonly object RegistryGate = new();

    /// <summary>
    /// Test-only seam invoked with the commit path just before each put-if-absent write attempt in
    /// the optimistic-concurrency loop (issue #206). Production leaves it null. A test sets it to
    /// simulate a concurrent writer landing the chosen version first, so the retry path can be
    /// exercised deterministically; the hook is path-guarded by the test so concurrently-running
    /// Delta tests are unaffected.
    /// </summary>
    internal static Action<string>? OnBeforeCommitWriteForTests;

    private sealed class LockEntry
    {
        /// <summary>The object callers <c>lock</c> on to serialize writes to one table path.</summary>
        public object Gate { get; } = new();

        /// <summary>Live holders of this entry; the entry is evicted when this reaches zero.</summary>
        public int RefCount { get; set; }
    }

    /// <summary>
    /// A reference-counted lease on a table path's serialization gate. <see cref="Gate"/> is the
    /// object to <c>lock</c> on; <see cref="Dispose"/> releases the lease (and evicts the registry
    /// entry when it was the last holder). Acquiring twice for the same path on the same thread is
    /// safe — the underlying <c>lock</c> is reentrant and the ref-count balances across disposes.
    /// </summary>
    internal readonly struct TableLockLease : IDisposable
    {
        private readonly string _key;
        public object Gate { get; }

        internal TableLockLease(string key, object gate)
        {
            _key = key;
            Gate = gate;
        }

        public void Dispose()
        {
            lock (RegistryGate)
            {
                if (TableLocks.TryGetValue(_key, out var entry) && --entry.RefCount <= 0)
                {
                    TableLocks.Remove(_key);
                }
            }
        }
    }

    /// <summary>
    /// Acquires a reference-counted lease on the serialization gate for <paramref name="tableRoot"/>.
    /// The caller must <c>lock</c> on <see cref="TableLockLease.Gate"/> and dispose the lease when
    /// done (typically via <c>using</c>). The entry is created on first acquire and evicted on last
    /// release, bounding the registry to the set of paths with a write in flight.
    /// </summary>
    internal static TableLockLease AcquireTableLock(string tableRoot)
    {
        var key = Path.GetFullPath(tableRoot);
        lock (RegistryGate)
        {
            if (!TableLocks.TryGetValue(key, out var entry))
            {
                entry = new LockEntry();
                TableLocks[key] = entry;
            }

            entry.RefCount++;
            return new TableLockLease(key, entry.Gate);
        }
    }

    /// <summary>A 20-digit, zero-padded commit file, e.g. <c>00000000000000000003.json</c>.</summary>
    private static readonly Regex CommitFileRegex = new(@"^(\d{20})\.json$", RegexOptions.Compiled);

    /// <summary>The active data files (absolute paths) of a table at one resolved version.</summary>
    internal sealed record Snapshot(long Version, IReadOnlyList<string> AbsoluteFilePaths);

    /// <summary>Per-column statistics for a single added data file.</summary>
    internal sealed record ColumnStats(object? Min, object? Max, long NullCount);

    /// <summary>One data file to record in the log.</summary>
    internal sealed record AddFile(
        string RelativePath,
        long Size,
        long Rows,
        long ModificationTimeMs,
        IReadOnlyDictionary<string, string>? PartitionValues = null,
        IReadOnlyDictionary<string, ColumnStats>? Stats = null);

    /// <summary>
    /// Replays the transaction log and resolves the set of active data files at the requested point in
    /// time. Exactly one of <paramref name="version"/> / <paramref name="timestamp"/> may be set; if
    /// both are null the latest version is resolved. Throws <see cref="DataFrameException"/> for an
    /// invalid path, an unsatisfiable version/timestamp, or an unsupported table feature.
    /// </summary>
    public static Snapshot ReadSnapshot(string tableRoot, long? version, DateTimeOffset? timestamp, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var logDir = Path.Combine(tableRoot, LogDirName);
        if (!Directory.Exists(logDir))
        {
            throw new DataFrameException(DataFrameErrorCodes.FileNotFound,
                $"Not a Delta table (no {LogDirName}/ directory): {tableRoot}",
                new Dictionary<string, object?> { ["path"] = tableRoot });
        }

        // The happy-path reader replays JSON commits from version 0. A checkpoint implies older
        // commits may have been pruned, so a from-zero replay could be incomplete — refuse rather
        // than risk a wrong snapshot.
        if (File.Exists(Path.Combine(logDir, "_last_checkpoint"))
            || Directory.EnumerateFiles(logDir, "*.checkpoint.parquet").Any())
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "Checkpointed Delta table: a `_last_checkpoint` marker or `*.checkpoint.parquet` files exist " +
                "in `_delta_log/`, which means older JSON commits may have been compacted away. This " +
                "happy-path reader replays JSON commits from version 0 only, so it cannot safely resolve a " +
                "snapshot for time travel. Load the latest snapshot without version/timestamp, or avoid " +
                "checkpointing/vacuuming history.");
        }

        var commits = Directory.EnumerateFiles(logDir, "*.json")
            .Select(f => (file: f, m: CommitFileRegex.Match(Path.GetFileName(f))))
            .Where(x => x.m.Success)
            .Select(x => (version: long.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture), x.file))
            .OrderBy(x => x.version)
            .ToList();
        ct.ThrowIfCancellationRequested();

        if (commits.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta table at {tableRoot} has no JSON commits in {LogDirName}/.");
        }

        var maxVersion = commits[^1].version;
        var target = ResolveTarget(commits, version, timestamp, maxVersion);

        // Replay commits [0..target] in order, applying add/remove to the active file set and
        // validating that every commit stays within the supported feature envelope.
        var active = new List<string>();
        foreach (var (commitVersion, file) in commits)
        {
            ct.ThrowIfCancellationRequested();
            if (commitVersion > target)
            {
                break;
            }

            ApplyCommit(file, commitVersion, active, ct);
        }

        var absolute = active
            .Select(rel => Path.GetFullPath(Path.Combine(tableRoot, rel)))
            .ToList();

        if (absolute.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta table {tableRoot} has no active data files at version {target}; " +
                "empty snapshots are not supported by this reader.");
        }

        return new Snapshot(target, absolute);
    }

    private static long ResolveTarget(
        IReadOnlyList<(long version, string file)> commits, long? version, DateTimeOffset? timestamp, long maxVersion)
    {
        if (version is { } v)
        {
            if (v < 0 || commits.All(c => c.version != v))
            {
                throw new DataFrameException(DataFrameErrorCodes.BackendError,
                    $"Delta version {v} does not exist; available versions are 0..{maxVersion}.",
                    new Dictionary<string, object?> { ["requested_version"] = v, ["max_version"] = maxVersion });
            }

            return v;
        }

        if (timestamp is { } ts)
        {
            var cutoff = ts.ToUnixTimeMilliseconds();
            long? best = null;
            foreach (var (commitVersion, file) in commits)
            {
                if (CommitTimestampMs(file) <= cutoff)
                {
                    best = commitVersion;
                }
            }

            if (best is null)
            {
                throw new DataFrameException(DataFrameErrorCodes.BackendError,
                    $"No Delta version was committed at or before {ts:O}; the earliest commit is later.",
                    new Dictionary<string, object?> { ["requested_timestamp"] = ts.ToString("O") });
            }

            return best.Value;
        }

        return maxVersion;
    }

    /// <summary>Reads a commit's timestamp (ms): its <c>commitInfo.timestamp</c>, else the file mtime.</summary>
    private static long CommitTimestampMs(string commitFile)
    {
        foreach (var line in File.ReadLines(commitFile))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("commitInfo", out var ci)
                && ci.TryGetProperty("timestamp", out var tsEl)
                && tsEl.TryGetInt64(out var ms))
            {
                return ms;
            }
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(commitFile)).ToUnixTimeMilliseconds();
    }

    private static void ApplyCommit(string commitFile, long commitVersion, List<string> active, CancellationToken ct)
    {
        foreach (var line in File.ReadLines(commitFile))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("protocol", out var protocol))
            {
                GuardProtocol(protocol, commitVersion);
            }
            else if (root.TryGetProperty("metaData", out var meta))
            {
                GuardMetadata(meta, commitVersion);
            }
            else if (root.TryGetProperty("add", out var add))
            {
                GuardNoDeletionVector(add, commitVersion);
                var path = DecodeDataFilePath(add.GetProperty("path").GetString()!, commitVersion);
                active.Remove(path);
                active.Add(path);
            }
            else if (root.TryGetProperty("remove", out var remove)
                && remove.TryGetProperty("path", out var removePath))
            {
                active.Remove(DecodeDataFilePath(removePath.GetString()!, commitVersion));
            }
        }
    }

    private static void GuardProtocol(JsonElement protocol, long commitVersion)
    {
        // Reader version >= 3 means the table relies on reader features (deletion vectors, column
        // mapping, …) that this happy-path reader does not implement.
        if (protocol.TryGetProperty("minReaderVersion", out var rv) && rv.TryGetInt32(out var reader)
            && reader >= 3)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta table requires reader version {reader} (reader features such as deletion vectors " +
                "or column mapping); only protocol reader versions 1–2 are supported.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion });
        }
    }

    private static void GuardMetadata(JsonElement meta, long commitVersion)
    {
        if (meta.TryGetProperty("partitionColumns", out var parts)
            && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "Partitioned Delta tables are not supported for time travel; partition values live in " +
                "the log rather than the data files. Reload the latest snapshot via the delta extension, " +
                "or repartition on export.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion });
        }

        // Column mapping is a reader-version-2 feature, so the protocol guard alone does not catch
        // it — and reading mapped tables as plain Parquet would return physical column names
        // (col-<guid>…) instead of logical ones: a silent wrong answer.
        if (meta.TryGetProperty("configuration", out var conf) && conf.ValueKind == JsonValueKind.Object
            && conf.TryGetProperty("delta.columnMapping.mode", out var mode)
            && mode.ValueKind == JsonValueKind.String
            && !string.Equals(mode.GetString(), "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta table uses column mapping (delta.columnMapping.mode={mode.GetString()}), which this " +
                "reader cannot resolve; the data files carry physical column names. Column mapping is " +
                "outside the happy path.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion });
        }
    }

    /// <summary>
    /// Decodes an <c>add</c>/<c>remove</c> action's <c>path</c>: per the Delta protocol it is a
    /// percent-encoded path relative to the table root (Spark encodes spaces and special characters),
    /// or an absolute URI — which this local-filesystem reader rejects rather than mis-resolving.
    /// </summary>
    private static string DecodeDataFilePath(string path, long commitVersion)
    {
        if (path.Contains("://", StringComparison.Ordinal))
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta commit references an absolute URI data file ('{path}'); only paths relative to " +
                "the table root are supported by this reader.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion, ["data_path"] = path });
        }

        return Uri.UnescapeDataString(path);
    }

    private static void GuardNoDeletionVector(JsonElement add, long commitVersion)
    {
        if (add.TryGetProperty("deletionVector", out var dv) && dv.ValueKind == JsonValueKind.Object)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "Delta table uses deletion vectors, which this reader cannot apply (it would over-count " +
                "rows). Deletion vectors are outside the happy path.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion });
        }
    }

    /// <summary>
    /// Validates that every column type can be mapped to a Delta type, throwing the same
    /// <c>INVALID_TYPE</c> error the log writer would — but before any data has been written.
    /// </summary>
    public static void EnsureExportable(IReadOnlyList<ColumnSchema> schema)
    {
        foreach (var column in schema)
        {
            _ = DeltaTypeFor(column.Type);
        }
    }

    /// <summary>
    /// Writes a brand-new, single-commit (version 0) Delta transaction log for data files that have
    /// already been written under <paramref name="tableRoot"/>. Emits protocol (reader 1 / writer 2),
    /// metaData (with the mapped schema), and one <c>add</c> action per file.
    /// </summary>
    public static void WriteNewTable(
        string tableRoot,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<string> partitionColumns,
        IReadOnlyList<AddFile> files,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var logDir = Path.Combine(tableRoot, LogDirName);
        Directory.CreateDirectory(logDir);
        var nowMs = now.ToUnixTimeMilliseconds();

        var lines = new List<string>
        {
            Serialize(new Dictionary<string, object?>
            {
                ["commitInfo"] = new Dictionary<string, object?>
                {
                    ["timestamp"] = nowMs,
                    ["operation"] = "WRITE",
                    ["operationParameters"] = new Dictionary<string, object?> { ["mode"] = "ErrorIfExists" },
                    ["engineInfo"] = "Andy.Data",
                },
            }),
            Serialize(new Dictionary<string, object?>
            {
                ["protocol"] = new Dictionary<string, object?>
                {
                    ["minReaderVersion"] = 1,
                    ["minWriterVersion"] = 2,
                },
            }),
            Serialize(new Dictionary<string, object?>
            {
                ["metaData"] = new Dictionary<string, object?>
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["format"] = new Dictionary<string, object?>
                    {
                        ["provider"] = "parquet",
                        ["options"] = new Dictionary<string, object?>(),
                    },
                    ["schemaString"] = SchemaString(schema),
                    ["partitionColumns"] = partitionColumns,
                    ["configuration"] = new Dictionary<string, object?>(),
                    ["createdTime"] = nowMs,
                },
            }),
        };

        foreach (var f in files)
        {
            lines.Add(SerializeAdd(f));
        }

        ct.ThrowIfCancellationRequested();
        WriteCommitAtomically(Path.Combine(logDir, $"{0:D20}.json"), string.Join("\n", lines) + "\n", 0);
    }

    /// <summary>
    /// Writes a single commit file with put-if-absent, all-or-nothing semantics. The file is opened
    /// with <see cref="FileMode.CreateNew"/>, which atomically fails (rather than truncating or
    /// clobbering) if version <paramref name="commitVersion"/> already exists — that collision is the
    /// optimistic-concurrency signal that another writer won the same commit number. The full payload
    /// is written and flushed before the handle closes, so a crash mid-write cannot leave a partial
    /// (already-published) commit: either the named commit exists in full or it does not exist at all.
    /// </summary>
    internal static void WriteCommitAtomically(string commitPath, string content, long commitVersion)
    {
        try
        {
            using var stream = new FileStream(
                commitPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException ex) when (File.Exists(commitPath))
        {
            // CreateNew throws IOException when the target already exists: a concurrent writer (in
            // this or another process) already published this commit number. Surface it as a clear
            // concurrency error rather than silently overwriting another writer's commit.
            throw new DataFrameException(DataFrameErrorCodes.TargetExists,
                $"Delta commit version {commitVersion} already exists at '{commitPath}'; a concurrent " +
                "writer committed this version first. Retry the append against the new latest version.",
                new Dictionary<string, object?> { ["commit_version"] = commitVersion, ["path"] = commitPath },
                ex);
        }
    }

    /// <summary>
    /// Appends a new commit to an existing Delta table. Validates schema and partition-column
    /// compatibility.
    /// </summary>
    public static void AppendCommit(
        string tableRoot,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<AddFile> files,
        DateTimeOffset now,
        IReadOnlyList<string>? partitionColumns = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = AcquireTableLock(tableRoot);
        lock (lease.Gate)
        {
            AppendCommitCore(tableRoot, schema, files, now, partitionColumns, ct);
        }
    }

    private static void AppendCommitCore(
        string tableRoot,
        IReadOnlyList<ColumnSchema> schema,
        IReadOnlyList<AddFile> files,
        DateTimeOffset now,
        IReadOnlyList<string>? partitionColumns = null,
        CancellationToken ct = default)
    {
        var logDir = Path.Combine(tableRoot, LogDirName);
        if (!Directory.Exists(logDir))
        {
            throw new DataFrameException(DataFrameErrorCodes.FileNotFound,
                $"Not a Delta table (no {LogDirName}/ directory): {tableRoot}",
                new Dictionary<string, object?> { ["path"] = tableRoot });
        }

        if (File.Exists(Path.Combine(logDir, "_last_checkpoint"))
            || Directory.EnumerateFiles(logDir, "*.checkpoint.parquet").Any())
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "Checkpointed Delta table: a `_last_checkpoint` marker or `*.checkpoint.parquet` files exist " +
                "in `_delta_log/`, which means older JSON commits may have been compacted away. This " +
                "happy-path writer appends by emitting the next JSON commit, so it cannot safely append to a " +
                "checkpointed table.");
        }

        var commits = Directory.EnumerateFiles(logDir, "*.json")
            .Select(f => (file: f, m: CommitFileRegex.Match(Path.GetFileName(f))))
            .Where(x => x.m.Success)
            .Select(x => (version: long.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture), x.file))
            .OrderBy(x => x.version)
            .ToList();

        if (commits.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Delta table at {tableRoot} has no JSON commits in {LogDirName}/.");
        }

        var metadata = ReadMetadata(commits);

        if (metadata.MinWriterVersion > 2)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                $"Cannot append to this Delta table: it requires minWriterVersion {metadata.MinWriterVersion}, " +
                "but this writer supports Delta writer versions 1–2. Upgrade the table with a compatible writer, " +
                "or recreate it with minWriterVersion 2.");
        }

        ValidateSchemaCompatibility(metadata.Schema, schema);
        ValidatePartitionCompatibility(metadata.PartitionColumns, partitionColumns ?? Array.Empty<string>());

        var nowMs = now.ToUnixTimeMilliseconds();
        var lines = new List<string>
        {
            Serialize(new Dictionary<string, object?>
            {
                ["commitInfo"] = new Dictionary<string, object?>
                {
                    ["timestamp"] = nowMs,
                    ["operation"] = "WRITE",
                    ["operationParameters"] = new Dictionary<string, object?> { ["mode"] = "Append" },
                    ["engineInfo"] = "Andy.Data",
                },
            }),
        };

        foreach (var f in files)
        {
            lines.Add(SerializeAdd(f));
        }

        var content = string.Join("\n", lines) + "\n";

        // Optimistic concurrency (issue #206). Each attempt re-reads the latest committed version
        // from disk under the per-table lock held by the caller (AppendCommit), assigns the next
        // number, and publishes the commit with put-if-absent semantics (WriteCommitAtomically). If
        // a concurrent writer in *another process* published that version first, the put-if-absent
        // write throws TARGET_EXISTS; we re-read the new head and retry against the next number. The
        // data files are already written and are version-independent, so only the commit-log entry
        // needs a fresh number — no data is rewritten. Same-process appends are serialized by the
        // lock and never collide here; retries only ever resolve cross-process races. Bound the
        // attempts so a structurally stuck table surfaces the error instead of looping forever.
        const int maxAttempts = 64;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var nextVersion = LatestCommittedVersion(logDir) + 1;
            var commitPath = Path.Combine(logDir, $"{nextVersion:D20}.json");
            OnBeforeCommitWriteForTests?.Invoke(commitPath);
            try
            {
                WriteCommitAtomically(commitPath, content, nextVersion);
                return;
            }
            catch (DataFrameException ex)
                when (ex.ErrorCode == DataFrameErrorCodes.TargetExists && attempt < maxAttempts)
            {
                // Lost the race for this version number to a concurrent writer; re-read the head on
                // the next iteration and try the next version.
            }
        }
    }

    /// <summary>Returns the highest committed version number currently present in <paramref name="logDir"/>.</summary>
    private static long LatestCommittedVersion(string logDir)
    {
        long latest = -1;
        foreach (var file in Directory.EnumerateFiles(logDir, "*.json"))
        {
            var m = CommitFileRegex.Match(Path.GetFileName(file));
            if (m.Success)
            {
                var v = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (v > latest)
                {
                    latest = v;
                }
            }
        }

        return latest;
    }

    private static string SerializeAdd(AddFile f)
    {
        var stats = new Dictionary<string, object?> { ["numRecords"] = f.Rows };
        if (f.Stats is { Count: > 0 })
        {
            var minValues = new Dictionary<string, object?>();
            var maxValues = new Dictionary<string, object?>();
            var nullCount = new Dictionary<string, object?>();
            foreach (var (name, col) in f.Stats)
            {
                nullCount[name] = col.NullCount;
                if (col.Min is not null)
                {
                    minValues[name] = col.Min;
                }

                if (col.Max is not null)
                {
                    maxValues[name] = col.Max;
                }
            }

            stats["minValues"] = minValues;
            stats["maxValues"] = maxValues;
            stats["nullCount"] = nullCount;
        }

        var add = new Dictionary<string, object?>
        {
            ["path"] = f.RelativePath,
            ["partitionValues"] = f.PartitionValues ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
            ["size"] = f.Size,
            ["modificationTime"] = f.ModificationTimeMs,
            ["dataChange"] = true,
            ["stats"] = Serialize(stats),
        };
        return Serialize(new Dictionary<string, object?> { ["add"] = add });
    }

    internal sealed record Metadata(
        string Id,
        IReadOnlyList<ColumnSchema> Schema,
        IReadOnlyList<string> PartitionColumns,
        int MinReaderVersion = 1,
        int MinWriterVersion = 2);

    private static Metadata ReadMetadata(IReadOnlyList<(long version, string file)> commits)
    {
        var minReaderVersion = 1;
        var minWriterVersion = 2;
        Metadata? metadata = null;

        foreach (var (_, file) in commits)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("protocol", out var protocol))
                {
                    if (protocol.TryGetProperty("minReaderVersion", out var rv)
                        && rv.TryGetInt32(out var reader))
                    {
                        minReaderVersion = reader;
                    }

                    if (protocol.TryGetProperty("minWriterVersion", out var wv)
                        && wv.TryGetInt32(out var writer))
                    {
                        minWriterVersion = writer;
                    }
                }
                else if (root.TryGetProperty("metaData", out var meta))
                {
                    var id = meta.GetProperty("id").GetString()!;
                    var schemaString = meta.GetProperty("schemaString").GetString()!;
                    var schema = ParseSchemaString(schemaString);

                    var partitionColumns = new List<string>();
                    if (meta.TryGetProperty("partitionColumns", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            partitionColumns.Add(part.GetString()!);
                        }
                    }

                    metadata = new Metadata(id, schema, partitionColumns, minReaderVersion, minWriterVersion);
                }
            }
        }

        if (metadata is null)
        {
            throw new DataFrameException(DataFrameErrorCodes.BackendError,
                "Delta table has no metadata action in its transaction log.");
        }

        return metadata;
    }

    private static IReadOnlyList<ColumnSchema> ParseSchemaString(string schemaString)
    {
        using var doc = JsonDocument.Parse(schemaString);
        var root = doc.RootElement;
        var fields = root.GetProperty("fields");
        var result = new List<ColumnSchema>();
        foreach (var field in fields.EnumerateArray())
        {
            var name = field.GetProperty("name").GetString()!;
            var type = field.GetProperty("type").GetString()!;
            var nullable = field.GetProperty("nullable").GetBoolean();
            result.Add(new ColumnSchema(name, type, nullable));
        }

        return result;
    }

    private static void ValidateSchemaCompatibility(IReadOnlyList<ColumnSchema> existing, IReadOnlyList<ColumnSchema> incoming)
    {
        if (existing.Count != incoming.Count)
        {
            throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                $"Append schema has {incoming.Count} columns but the existing Delta table has {existing.Count}.");
        }

        for (var i = 0; i < existing.Count; i++)
        {
            var e = existing[i];
            var n = incoming[i];
            var incomingDeltaType = DeltaTypeFor(n.Type);
            if (!string.Equals(e.Name, n.Name, StringComparison.Ordinal)
                || !string.Equals(NormalizeType(e.Type), NormalizeType(incomingDeltaType), StringComparison.OrdinalIgnoreCase)
                || e.Nullable != n.Nullable)
            {
                throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                    $"Append schema column '{n.Name}' ({n.Type}, nullable={n.Nullable}) does not match existing " +
                    $"column '{e.Name}' ({e.Type}, nullable={e.Nullable}).");
            }
        }
    }

    private static void ValidatePartitionCompatibility(
        IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        var existingSet = existing.Select(UnquoteIdent).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomingSet = incoming.Select(UnquoteIdent).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existing.Count == 0 && incoming.Count == 0)
        {
            return;
        }

        if (existing.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                "Cannot append partitioned data to an unpartitioned Delta table.");
        }

        if (incoming.Count == 0)
        {
            throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                "Cannot append unpartitioned data to a partitioned Delta table.");
        }

        if (!existingSet.SetEquals(incomingSet))
        {
            throw new DataFrameException(DataFrameErrorCodes.SchemaMismatch,
                $"Partition columns do not match the existing Delta table. " +
                $"Existing: [{string.Join(", ", existing)}]; incoming: [{string.Join(", ", incoming)}].");
        }
    }

    private static string UnquoteIdent(string quoted)
    {
        if (quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"')
        {
            return quoted[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return quoted;
    }

    private static string NormalizeType(string type) =>
        type.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string Serialize(object value) =>
        JsonSerializer.Serialize(value, DeltaJson);

    private static readonly JsonSerializerOptions DeltaJson = new() { WriteIndented = false };

    /// <summary>Builds the Delta <c>schemaString</c> (an embedded JSON document) from a DuckDB schema.</summary>
    private static string SchemaString(IReadOnlyList<ColumnSchema> schema)
    {
        var fields = schema.Select(c => (object)new Dictionary<string, object?>
        {
            ["name"] = c.Name,
            ["type"] = DeltaTypeFor(c.Type),
            ["nullable"] = c.Nullable,
            ["metadata"] = new Dictionary<string, object?>(),
        }).ToArray();

        return Serialize(new Dictionary<string, object?>
        {
            ["type"] = "struct",
            ["fields"] = fields,
        });
    }

    private static readonly Regex DecimalRegex =
        new(@"^DECIMAL\((\d+)\s*,\s*(\d+)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Maps a DuckDB type (as DESCRIBE reports it) to its Delta primitive type name.</summary>
    internal static string DeltaTypeFor(string duckType)
    {
        var t = duckType.Trim().ToUpperInvariant();

        var dec = DecimalRegex.Match(t);
        if (dec.Success)
        {
            return $"decimal({dec.Groups[1].Value},{dec.Groups[2].Value})";
        }

        return t switch
        {
            "BOOLEAN" or "BOOL" => "boolean",
            "TINYINT" or "INT1" => "byte",
            "SMALLINT" or "INT2" or "SHORT" => "short",
            "INTEGER" or "INT4" or "INT" or "SIGNED" => "integer",
            "BIGINT" or "INT8" or "LONG" => "long",
            "FLOAT" or "REAL" or "FLOAT4" => "float",
            "DOUBLE" or "FLOAT8" => "double",
            "VARCHAR" or "TEXT" or "STRING" or "CHAR" or "BPCHAR" or "UUID" => "string",
            "DATE" => "date",
            "TIMESTAMP" or "DATETIME" or "TIMESTAMP WITH TIME ZONE" or "TIMESTAMPTZ" => "timestamp",
            "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" => "binary",
            _ => throw new DataFrameException(DataFrameErrorCodes.InvalidType,
                $"Cannot export column of type '{duckType}' to Delta; no Delta type mapping is defined. " +
                "Supported: boolean, integer family, float/double, decimal, string, date, timestamp, binary.",
                new Dictionary<string, object?> { ["duckdb_type"] = duckType }),
        };
    }
}
