using Andy.Data;
using FluentAssertions;

namespace Andy.Data.Abstractions.Tests;

public class DatasetCatalogTests
{
    private static DatasetEntry Entry(string id, string type = "VARCHAR") =>
        new(id, new[] { new ColumnSchema("c", type) }, $"load:{id}.csv", 3);

    [Fact]
    public void Register_then_contains_get_schema()
    {
        var cat = new InMemoryDatasetCatalog();
        cat.Register(Entry("sales"));

        cat.Contains("sales").Should().BeTrue();
        cat.Get("sales")!.Source.Should().Be("load:sales.csv");
        cat.TryGetSchema("sales").Should().ContainSingle().Which.Name.Should().Be("c");
    }

    [Fact]
    public void Register_replaces_existing_entry()
    {
        var cat = new InMemoryDatasetCatalog();
        cat.Register(Entry("d", "VARCHAR"));
        cat.Register(Entry("d", "BIGINT"));
        cat.Get("d")!.Schema[0].Type.Should().Be("BIGINT");
        cat.List().Should().HaveCount(1);
    }

    [Fact]
    public void List_returns_all_entries()
    {
        var cat = new InMemoryDatasetCatalog();
        cat.Register(Entry("a"));
        cat.Register(Entry("b"));
        cat.List().Select(e => e.DatasetId).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public void Drop_removes_and_reports_existence()
    {
        var cat = new InMemoryDatasetCatalog();
        cat.Register(Entry("x"));
        cat.Drop("x").Should().BeTrue();
        cat.Drop("x").Should().BeFalse();
        cat.Contains("x").Should().BeFalse();
    }

    [Fact]
    public void Unknown_dataset_is_null()
    {
        var cat = new InMemoryDatasetCatalog();
        cat.Get("nope").Should().BeNull();
        cat.TryGetSchema("nope").Should().BeNull();
    }

    [Fact]
    public void Register_empty_id_throws()
    {
        var cat = new InMemoryDatasetCatalog();
        var act = () => cat.Register(new DatasetEntry(" ", Array.Empty<ColumnSchema>(), "x"));
        act.Should().Throw<DataFrameException>();
    }
}
