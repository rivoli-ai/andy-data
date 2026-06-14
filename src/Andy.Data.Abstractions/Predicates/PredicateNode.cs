namespace Andy.Data.Predicates;

/// <summary>A validated node of a filter predicate tree (see docs/operations.md#predicate-trees).</summary>
public abstract record PredicateNode;

/// <summary>A leaf comparison/test on a single column.</summary>
/// <param name="Column">Column name.</param>
/// <param name="Op">One of the comparison/set/range/null/text operators.</param>
/// <param name="Value">Operand for comparison/text ops.</param>
/// <param name="ValueColumn">Column operand for comparison/text ops in place of a literal value.</param>
/// <param name="Values">Operands for <c>in</c>.</param>
/// <param name="Low">Lower bound for <c>between</c>.</param>
/// <param name="High">Upper bound for <c>between</c>.</param>
public sealed record ConditionNode(
    string Column,
    string Op,
    object? Value = null,
    string? ValueColumn = null,
    IReadOnlyList<object?>? Values = null,
    object? Low = null,
    object? High = null) : PredicateNode;

/// <summary>An n-ary logical combination (<c>and</c>/<c>or</c>).</summary>
public sealed record LogicalNode(string Op, IReadOnlyList<PredicateNode> Conditions) : PredicateNode;

/// <summary>Logical negation of a single child node.</summary>
public sealed record NotNode(PredicateNode Condition) : PredicateNode;
