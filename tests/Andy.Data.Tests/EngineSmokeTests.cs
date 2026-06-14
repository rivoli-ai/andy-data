using Andy.Data;
using Andy.Data.Backend;
using Andy.Data.Predicates;
using Andy.Data.Sql;
using FluentAssertions;

namespace Andy.Data.Tests;

/// <summary>
/// End-to-end smoke tests for the framework-independent engine: the DuckDB backend, the predicate
/// parser, and the SQL renderer working together without any tool-framework dependency.
/// </summary>
public sealed class EngineSmokeTests : IDisposable
{
    private readonly DuckDbBackend _backend = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "andydata_" + Guid.NewGuid().ToString("N"));

    public EngineSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCsv(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Load_filter_count_preview_roundtrips_through_the_engine()
    {
        var csv = WriteCsv("sales.csv", "region,amount\nEMEA,100\nEMEA,40\nAPAC,200\n");
        var schema = _backend.RegisterCsv("sales", new CsvLoadOptions(csv));
        schema.Select(c => c.Name).Should().Contain(new[] { "region", "amount" });
        _backend.CountRows("sales").Should().Be(3L);

        // Parse a structured predicate, render it to SQL via the closed-vocabulary renderer, and
        // run it through the backend — exercising the abstractions parser + Sql renderer + engine.
        var predicate = PredicateParser.Parse(new Dictionary<string, object?>
        {
            ["column"] = "amount", ["op"] = "gte", ["value"] = 100,
        });
        var where = PredicateSqlRenderer.Render(predicate, schema);
        _backend.Derive("big", "sales", "*", whereClause: where);

        _backend.CountRows("big").Should().Be(2L); // amounts 100 and 200
        var preview = _backend.Preview("big", "head", 10, null, 2);
        preview.Should().HaveCount(2);
        preview.Should().OnlyContain(r => Convert.ToInt64(r["amount"]) >= 100);
    }

    [Fact]
    public void Group_by_aggregates_through_the_backend()
    {
        var csv = WriteCsv("g.csv", "region,amount\nEMEA,100\nEMEA,40\nAPAC,200\n");
        _backend.RegisterCsv("g", new CsvLoadOptions(csv));

        _backend.Derive("by_region", "g", "\"region\", sum(\"amount\") AS \"total\"",
            groupByClause: "\"region\"", orderByClause: "\"region\" ASC");

        var rows = _backend.Preview("by_region", "head", 10, null, 2);
        rows.Should().HaveCount(2);
        var apac = rows.First(r => (string)r["region"]! == "APAC");
        Convert.ToDouble(apac["total"]).Should().Be(200);
    }

    [Fact]
    public void Parquet_export_and_reload_roundtrips()
    {
        var csv = WriteCsv("p.csv", "id,v\n1,10\n2,20\n3,30\n");
        _backend.RegisterCsv("p", new CsvLoadOptions(csv));

        var outPath = Path.Combine(_dir, "out.parquet");
        _backend.Export("p", outPath, "parquet", partitionByQuoted: null, compression: null, overwrite: false);

        _backend.RegisterParquet("p_back", outPath);
        _backend.CountRows("p_back").Should().Be(3L);
    }
}
