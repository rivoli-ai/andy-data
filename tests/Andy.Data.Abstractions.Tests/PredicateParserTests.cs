using Andy.Data;
using Andy.Data.Predicates;
using FluentAssertions;

namespace Andy.Data.Abstractions.Tests;

public class PredicateParserTests
{
    private static Dictionary<string, object?> N(params (string, object?)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("neq")]
    [InlineData("gt")]
    [InlineData("gte")]
    [InlineData("lt")]
    [InlineData("lte")]
    [InlineData("like")]
    [InlineData("ilike")]
    [InlineData("starts_with")]
    [InlineData("ends_with")]
    [InlineData("contains")]
    [InlineData("matches")]
    public void Parses_value_operators(string op)
    {
        var node = PredicateParser.Parse(N(("column", "amount"), ("op", op), ("value", 100)));
        node.Should().BeOfType<ConditionNode>()
            .Which.Should().Match<ConditionNode>(c => c.Column == "amount" && c.Op == op);
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("gt")]
    [InlineData("starts_with")]
    [InlineData("matches")]
    public void Parses_value_column_operators(string op)
    {
        var node = PredicateParser.Parse(N(("column", "amount"), ("op", op), ("value_column", "budget")));
        var c = node.Should().BeOfType<ConditionNode>().Which;
        c.Column.Should().Be("amount");
        c.Op.Should().Be(op);
        c.ValueColumn.Should().Be("budget");
        c.Value.Should().BeNull();
    }

    [Fact]
    public void Rejects_value_and_value_column_together()
    {
        var act = () => PredicateParser.Parse(
            N(("column", "amount"), ("op", "gt"), ("value", 1), ("value_column", "budget")));
        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidPredicate);
    }

    [Theory]
    [InlineData("is_null")]
    [InlineData("is_not_null")]
    public void Parses_null_operators_without_value(string op)
    {
        var node = (ConditionNode)PredicateParser.Parse(N(("column", "x"), ("op", op)));
        node.Op.Should().Be(op);
        node.Value.Should().BeNull();
    }

    [Fact]
    public void Parses_in_with_values()
    {
        var node = (ConditionNode)PredicateParser.Parse(
            N(("column", "region"), ("op", "in"), ("values", new object[] { "EMEA", "APAC" })));
        node.Values.Should().BeEquivalentTo(new object?[] { "EMEA", "APAC" });
    }

    [Fact]
    public void Parses_between_with_bounds()
    {
        var node = (ConditionNode)PredicateParser.Parse(
            N(("column", "amount"), ("op", "between"), ("low", 10), ("high", 20)));
        node.Low.Should().Be(10);
        node.High.Should().Be(20);
    }

    [Fact]
    public void Parses_nested_and_or_not()
    {
        var tree = N(
            ("op", "and"),
            ("conditions", new object[]
            {
                N(("column", "status"), ("op", "eq"), ("value", "completed")),
                N(("op", "or"), ("conditions", new object[]
                {
                    N(("column", "region"), ("op", "in"), ("values", new object[] { "EMEA" })),
                    N(("op", "not"), ("condition", N(("column", "amount"), ("op", "lt"), ("value", 0)))),
                })),
            }));

        var root = PredicateParser.Parse(tree).Should().BeOfType<LogicalNode>().Which;
        root.Op.Should().Be("and");
        root.Conditions.Should().HaveCount(2);
        root.Conditions[1].Should().BeOfType<LogicalNode>()
            .Which.Conditions[1].Should().BeOfType<NotNode>();
    }

    [Theory]
    [InlineData("eq")]   // missing value
    [InlineData("like")]
    [InlineData("matches")]
    public void Rejects_value_operator_without_value(string op)
    {
        var act = () => PredicateParser.Parse(N(("column", "x"), ("op", op)));
        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidPredicate);
    }

    [Fact]
    public void Rejects_unknown_operator()
    {
        var act = () => PredicateParser.Parse(N(("column", "x"), ("op", "matches_regex"), ("value", ".*")));
        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidPredicate);
    }

    [Fact]
    public void Rejects_in_with_empty_values()
    {
        var act = () => PredicateParser.Parse(N(("column", "x"), ("op", "in"), ("values", new object[0])));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_and_without_conditions()
    {
        var act = () => PredicateParser.Parse(N(("op", "and")));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_node_without_op_or_column()
    {
        var act = () => PredicateParser.Parse(N(("value", 1)));
        act.Should().Throw<DataFrameException>();
    }
}
