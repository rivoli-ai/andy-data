using Andy.Data.Predicates;

namespace Andy.Data.Expressions;

/// <summary>A validated node of a derived-column expression tree (see docs/operations.md#expression-trees).</summary>
public abstract record ExprNode;

/// <summary>A reference to an existing column.</summary>
public sealed record ColumnExpr(string Column) : ExprNode;

/// <summary>A literal constant.</summary>
public sealed record LiteralExpr(object? Value) : ExprNode;

/// <summary>An operator/function applied to argument expressions.</summary>
public sealed record FuncExpr(string Op, IReadOnlyList<ExprNode> Args) : ExprNode;

/// <summary>A cast of an argument expression to a target type.</summary>
public sealed record CastExpr(string ToType, ExprNode Arg) : ExprNode;

/// <summary>A safe cast that returns NULL on failure instead of aborting.</summary>
public sealed record TryCastExpr(string ToType, ExprNode Arg) : ExprNode;

/// <summary>A single WHEN branch inside a CASE expression.</summary>
public sealed record WhenClause(PredicateNode Predicate, ExprNode Then);

/// <summary>A searched CASE expression with one or more WHEN branches and an optional ELSE.</summary>
public sealed record CaseExpr(IReadOnlyList<WhenClause> Whens, ExprNode? Else) : ExprNode;

/// <summary>Accesses a named field of a STRUCT expression.</summary>
public sealed record StructFieldExpr(ExprNode Expr, string Field) : ExprNode;
