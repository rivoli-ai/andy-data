using System.Globalization;
using System.Text.Json;
using FluentAssertions;

namespace Andy.Data.Abstractions.Tests;

/// <summary>
/// Guards the numeric-serialization-precision guarantees in docs/reliability.md:
/// doubles must round-trip bitwise, and formatting must be culture-invariant.
/// </summary>
public class NumericPrecisionTests
{
    public static IEnumerable<object[]> AdversarialDoubles() => new[]
    {
        new object[] { 0.1 },
        new object[] { 1.0 / 3.0 },
        new object[] { double.Epsilon },
        new object[] { double.MaxValue },
        new object[] { double.MinValue },
        new object[] { -0.0 },
        new object[] { 123456789.123456789 },
        new object[] { 2.2250738585072014e-308 }, // smallest normal
    };

    [Theory]
    [MemberData(nameof(AdversarialDoubles))]
    public void SystemTextJson_round_trips_double_bitwise(double value)
    {
        var json = JsonSerializer.Serialize(value);
        var back = JsonSerializer.Deserialize<double>(json);
        BitConverter.DoubleToInt64Bits(back).Should().Be(BitConverter.DoubleToInt64Bits(value));
    }

    [Theory]
    [MemberData(nameof(AdversarialDoubles))]
    public void RoundTrip_format_specifiers_preserve_value(double value)
    {
        foreach (var fmt in new[] { "R", "G17" })
        {
            var s = value.ToString(fmt, CultureInfo.InvariantCulture);
            var back = double.Parse(s, CultureInfo.InvariantCulture);
            BitConverter.DoubleToInt64Bits(back).Should().Be(BitConverter.DoubleToInt64Bits(value));
        }
    }

    [Fact]
    public void Invariant_formatting_is_unaffected_by_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
            var s = (1234.5).ToString("R", CultureInfo.InvariantCulture);
            s.Should().Be("1234.5");          // '.' separator, no grouping
            s.Should().NotContain(",");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Preview_row_double_round_trips_through_json()
    {
        var row = new Dictionary<string, object?> { ["amount"] = 0.1 + 0.2 };
        var json = JsonSerializer.Serialize(row);
        var back = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        back["amount"].GetDouble().Should().Be(0.1 + 0.2);
    }
}
