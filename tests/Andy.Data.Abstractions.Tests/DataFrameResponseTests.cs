using Andy.Data;
using FluentAssertions;

namespace Andy.Data.Abstractions.Tests;

/// <summary>
/// Pins the documented response-envelope shape (docs/tool-contract.md). These run against the
/// self-contained <see cref="DataFrameResponse"/> model and serve as a starting point for the
/// golden tool-call suite described in docs/testing.md.
/// </summary>
public class DataFrameResponseTests
{
    [Fact]
    public void Ok_envelope_has_the_documented_success_shape()
    {
        var schema = new[] { new ColumnSchema("region", "VARCHAR", Nullable: false) };
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["region"] = "EMEA" },
        };

        var env = DataFrameResponse
            .Ok("sales", schema, rowCount: 10, rows, new DataFrameStats(12, 4096, 10))
            .ToEnvelope();

        env["success"].Should().Be(true);
        env["dataset_id"].Should().Be("sales");
        env["row_count"].Should().Be(10L);
        env["preview_truncated"].Should().Be(true); // 10 total rows > 1 preview row
        env.Should().ContainKeys("schema", "preview_rows", "warnings", "stats");
    }

    [Fact]
    public void Error_envelope_carries_stable_code_message_and_details()
    {
        var env = DataFrameResponse.Error(
                DataFrameErrorCodes.ColumnNotFound,
                "Column 'amont' does not exist in dataset 'sales'.",
                new Dictionary<string, object?> { ["column"] = "amont", ["did_you_mean"] = "amount" })
            .ToEnvelope();

        env["success"].Should().Be(false);
        env["error_code"].Should().Be("COLUMN_NOT_FOUND");
        env["message"].Should().Be("Column 'amont' does not exist in dataset 'sales'.");
        env.Should().ContainKey("details");
    }

    [Fact]
    public void PreviewTruncated_is_false_when_every_row_is_present()
    {
        var rows = new IReadOnlyDictionary<string, object?>[]
        {
            new Dictionary<string, object?> { ["x"] = 1 },
        };

        var response = DataFrameResponse.Ok("d", Array.Empty<ColumnSchema>(), rowCount: 1, rows);

        response.PreviewTruncated.Should().BeFalse();
    }
}
