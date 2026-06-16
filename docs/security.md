# Security

`Andy.Data` is designed to be safe to drive from an untrusted or model-generated request. There are
two pillars: an **injection-free execution model** (no code execution, no model-supplied SQL) and an
optional **`IPathPolicy` filesystem gate** that constrains which paths may be read or written.

See also: [operations.md](operations.md), [reliability.md](reliability.md).

## Injection-free design

The engine never compiles or executes caller-supplied code, and it never executes caller-supplied
SQL. Operations that need expressive input — filters, computed columns, aggregations — accept a
**structured tree**, not a SQL string, and that tree is rendered through closed vocabularies.

### Every SQL token is accounted for

The SQL renderers (`PredicateSqlRenderer`, `ExpressionSqlRenderer`, and the `SqlText` helpers) emit
fragments in which **every token is exactly one of three kinds**:

1. **A fixed renderer template.** Each operator/function maps to a hard-coded template selected by a
   `switch` over a closed, enumerated vocabulary. For example the predicate operator `eq` always
   renders to `{col} = {right}`, and the expression function `upper` always renders to
   `upper({arg})`. An operator or function name not in the set throws `INVALID_PREDICATE` — it
   cannot reach SQL.
2. **A schema-resolved, quoted identifier.** Column names are resolved against the dataset's actual
   schema (case-insensitively) by `SqlText.ResolveColumn`; an unknown column throws
   `COLUMN_NOT_FOUND` *before* execution. The resolved canonical name is then double-quoted, with
   embedded double-quotes doubled, so an identifier cannot break out into SQL.
3. **An escaped literal.** Values are rendered by `SqlText.Literal` as culture-invariant,
   round-trippable forms; strings are single-quoted with embedded single-quotes doubled, so a
   literal string cannot terminate its quote and inject SQL.

Additional closed vocabularies guard the remaining corners:

- Aggregation function names are checked against a closed set (`INVALID_AGGREGATION` otherwise).
- Cast target types must match a conservative regex; `strptime` formats and `date_trunc` /
  `date_part` / `date_diff` / `date_add` units are validated against enumerated sets.
- CSV column-type hints (`columns`) and compression codecs must match plain-token regexes.
- The caller-supplied `dataset_id` never reaches SQL at all: it is mapped to an internally generated
  physical relation name. File paths and option values that *must* be inlined (DuckDB cannot prepare
  DDL) are emitted as escaped string literals.

Because there is no path from input to a raw SQL string, there is **no injection surface and no
model-supplied SQL**. Malformed or out-of-vocabulary input fails fast with a stable error code (see
[reliability.md](reliability.md)) rather than producing unexpected SQL.

## The `IPathPolicy` filesystem gate

Filesystem access in this repo is gated solely by `IPathPolicy`. It is a host-defined policy that
decides which paths the engine may read from or write to:

```csharp
namespace Andy.Data;

public interface IPathPolicy
{
    bool CanRead(string path);   // may the engine read this path?
    bool CanWrite(string path);  // may the engine write this path?
}
```

### Registering a policy

Pass an `IPathPolicy` to the engine constructor:

```csharp
using var engine = new DataFrameEngine(pathPolicy: myPolicy);
// or, over an existing backend/catalog:
// using var engine = new DataFrameEngine(backend, catalog, pathPolicy: myPolicy);
```

### Default behavior

When **no** policy is registered (the parameter is `null`), all paths are permitted and behavior is
unchanged — the gate is opt-in.

### Which operations consult it

- **Loaders read.** `dataframe_load_csv`, `dataframe_load_json`, `dataframe_load_parquet`, and
  `dataframe_load_delta` call `CanRead` on the input path before opening it.
- **Export writes.** `dataframe_export` calls `CanWrite` on the output path before writing.

A denied path throws and is mapped to a `PERMISSION_DENIED` envelope; the canonicalized path appears
in `Details["path"]`. No file is opened or created on a denial.

### Glob and concrete paths, canonicalization, and symlink resolution

- The path handed to the policy may be a **concrete file/directory or a glob pattern** (e.g.
  `data/*.csv`). `IPathPolicy` implementations should expect both.
- Before the policy sees a path, the engine **canonicalizes** it: it resolves `..` and separators
  via `Path.GetFullPath`, and **resolves symbolic links** over the existing path prefix. For a glob,
  the concrete base before the first wildcard is resolved and recombined with the wildcard tail.
- This canonicalization prevents traversal (`../../etc/passwd`) and symlink tricks from bypassing a
  prefix/allow-list policy: the policy always evaluates the real target, not a deceptive spelling of
  it.

### Worked example: a sandbox allow-list

A minimal policy that confines all reads and writes to a single sandbox directory:

```csharp
using Andy.Data;
using Andy.Data.Operations;

public sealed class SandboxPathPolicy : IPathPolicy
{
    private readonly string _root;

    public SandboxPathPolicy(string sandboxRoot)
        => _root = Path.GetFullPath(sandboxRoot) + Path.DirectorySeparatorChar;

    // The engine has already canonicalized 'path' (.. resolved, symlinks resolved over the
    // existing prefix), so a prefix check is sufficient and cannot be bypassed by traversal.
    private bool IsInside(string path)
        => Path.GetFullPath(path).StartsWith(_root, StringComparison.Ordinal);

    public bool CanRead(string path)  => IsInside(path);
    public bool CanWrite(string path) => IsInside(path);
}
```

Wiring it up and observing a denial:

```csharp
var policy = new SandboxPathPolicy("/srv/andy/sandbox");
using var engine = new DataFrameEngine(pathPolicy: policy);

// Load is allowed: the file lives inside the sandbox.
var load = engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
{
    ["path"] = "/srv/andy/sandbox/sales.csv",
    ["dataset_id"] = "sales",
});

// Export outside the sandbox is denied before any file is written.
var export = engine.Execute("dataframe_export", new Dictionary<string, object?>
{
    ["dataset_id"] = "sales",
    ["path"] = "/etc/passwd.csv",
    ["format"] = "csv",
});

// export.Success == false
// export.ErrorCode == "PERMISSION_DENIED"   (DataFrameErrorCodes.PermissionDenied)
// export.Details["path"] == the canonicalized "/etc/passwd.csv"
```

## Resource limits as a guard/defense against runaway queries

Beyond the filesystem gate, per-call resource limits in `DataFrameExecuteOptions` act as a
guard/defense against runaway or pathological requests:

- **`MaxMemoryBytes`** caps DuckDB's `memory_limit`; the backend configures a spill directory so a
  query exceeding the cap spills to disk rather than exhausting process memory.
- **`MaxExecutionTimeMs`** bounds wall-clock time; exceeding it cancels the in-flight statement and
  returns `CANCELLED`.

These bounds let a host accept untrusted requests without a single query monopolizing memory or
running unbounded. See [reliability.md](reliability.md) for the governance and cancellation contract.
