using System.Diagnostics;
using System.Globalization;
using System.Text;
using Andy.Data;
using Andy.Data.Operations;

// Andy.Data micro-benchmark.
//
// Generates synthetic tabular data at several scales, then times each engine operation
// (median of N repetitions) and prints a Markdown report to stdout. This is a "quick
// benchmark" harness — a deterministic Stopwatch wall-clock measurement, not a statistically
// rigorous BenchmarkDotNet study. It exists to map the rough shape and limits of the library.
//
// Usage:
//   dotnet run -c Release -- [rowCounts] [repetitions]
//   dotnet run -c Release -- 100000,1000000,5000000 5
//
// Defaults: scales = 100000,1000000,5000000 ; repetitions = 5.

var scales = (args.Length > 0 ? args[0] : "100000,1000000,5000000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(s => long.Parse(s, CultureInfo.InvariantCulture))
    .ToArray();
var reps = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;

var workDir = Path.Combine(Path.GetTempPath(), "andy-data-bench");
Directory.CreateDirectory(workDir);

Console.Error.WriteLine($"Andy.Data benchmark — scales=[{string.Join(", ", scales)}] reps={reps}");
Console.Error.WriteLine($"work dir: {workDir}");
Console.Error.WriteLine($"runtime: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} / " +
                        $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} / " +
                        $".NET {Environment.Version} / {Environment.ProcessorCount} logical cores");

// Warm up: JIT + DuckDB native init + extension autoload, off the clock.
Warmup(workDir);

var report = new StringBuilder();
report.AppendLine("| rows | load_csv | load_parquet | filter | group_by | sort+limit | window | distinct | join | export_parquet |");
report.AppendLine("|-----:|---------:|-------------:|-------:|---------:|-----------:|-------:|---------:|-----:|---------------:|");

var rowReports = new List<(long rows, double csvMB, double pqMB, long peakMB)>();

foreach (var n in scales)
{
    Console.Error.WriteLine($"\n=== scale {n:N0} rows ===");
    var csvPath = Path.Combine(workDir, $"data_{n}.csv");
    var dimPath = Path.Combine(workDir, "dim_region.csv");
    var pqPath = Path.Combine(workDir, $"data_{n}.parquet");

    Console.Error.Write("generating CSV... ");
    GenerateCsv(csvPath, n);
    GenerateDimCsv(dimPath);
    var csvMB = new FileInfo(csvPath).Length / 1024.0 / 1024.0;
    Console.Error.WriteLine($"{csvMB:N1} MB");

    using var engine = new DataFrameEngine();

    // Load the dim table once (tiny, shared across join reps).
    Check(engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
        { ["path"] = dimPath, ["dataset_id"] = "dim" }));

    double loadCsv = Bench(reps, () =>
        Check(engine.Execute("dataframe_load_csv", new Dictionary<string, object?>
            { ["path"] = csvPath, ["dataset_id"] = "t" })));

    double exportPq = Bench(reps, () =>
        Check(engine.Execute("dataframe_export", new Dictionary<string, object?>
            { ["dataset_id"] = "t", ["path"] = pqPath, ["format"] = "parquet", ["mode"] = "overwrite" })));
    var pqMB = new FileInfo(pqPath).Length / 1024.0 / 1024.0;

    double loadPq = Bench(reps, () =>
        Check(engine.Execute("dataframe_load_parquet", new Dictionary<string, object?>
            { ["path"] = pqPath, ["dataset_id"] = "tp" })));

    double filter = Bench(reps, () =>
        Check(engine.Execute("dataframe_filter", new Dictionary<string, object?>
        {
            ["dataset_id"] = "tp", ["into"] = "f",
            ["predicate"] = new Dictionary<string, object?> { ["column"] = "amount", ["op"] = "gt", ["value"] = 500.0 },
        })));

    double groupBy = Bench(reps, () =>
        Check(engine.Execute("dataframe_group_by", new Dictionary<string, object?>
        {
            ["dataset_id"] = "tp", ["into"] = "g", ["group_by"] = new[] { "region" },
            ["aggregations"] = new object[]
            {
                new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "total" },
                new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "avg", ["alias"] = "avg_amount" },
                new Dictionary<string, object?> { ["column"] = "*", ["function"] = "count", ["alias"] = "n" },
            },
        })));

    double sort = Bench(reps, () =>
        Check(engine.Execute("dataframe_sort", new Dictionary<string, object?>
        {
            ["dataset_id"] = "tp", ["into"] = "s",
            ["by"] = new object[] { new Dictionary<string, object?> { ["column"] = "amount", ["direction"] = "desc" } },
            ["limit"] = 100,
        })));

    double window = Bench(reps, () =>
        Check(engine.Execute("dataframe_window", new Dictionary<string, object?>
        {
            ["dataset_id"] = "tp", ["into"] = "w",
            ["functions"] = new object[] { new Dictionary<string, object?> { ["function"] = "row_number", ["alias"] = "rn" } },
            ["partition_by"] = new[] { "region" },
            ["order_by"] = new object[] { new Dictionary<string, object?> { ["column"] = "amount", ["direction"] = "desc" } },
        })));

    double distinct = Bench(reps, () =>
        Check(engine.Execute("dataframe_distinct", new Dictionary<string, object?>
        {
            ["dataset_id"] = "tp", ["into"] = "d", ["columns"] = new[] { "region", "category" },
        })));

    double join = Bench(reps, () =>
        Check(engine.Execute("dataframe_join", new Dictionary<string, object?>
        {
            ["left"] = "tp", ["right"] = "dim", ["into"] = "j", ["how"] = "inner", ["on"] = new[] { "region" },
        })));

    // PeakWorkingSet64 is unreliable on macOS (often 0); sample the current resident set instead.
    var proc = Process.GetCurrentProcess();
    proc.Refresh();
    var peakMB = proc.WorkingSet64 / 1024 / 1024;
    rowReports.Add((n, csvMB, pqMB, peakMB));

    report.AppendLine(
        $"| {n:N0} | {loadCsv:N0} | {loadPq:N0} | {filter:N0} | {groupBy:N0} | {sort:N0} | " +
        $"{window:N0} | {distinct:N0} | {join:N0} | {exportPq:N0} |");
}

Console.WriteLine();
Console.WriteLine("## Timings (median ms over " + reps + " runs)");
Console.WriteLine();
Console.Write(report.ToString());
Console.WriteLine();
Console.WriteLine("## Dataset sizes");
Console.WriteLine();
Console.WriteLine("| rows | CSV on disk | Parquet on disk | process RSS after scale |");
Console.WriteLine("|-----:|------------:|----------------:|------------------------:|");
foreach (var r in rowReports)
{
    Console.WriteLine($"| {r.rows:N0} | {r.csvMB:N1} MB | {r.pqMB:N1} MB | {r.peakMB:N0} MB |");
}

// ---- helpers ----------------------------------------------------------------

static double Bench(int reps, Action body)
{
    var times = new double[reps];
    for (var i = 0; i < reps; i++)
    {
        var sw = Stopwatch.StartNew();
        body();
        sw.Stop();
        times[i] = sw.Elapsed.TotalMilliseconds;
    }
    Array.Sort(times);
    return times[times.Length / 2]; // median
}

static DataFrameResponse Check(DataFrameResponse r)
{
    if (!r.Success)
    {
        throw new InvalidOperationException($"operation failed: {r.ErrorCode}: {r.Message}");
    }
    return r;
}

static void Warmup(string dir)
{
    var p = Path.Combine(dir, "warmup.csv");
    GenerateCsv(p, 1000);
    using var engine = new DataFrameEngine();
    engine.Execute("dataframe_load_csv", new Dictionary<string, object?> { ["path"] = p, ["dataset_id"] = "w" });
    engine.Execute("dataframe_group_by", new Dictionary<string, object?>
    {
        ["dataset_id"] = "w", ["into"] = "wg", ["group_by"] = new[] { "region" },
        ["aggregations"] = new object[] { new Dictionary<string, object?> { ["column"] = "*", ["function"] = "count", ["alias"] = "n" } },
    });
    var pq = Path.Combine(dir, "warmup.parquet");
    engine.Execute("dataframe_export", new Dictionary<string, object?>
        { ["dataset_id"] = "w", ["path"] = pq, ["format"] = "parquet", ["mode"] = "overwrite" });
    engine.Execute("dataframe_load_parquet", new Dictionary<string, object?> { ["path"] = pq, ["dataset_id"] = "wp" });
}

// Deterministic synthetic data: id, region (5 distinct), category (20 distinct), amount, qty, ts.
static void GenerateCsv(string path, long rows)
{
    using var w = new StreamWriter(path, append: false, Encoding.ASCII, 1 << 20);
    w.WriteLine("id,region,category,amount,qty,ts");
    ulong state = 0x9E3779B97F4A7C15UL; // fixed seed → reproducible
    var baseDate = new DateTime(2020, 1, 1);
    for (long i = 0; i < rows; i++)
    {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        var r = state >> 33;
        var region = r % 5;
        var category = (r / 5) % 20;
        var amount = (r % 100000) / 100.0;           // 0.00 .. 999.99
        var qty = (int)(r % 100) + 1;                 // 1 .. 100
        var day = (int)(r % 1000);
        var ts = baseDate.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        w.Write(i); w.Write(",R"); w.Write(region); w.Write(",C");
        if (category < 10) w.Write('0');
        w.Write(category); w.Write(',');
        w.Write(amount.ToString("0.00", CultureInfo.InvariantCulture)); w.Write(',');
        w.Write(qty); w.Write(','); w.WriteLine(ts);
    }
}

static void GenerateDimCsv(string path)
{
    using var w = new StreamWriter(path, append: false, Encoding.ASCII);
    w.WriteLine("region,region_name");
    for (var i = 0; i < 5; i++)
    {
        w.WriteLine($"R{i},Region {i}");
    }
}
