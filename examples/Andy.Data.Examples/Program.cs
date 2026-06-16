// Andy.Data — runnable examples.
//
// A small scenario suite that drives the framework-independent dataframe engine through its
// public surface only: construct a DataFrameEngine, call engine.Execute(id, parameters, options),
// and read the typed DataFrameResponse. No tool framework, no DI, no SQL.
//
//   dotnet run                 # run every scenario
//   dotnet run -- list         # list scenario names
//   dotnet run -- <name> ...   # run only the named scenario(s)

using Andy.Data;
using Andy.Data.Operations;

var scenarios = new (string Name, string Description, Action Run)[]
{
    ("load-inspect",     "Load a CSV, then read its schema and a bounded preview.",        Scenarios.LoadInspect),
    ("filter-aggregate", "Filter rows, group-by + sum, then sort the result top-down.",    Scenarios.FilterAggregate),
    ("derived-column",   "Add a computed column from a structured expression tree.",        Scenarios.DerivedColumn),
    ("join",             "Join two datasets on a shared key.",                              Scenarios.Join),
    ("export-reload",    "Export a dataset to Parquet, then load it back.",                 Scenarios.ExportReload),
    ("error-envelope",   "Trigger failures and read the stable error envelope.",            Scenarios.ErrorEnvelope),
    ("path-policy",      "Gate filesystem access with an IPathPolicy sandbox.",             Scenarios.PathPolicy),
};

if (args.Length == 1 && args[0] == "list")
{
    Console.WriteLine("Available scenarios:\n");
    foreach (var s in scenarios)
    {
        Console.WriteLine($"  {s.Name,-18} {s.Description}");
    }

    return 0;
}

var selected = args.Length == 0
    ? scenarios
    : scenarios.Where(s => args.Contains(s.Name)).ToArray();

if (selected.Length == 0)
{
    Console.Error.WriteLine($"No matching scenario. Try: dotnet run -- list");
    return 1;
}

foreach (var s in selected)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"  {s.Name} — {s.Description}");
    Console.WriteLine(new string('=', 78));
    s.Run();
}

Console.WriteLine();
return 0;

/// <summary>The example scenarios. Each constructs its own engine and prints what it does.</summary>
internal static class Scenarios
{
    // ----- scenario bodies ---------------------------------------------------

    public static void LoadInspect()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();

        Load(engine, work.SalesCsv, "sales");

        var schema = engine.Execute("dataframe_schema", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
        });
        Console.WriteLine("Schema:");
        foreach (var col in schema.Schema)
        {
            Console.WriteLine($"  {col.Name,-12} {col.Type}{(col.Nullable ? "" : " NOT NULL")}");
        }

        var preview = engine.Execute("dataframe_preview", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["rows"] = 3,
        });
        Print(preview);
    }

    public static void FilterAggregate()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();
        Load(engine, work.SalesCsv, "sales");

        // Keep completed orders only.
        Print(engine.Execute("dataframe_filter", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["into"] = "completed",
            ["predicate"] = new Dictionary<string, object?>
            {
                ["column"] = "status", ["op"] = "eq", ["value"] = "completed",
            },
        }));

        // Revenue per region.
        Print(engine.Execute("dataframe_group_by", new Dictionary<string, object?>
        {
            ["dataset_id"] = "completed",
            ["into"] = "by_region",
            ["group_by"] = new[] { "region" },
            ["aggregations"] = new object[]
            {
                new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "revenue" },
                new Dictionary<string, object?> { ["column"] = "*", ["function"] = "count", ["alias"] = "orders" },
            },
        }));

        // Highest-revenue regions first.
        Print(engine.Execute("dataframe_sort", new Dictionary<string, object?>
        {
            ["dataset_id"] = "by_region",
            ["by"] = new object[]
            {
                new Dictionary<string, object?> { ["column"] = "revenue", ["direction"] = "desc" },
            },
        }));
    }

    public static void DerivedColumn()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();
        Load(engine, work.SalesCsv, "sales");

        // net = amount * 0.9 (a 10% discount), via a structured expression tree — no SQL.
        Print(engine.Execute("dataframe_with_column", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["name"] = "net",
            ["expression"] = new Dictionary<string, object?>
            {
                ["op"] = "multiply",
                ["args"] = new object[]
                {
                    new Dictionary<string, object?> { ["column"] = "amount" },
                    new Dictionary<string, object?> { ["literal"] = 0.9 },
                },
            },
        }));
    }

    public static void Join()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();
        Load(engine, work.SalesCsv, "sales");
        Load(engine, work.RegionsCsv, "regions");

        Print(engine.Execute("dataframe_join", new Dictionary<string, object?>
        {
            ["left"] = "sales",
            ["right"] = "regions",
            ["into"] = "enriched",
            ["how"] = "inner",
            ["on"] = new[] { "region" },
        }));
    }

    public static void ExportReload()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();
        Load(engine, work.SalesCsv, "sales");

        var outPath = Path.Combine(work.Root, "sales.parquet");
        Print(engine.Execute("dataframe_export", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["path"] = outPath,
            ["format"] = "parquet",
            ["mode"] = "overwrite",
        }));

        Console.WriteLine($"Reloading {Path.GetFileName(outPath)} ...");
        Print(engine.Execute("dataframe_load_parquet", new Dictionary<string, object?>
        {
            ["path"] = outPath,
            ["dataset_id"] = "roundtrip",
        }));
    }

    public static void ErrorEnvelope()
    {
        using var work = new Workspace();
        using var engine = new DataFrameEngine();
        Load(engine, work.SalesCsv, "sales");

        Console.WriteLine("-- unknown dataset --");
        Print(engine.Execute("dataframe_schema", new Dictionary<string, object?>
        {
            ["dataset_id"] = "does_not_exist",
        }));

        Console.WriteLine("-- unknown column --");
        Print(engine.Execute("dataframe_filter", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["predicate"] = new Dictionary<string, object?>
            {
                ["column"] = "nope", ["op"] = "gt", ["value"] = 0,
            },
        }));
    }

    public static void PathPolicy()
    {
        using var work = new Workspace();

        // Reads and writes are confined to the sandbox root; anything else is denied.
        var policy = new SandboxPathPolicy(work.Root);
        using var engine = new DataFrameEngine(policy);
        Load(engine, work.SalesCsv, "sales");

        Console.WriteLine("-- export inside the sandbox (allowed) --");
        Print(engine.Execute("dataframe_export", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["path"] = Path.Combine(work.Root, "inside.csv"),
            ["format"] = "csv",
            ["mode"] = "overwrite",
        }));

        Console.WriteLine("-- export outside the sandbox (denied) --");
        Print(engine.Execute("dataframe_export", new Dictionary<string, object?>
        {
            ["dataset_id"] = "sales",
            ["path"] = Path.Combine(Path.GetTempPath(), "outside.csv"),
            ["format"] = "csv",
            ["mode"] = "overwrite",
        }));
    }

    // ----- helpers -----------------------------------------------------------

    private static void Load(DataFrameEngine engine, string csvPath, string datasetId) =>
        Print(engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
        {
            ["path"] = csvPath,
            ["dataset_id"] = datasetId,
        }));

    /// <summary>Prints a response the same way for success and failure (one envelope, one reader).</summary>
    private static void Print(DataFrameResponse r)
    {
        if (!r.Success)
        {
            Console.WriteLine($"  [{r.ErrorCode}] {r.Message}");
            return;
        }

        var cols = string.Join(", ", r.Schema.Select(c => c.Name));
        Console.WriteLine($"  ok  dataset='{r.DatasetId}'  rows={r.RowCount}  columns=[{cols}]");

        foreach (var row in r.PreviewRows)
        {
            var cells = r.Schema.Select(c => $"{c.Name}={Format(row.TryGetValue(c.Name, out var v) ? v : null)}");
            Console.WriteLine($"    · {string.Join("  ", cells)}");
        }

        if (r.PreviewTruncated)
        {
            Console.WriteLine($"    … preview bounded; export for the full {r.RowCount} rows");
        }

        foreach (var w in r.Warnings)
        {
            Console.WriteLine($"    ! {w}");
        }
    }

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string s => s,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>A temporary directory seeded with the sample CSV files; deletes itself on dispose.</summary>
internal sealed class Workspace : IDisposable
{
    public string Root { get; }
    public string SalesCsv { get; }
    public string RegionsCsv { get; }

    public Workspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "andy-data-examples-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        SalesCsv = Path.Combine(Root, "sales.csv");
        File.WriteAllText(SalesCsv,
            "order_id,region,status,amount,quantity\n" +
            "1,west,completed,120.50,2\n" +
            "2,east,completed,80.00,1\n" +
            "3,west,cancelled,45.00,1\n" +
            "4,north,completed,210.25,4\n" +
            "5,east,completed,60.00,1\n" +
            "6,west,completed,30.75,1\n");

        RegionsCsv = Path.Combine(Root, "regions.csv");
        File.WriteAllText(RegionsCsv,
            "region,manager\n" +
            "west,Alice\n" +
            "east,Bao\n" +
            "north,Chitra\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp workspace.
        }
    }
}

/// <summary>An <see cref="IPathPolicy"/> that confines all reads and writes to a single root directory.</summary>
internal sealed class SandboxPathPolicy : IPathPolicy
{
    private readonly string _root;

    public SandboxPathPolicy(string root) => _root = RealPath(root);

    public bool CanRead(string path) => IsInsideRoot(path);

    public bool CanWrite(string path) => IsInsideRoot(path);

    private bool IsInsideRoot(string path)
    {
        var full = RealPath(path);
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || string.Equals(full, _root, StringComparison.Ordinal);
    }

    /// <summary>
    /// Canonicalizes a path, resolving symlinked ancestor directories (e.g. macOS <c>/var</c> →
    /// <c>/private/var</c>) — the engine does the same before consulting the policy, so the sandbox
    /// root and the incoming path must be compared on the same resolved form.
    /// </summary>
    private static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return full;
        }

        var result = "";
        foreach (var segment in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = result + "/" + segment;

            // Only resolve segments that exist — a not-yet-created export target has none, and
            // ResolveLinkTarget throws on a missing path. (This mirrors the engine's own canonicalizer.)
            if (Directory.Exists(next) || File.Exists(next))
            {
                var link = Directory.ResolveLinkTarget(next, returnFinalTarget: true);
                result = link?.FullName.TrimEnd('/') ?? next;
            }
            else
            {
                result = next;
            }
        }

        return result.Length == 0 ? "/" : result;
    }
}
