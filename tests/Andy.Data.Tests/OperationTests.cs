using Andy.Data;
using Andy.Data.Operations;
using FluentAssertions;

namespace Andy.Data.Tests;

/// <summary>
/// End-to-end coverage of every operation through the framework-independent <see cref="DataFrameEngine"/>.
/// The exhaustive behavioral suite still lives in andy-tools-dataframe until that repo is archived;
/// this proves each operation works through the new engine API.
/// </summary>
public sealed class OperationTests : IDisposable
{
    private readonly DataFrameEngine _engine = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "andyop_" + Guid.NewGuid().ToString("N"));

    public OperationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private DataFrameResponse R(string id, Dictionary<string, object?> p) => _engine.Execute(id, p);

    private string CsvFile(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void LoadSales(string id = "sales")
    {
        var path = CsvFile($"{id}.csv", "region,amount\nEMEA,100\nEMEA,40\nAPAC,200\n");
        R("dataframe_load_csv", new() { ["path"] = path, ["dataset_id"] = id }).Success.Should().BeTrue();
    }

    private static List<IReadOnlyDictionary<string, object?>> Rows(DataFrameResponse r) => r.PreviewRows.ToList();

    [Fact]
    public void Load_csv_schema_profile_preview_value_counts()
    {
        LoadSales();

        R("dataframe_schema", new() { ["dataset_id"] = "sales" })
            .Schema.Select(c => c.Name).Should().Contain(new[] { "region", "amount" });

        var profile = R("dataframe_profile", new() { ["dataset_id"] = "sales" });
        profile.Success.Should().BeTrue();
        var amount = Rows(profile).First(r => (string)r["column"]! == "amount");
        Convert.ToInt64(amount["count"]).Should().Be(3);
        Convert.ToDouble(amount["mean"]).Should().BeApproximately(113.333, 0.01);

        R("dataframe_preview", new() { ["dataset_id"] = "sales", ["mode"] = "head" })
            .RowCount.Should().Be(3L);

        var vc = R("dataframe_value_counts", new() { ["dataset_id"] = "sales", ["into"] = "vc", ["column"] = "region" });
        vc.Success.Should().BeTrue();
        Rows(vc)[0]["region"].Should().Be("EMEA");
        Convert.ToInt64(Rows(vc)[0]["count"]).Should().Be(2);
    }

    [Fact]
    public void Select_filter_with_column_rename_chain()
    {
        LoadSales();

        R("dataframe_select", new() { ["dataset_id"] = "sales", ["into"] = "s1", ["columns"] = new[] { "amount" } })
            .Schema.Select(c => c.Name).Should().ContainSingle().Which.Should().Be("amount");

        var f = R("dataframe_filter", new()
        {
            ["dataset_id"] = "sales", ["into"] = "f1",
            ["predicate"] = new Dictionary<string, object?> { ["column"] = "amount", ["op"] = "gte", ["value"] = 100 },
        });
        f.RowCount.Should().Be(2L);

        var wc = R("dataframe_with_column", new()
        {
            ["dataset_id"] = "sales", ["into"] = "w1", ["name"] = "doubled",
            ["expression"] = new Dictionary<string, object?>
            {
                ["op"] = "multiply",
                ["args"] = new object[]
                {
                    new Dictionary<string, object?> { ["column"] = "amount" },
                    new Dictionary<string, object?> { ["literal"] = 2 },
                },
            },
        });
        wc.Success.Should().BeTrue();
        Convert.ToInt64(Rows(wc).First(r => (string)r["region"]! == "APAC")["doubled"]).Should().Be(400);

        R("dataframe_rename", new()
        {
            ["dataset_id"] = "sales", ["into"] = "r1",
            ["columns"] = new Dictionary<string, object?> { ["amount"] = "amt" },
        }).Schema.Select(c => c.Name).Should().Contain("amt");
    }

    [Fact]
    public void Group_by_and_window()
    {
        LoadSales();

        var g = R("dataframe_group_by", new()
        {
            ["dataset_id"] = "sales", ["into"] = "g1",
            ["group_by"] = new[] { "region" },
            ["aggregations"] = new object[]
            {
                new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "sum", ["alias"] = "total" },
                new Dictionary<string, object?> { ["column"] = "amount", ["function"] = "stddev_pop", ["alias"] = "sd" },
            },
        });
        g.RowCount.Should().Be(2L);
        Convert.ToDouble(Rows(g).First(r => (string)r["region"]! == "EMEA")["total"]).Should().Be(140);

        var w = R("dataframe_window", new()
        {
            ["dataset_id"] = "sales", ["into"] = "w2",
            ["functions"] = new object[] { new Dictionary<string, object?> { ["function"] = "row_number", ["alias"] = "rn" } },
            ["order_by"] = new object[] { new Dictionary<string, object?> { ["column"] = "amount", ["direction"] = "desc" } },
        });
        w.Success.Should().BeTrue();
        Rows(w).Select(r => Convert.ToInt64(r["rn"])).Should().Contain(new[] { 1L, 2L, 3L });
    }

    [Fact]
    public void Join_pivot_unpivot()
    {
        var left = CsvFile("l.csv", "id,name\n1,Alice\n2,Bob\n");
        var right = CsvFile("r.csv", "id,score\n1,90\n2,80\n");
        R("dataframe_load_csv", new() { ["path"] = left, ["dataset_id"] = "l" });
        R("dataframe_load_csv", new() { ["path"] = right, ["dataset_id"] = "r" });

        var j = R("dataframe_join", new()
        {
            ["left"] = "l", ["right"] = "r", ["into"] = "j", ["how"] = "inner", ["on"] = new[] { "id" },
        });
        j.RowCount.Should().Be(2L);

        var pv = CsvFile("pv.csv", "region,product,amount\nEMEA,A,10\nEMEA,B,20\nAPAC,A,30\n");
        R("dataframe_load_csv", new() { ["path"] = pv, ["dataset_id"] = "pv" });
        var piv = R("dataframe_pivot", new()
        {
            ["dataset_id"] = "pv", ["into"] = "piv",
            ["index"] = new[] { "region" }, ["columns"] = "product", ["values"] = "amount", ["aggregation"] = "sum",
        });
        piv.Success.Should().BeTrue();
        piv.Schema.Select(c => c.Name).Should().Contain("region");

        var unp = R("dataframe_unpivot", new()
        {
            ["dataset_id"] = "piv", ["into"] = "unp",
            ["id_columns"] = new[] { "region" }, ["value_columns"] = new[] { "A", "B" },
        });
        unp.Success.Should().BeTrue();
    }

    [Fact]
    public void Unnest_explodes_a_list_column()
    {
        LoadSales();
        // Build a LIST column [amount, amount] then explode it: each row becomes two.
        R("dataframe_with_column", new()
        {
            ["dataset_id"] = "sales", ["into"] = "withlist", ["name"] = "lst",
            ["expression"] = new Dictionary<string, object?>
            {
                ["op"] = "array",
                ["args"] = new object[]
                {
                    new Dictionary<string, object?> { ["column"] = "amount" },
                    new Dictionary<string, object?> { ["column"] = "amount" },
                },
            },
        }).Success.Should().BeTrue();

        var u = R("dataframe_unnest", new() { ["dataset_id"] = "withlist", ["into"] = "exploded", ["column"] = "lst" });
        u.RowCount.Should().Be(6L); // 3 rows × 2 elements
    }

    [Fact]
    public void Sort_distinct_sample_union()
    {
        LoadSales();

        var s = R("dataframe_sort", new()
        {
            ["dataset_id"] = "sales", ["into"] = "sorted",
            ["by"] = new object[] { new Dictionary<string, object?> { ["column"] = "amount", ["direction"] = "asc" } },
        });
        Convert.ToInt64(Rows(s)[0]["amount"]).Should().Be(40);

        var d = R("dataframe_distinct", new() { ["dataset_id"] = "sales", ["into"] = "dist", ["columns"] = new[] { "region" } });
        d.RowCount.Should().Be(2L);

        var sm = R("dataframe_sample", new() { ["dataset_id"] = "sales", ["into"] = "samp", ["n"] = 2, ["seed"] = 42 });
        sm.RowCount.Should().Be(2L);

        var un = R("dataframe_union", new() { ["datasets"] = new[] { "sales", "sales" }, ["into"] = "u" });
        un.RowCount.Should().Be(6L);
    }

    [Fact]
    public void Fillna_scalar_and_ffill_and_dropna()
    {
        var path = CsvFile("g.csv", "g,t,v\nA,1,10\nA,2,\nA,3,30\n");
        R("dataframe_load_csv", new() { ["path"] = path, ["dataset_id"] = "g" }).Success.Should().BeTrue();

        var scalar = R("dataframe_fillna", new() { ["dataset_id"] = "g", ["into"] = "fs", ["value"] = "0" });
        scalar.Success.Should().BeTrue();
        Rows(scalar).Select(r => Convert.ToInt64(r["v"])).Should().Contain(new[] { 10L, 0L, 30L });

        var ffill = R("dataframe_fillna", new()
        {
            ["dataset_id"] = "g", ["into"] = "ff",
            ["method"] = "ffill", ["order_by"] = new[] { "t" }, ["partition_by"] = new[] { "g" },
        });
        ffill.Success.Should().BeTrue();
        var t2 = Rows(ffill).First(r => Convert.ToInt64(r["t"]) == 2);
        Convert.ToInt64(t2["v"]).Should().Be(10); // carried forward

        var drop = R("dataframe_dropna", new() { ["dataset_id"] = "g", ["into"] = "dn" });
        drop.RowCount.Should().Be(2L); // the null-v row is dropped
    }

    [Fact]
    public void Assert_reports_pass_and_fail()
    {
        var path = CsvFile("o.csv", "id,amount\n1,10\n1,-5\n");
        R("dataframe_load_csv", new() { ["path"] = path, ["dataset_id"] = "o" });

        var r = R("dataframe_assert", new()
        {
            ["dataset_id"] = "o",
            ["expectations"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "unique", ["column"] = "id" },         // fails (dup)
                new Dictionary<string, object?> { ["type"] = "in_range", ["column"] = "amount", ["min"] = 0 }, // fails (-5)
                new Dictionary<string, object?> { ["type"] = "row_count", ["min"] = 1 },             // passes
            },
        });
        r.Success.Should().BeTrue();
        var byType = Rows(r).ToDictionary(row => (string)row["expectation"]!, row => (bool)row["passed"]!);
        byType["unique"].Should().BeFalse();
        byType["in_range"].Should().BeFalse();
        byType["row_count"].Should().BeTrue();
        r.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Export_parquet_and_reload_and_load_json()
    {
        LoadSales();
        var outPath = Path.Combine(_dir, "out.parquet");
        R("dataframe_export", new() { ["dataset_id"] = "sales", ["path"] = outPath, ["format"] = "parquet" })
            .Success.Should().BeTrue();
        R("dataframe_load_parquet", new() { ["path"] = outPath, ["dataset_id"] = "back" }).RowCount.Should().Be(3L);

        var json = CsvFile("d.ndjson", "{\"id\":1,\"v\":10}\n{\"id\":2,\"v\":20}\n");
        R("dataframe_load_json", new() { ["path"] = json, ["dataset_id"] = "j" }).RowCount.Should().Be(2L);
    }

    [Fact]
    public void Export_delta_and_load_delta_roundtrip()
    {
        LoadSales();
        var target = Path.Combine(_dir, "delta_tbl");
        R("dataframe_export", new() { ["dataset_id"] = "sales", ["path"] = target, ["format"] = "delta", ["mode"] = "error" })
            .Success.Should().BeTrue();
        R("dataframe_load_delta", new() { ["path"] = target, ["dataset_id"] = "dl", ["version"] = 0 })
            .RowCount.Should().Be(3L);
    }

    [Fact]
    public void List_and_drop_manage_the_session()
    {
        LoadSales("a");
        LoadSales("b");
        var list = R("dataframe_list", new());
        Rows(list).Select(r => (string)r["dataset_id"]!).Should().Contain(new[] { "a", "b" });

        R("dataframe_drop", new() { ["dataset_id"] = "a" }).Success.Should().BeTrue();
        R("dataframe_schema", new() { ["dataset_id"] = "a" }).ErrorCode.Should().Be(DataFrameErrorCodes.DatasetNotFound);
    }

    [Fact]
    public void Validation_and_dispatch_errors_are_structured()
    {
        LoadSales();

        // Out-of-vocabulary allowed value → INVALID_ARGUMENT.
        R("dataframe_load_json", new() { ["path"] = CsvFile("x.json", "[]"), ["dataset_id"] = "x", ["format"] = "nope" })
            .ErrorCode.Should().Be(DataFrameErrorCodes.InvalidArgument);

        // Unknown column → COLUMN_NOT_FOUND.
        R("dataframe_value_counts", new() { ["dataset_id"] = "sales", ["column"] = "nope" })
            .ErrorCode.Should().Be(DataFrameErrorCodes.ColumnNotFound);

        // Unknown operation id throws (engine dispatch).
        var act = () => R("dataframe_nope", new() { ["dataset_id"] = "sales" });
        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidArgument);
    }
}
