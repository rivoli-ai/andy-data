using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Andy.Data.Operations;

/// <summary>
/// Validates a parameters dictionary against a declared <see cref="DataFrameParam"/> schema before an
/// operation body runs. This replaces the tool-framework's parameter validation so the engine is
/// self-contained. Required/type/range/pattern violations map to <see cref="DataFrameErrorCodes.InvalidType"/>;
/// out-of-vocabulary values (closed <see cref="DataFrameParam.AllowedValues"/>) map to
/// <see cref="DataFrameErrorCodes.InvalidArgument"/> — matching the documented error contract.
/// </summary>
public static class DataFrameParameterValidator
{
    public static void Validate(
        IReadOnlyDictionary<string, object?> parameters, IReadOnlyList<DataFrameParam> schema)
    {
        foreach (var p in schema)
        {
            var present = parameters.TryGetValue(p.Name, out var value) && value is not null;

            if (!present)
            {
                if (p.Required)
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Required parameter '{p.Name}' is missing.", p.Name);
                }

                continue;
            }

            // Closed-vocabulary check first (out-of-set is INVALID_ARGUMENT).
            if (p.AllowedValues is { Count: > 0 })
            {
                var s = value!.ToString();
                if (!p.AllowedValues.Any(a => string.Equals(a?.ToString(), s, StringComparison.Ordinal)))
                {
                    var allowed = string.Join(", ", p.AllowedValues.Select(a => a?.ToString()));
                    throw Invalid(DataFrameErrorCodes.InvalidArgument,
                        $"Parameter '{p.Name}' must be one of: {allowed}.", p.Name);
                }
            }

            ValidateType(p, value!);
            ValidateRange(p, value!);

            if (p.Pattern is not null && value is string str && !Regex.IsMatch(str, p.Pattern))
            {
                throw Invalid(DataFrameErrorCodes.InvalidType,
                    $"Parameter '{p.Name}' does not match the required pattern.", p.Name);
            }
        }
    }

    private static void ValidateType(DataFrameParam p, object value)
    {
        switch (p.Type)
        {
            case "integer":
                if (value is bool || !TryToLong(value, out _))
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be an integer.", p.Name);
                }

                break;

            case "number":
                if (value is bool || !TryToDouble(value, out _))
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be a number.", p.Name);
                }

                break;

            case "boolean":
                if (value is not bool &&
                    !(value is string bs && (bool.TryParse(bs, out _))) &&
                    !(TryToLong(value, out var bl) && (bl == 0 || bl == 1)))
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be a boolean.", p.Name);
                }

                break;

            case "array":
                if (value is string || value is IDictionary || value is not IEnumerable)
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be an array.", p.Name);
                }

                break;

            case "object":
                if (value is not IDictionary && !IsReadOnlyDictionary(value))
                {
                    throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be an object.", p.Name);
                }

                break;

            // "string" and unknown types: accept any non-null value (it has a string form).
        }
    }

    private static void ValidateRange(DataFrameParam p, object value)
    {
        if ((p.MinValue is null && p.MaxValue is null) || !TryToDouble(value, out var v))
        {
            return;
        }

        if (p.MinValue is not null && TryToDouble(p.MinValue, out var min) && v < min)
        {
            throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be >= {min}.", p.Name);
        }

        if (p.MaxValue is not null && TryToDouble(p.MaxValue, out var max) && v > max)
        {
            throw Invalid(DataFrameErrorCodes.InvalidType, $"Parameter '{p.Name}' must be <= {max}.", p.Name);
        }
    }

    private static bool IsReadOnlyDictionary(object value)
    {
        var t = value.GetType();
        return t.GetInterfaces().Any(i => i.IsGenericType &&
            i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
    }

    private static bool TryToLong(object value, out long result)
    {
        try { result = Convert.ToInt64(value, CultureInfo.InvariantCulture); return true; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { result = 0; return false; }
    }

    private static bool TryToDouble(object value, out double result)
    {
        try { result = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true; }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { result = 0; return false; }
    }

    private static DataFrameException Invalid(string code, string message, string param) =>
        new(code, message, new Dictionary<string, object?> { ["parameter"] = param });
}
