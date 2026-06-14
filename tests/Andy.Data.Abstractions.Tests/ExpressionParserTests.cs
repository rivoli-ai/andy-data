using Andy.Data;
using Andy.Data.Expressions;
using FluentAssertions;

namespace Andy.Data.Abstractions.Tests;

public class ExpressionParserTests
{
    private static Dictionary<string, object?> N(params (string, object?)[] kv)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Fact]
    public void Parses_column_and_literal_leaves()
    {
        ExpressionParser.Parse(N(("column", "amount"))).Should().BeOfType<ColumnExpr>()
            .Which.Column.Should().Be("amount");
        ExpressionParser.Parse(N(("literal", 42))).Should().BeOfType<LiteralExpr>()
            .Which.Value.Should().Be(42);
    }

    [Fact]
    public void Parses_nested_arithmetic()
    {
        // amount - discount
        var expr = ExpressionParser.Parse(N(
            ("op", "subtract"),
            ("args", new object[] { N(("column", "amount")), N(("column", "discount")) })));

        var f = expr.Should().BeOfType<FuncExpr>().Which;
        f.Op.Should().Be("subtract");
        f.Args.Should().HaveCount(2);
        f.Args[0].Should().BeOfType<ColumnExpr>();
    }

    [Fact]
    public void Parses_cast()
    {
        var expr = ExpressionParser.Parse(N(
            ("op", "cast"), ("to", "DOUBLE"),
            ("args", new object[] { N(("column", "x")) })));
        expr.Should().BeOfType<CastExpr>().Which.ToType.Should().Be("DOUBLE");
    }

    [Fact]
    public void Parses_try_cast()
    {
        var expr = ExpressionParser.Parse(N(
            ("op", "try_cast"), ("to", "INTEGER"),
            ("args", new object[] { N(("column", "x")) })));
        var tc = expr.Should().BeOfType<TryCastExpr>().Which;
        tc.ToType.Should().Be("INTEGER");
        tc.Arg.Should().BeOfType<ColumnExpr>();
    }

    [Fact]
    public void Parses_case_expression()
    {
        var expr = ExpressionParser.Parse(N(
            ("op", "case"),
            ("when", new object[]
            {
                N(
                    ("predicate", N(("column", "amount"), ("op", "gte"), ("value", 100))),
                    ("then", N(("literal", "high")))),
                N(
                    ("predicate", N(("column", "amount"), ("op", "gte"), ("value", 10))),
                    ("then", N(("literal", "medium")))),
            }),
            ("else", N(("literal", "low")))));

        var c = expr.Should().BeOfType<CaseExpr>().Which;
        c.Whens.Should().HaveCount(2);
        c.Else.Should().BeOfType<LiteralExpr>();
    }

    [Fact]
    public void Parses_case_without_else()
    {
        var expr = ExpressionParser.Parse(N(
            ("op", "case"),
            ("when", new object[]
            {
                N(
                    ("predicate", N(("column", "amount"), ("op", "gte"), ("value", 100))),
                    ("then", N(("literal", "high")))),
            })));

        var c = expr.Should().BeOfType<CaseExpr>().Which;
        c.Whens.Should().HaveCount(1);
        c.Else.Should().BeNull();
    }

    [Fact]
    public void Rejects_case_with_non_object_else()
    {
        var act = () => ExpressionParser.Parse(N(
            ("op", "case"),
            ("when", new object[]
            {
                N(
                    ("predicate", N(("column", "amount"), ("op", "gte"), ("value", 100))),
                    ("then", N(("literal", "high")))),
            }),
            ("else", "low")));

        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidPredicate);
    }

    [Theory]
    [InlineData("round", 1)]
    [InlineData("abs", 1)]
    [InlineData("floor", 1)]
    [InlineData("ceil", 1)]
    [InlineData("power", 2)]
    [InlineData("ln", 1)]
    [InlineData("replace", 3)]
    [InlineData("split_part", 3)]
    [InlineData("lpad", 2)]
    [InlineData("rpad", 2)]
    [InlineData("regexp_replace", 3)]
    [InlineData("regexp_extract", 2)]
    [InlineData("regexp_matches", 2)]
    [InlineData("strptime", 2)]
    [InlineData("date_add", 3)]
    [InlineData("hash", 1)]
    public void Parses_new_functions(string op, int arity)
    {
        var args = Enumerable.Range(0, arity)
            .Select(_ => N(("column", "x")))
            .Cast<object>()
            .ToArray();

        if (op == "strptime")
        {
            args[^1] = N(("literal", "%Y-%m-%d"));
        }
        else if (op == "date_add")
        {
            args[0] = N(("literal", "day"));
            args[1] = N(("literal", 1));
        }

        var expr = ExpressionParser.Parse(N(("op", op), ("args", args)));
        expr.Should().BeOfType<FuncExpr>().Which.Op.Should().Be(op);
    }

    [Fact]
    public void Rejects_unknown_function()
    {
        var act = () => ExpressionParser.Parse(N(("op", "unknown_op"),
            ("args", new object[] { N(("column", "x")) })));
        act.Should().Throw<DataFrameException>().Which.ErrorCode.Should().Be(DataFrameErrorCodes.InvalidPredicate);
    }

    [Fact]
    public void Rejects_wrong_arity()
    {
        // subtract requires exactly 2 args
        var act = () => ExpressionParser.Parse(N(("op", "subtract"),
            ("args", new object[] { N(("column", "x")) })));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_cast_without_target_type()
    {
        var act = () => ExpressionParser.Parse(N(("op", "cast"),
            ("args", new object[] { N(("column", "x")) })));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_try_cast_without_target_type()
    {
        var act = () => ExpressionParser.Parse(N(("op", "try_cast"),
            ("args", new object[] { N(("column", "x")) })));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_case_without_when()
    {
        var act = () => ExpressionParser.Parse(N(("op", "case")));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_case_with_empty_when()
    {
        var act = () => ExpressionParser.Parse(N(("op", "case"), ("when", Array.Empty<object?>())));
        act.Should().Throw<DataFrameException>();
    }

    [Fact]
    public void Rejects_node_without_column_literal_or_op()
    {
        var act = () => ExpressionParser.Parse(N(("foo", "bar")));
        act.Should().Throw<DataFrameException>();
    }
}
