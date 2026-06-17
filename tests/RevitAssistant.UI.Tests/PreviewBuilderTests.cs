using Xunit;
using RevitAssistant.UI;
using RevitAssistant.Llm;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class PreviewBuilderTests
{
    private static ToolCall Write(string name, string argsJson) => new("call_1", name, argsJson);

    private static JsonObject Envelope(JsonObject data) =>
        new() { ["ok"] = true, ["data"] = data, ["dryRun"] = true, ["committed"] = false };

    [Fact]
    public void Batch_AllSucceed_BuildsRowsAndSummary()
    {
        var write = Write("set_parameter_batch",
            """{"ids":[101,102],"parameterName":"Comments","value":"Đã duyệt"}""");
        var dry = Envelope(new JsonObject
        {
            ["total"] = 2,
            ["succeeded"] = 2,
            ["failed"] = 0,
        });

        var preview = PreviewBuilder.Build(write, dry);

        preview.Title.Should().Be("Sửa tham số hàng loạt");
        preview.TotalCount.Should().Be(2);
        preview.FailedCount.Should().Be(0);
        preview.Rows.Should().HaveCount(2);
        preview.Rows[0].Element.Should().Be("ID 101");
        preview.Rows[0].Detail.Should().Contain("Comments").And.Contain("Đã duyệt");
        preview.Summary.Should().Contain("Comments").And.Contain("Đã duyệt");
    }

    [Fact]
    public void Batch_WithFailure_MarksFailedRow()
    {
        var write = Write("set_parameter_batch",
            """{"ids":[101,102],"parameterName":"Comments","value":"X"}""");
        var dry = Envelope(new JsonObject
        {
            ["total"] = 2,
            ["succeeded"] = 1,
            ["failed"] = 1,
            ["errors"] = new JsonArray
            {
                new JsonObject { ["id"] = 102, ["error"] = "read-only" },
            },
        });

        var preview = PreviewBuilder.Build(write, dry);

        preview.FailedCount.Should().Be(1);
        var failedRow = preview.Rows.Single(r => r.Element == "ID 102");
        failedRow.IsFailure.Should().BeTrue();
        failedRow.Detail.Should().Contain("read-only");
        preview.Summary.Should().Contain("lỗi");
    }

    [Fact]
    public void UpdateWhere_BuildsRowsAndTypeScopeWarning()
    {
        var write = Write("update_where",
            """{"category":"OST_Doors","where":[{"parameter":"Mark","operator":"eq","value":"S10"}],"set":{"parameter":"Fire Rating","value":"90 MIN","scope":"type"}}""");
        var dry = Envelope(new JsonObject
        {
            ["scope"] = "type",
            ["matchedCount"] = 1,
            ["applied"] = 1,
            ["failed"] = 0,
            ["affectedInstances"] = 6,
            ["results"] = new JsonArray
            {
                new JsonObject { ["id"] = 101, ["name"] = "S10", ["ok"] = true, ["before"] = "60 MIN", ["after"] = "90 MIN" },
            },
        });

        var preview = PreviewBuilder.Build(write, dry);

        preview.Title.Should().Be("Sửa tham số (theo điều kiện)");
        preview.Rows.Should().ContainSingle();
        preview.Rows[0].Detail.Should().Contain("Fire Rating").And.Contain("90 MIN");
        preview.Summary.Should().Contain("tham số LOẠI").And.Contain("6");  // collateral warning
    }

    [Fact]
    public void UpdateWhere_FailedRow_MarkedFailure()
    {
        var write = Write("update_where",
            """{"category":"OST_Doors","where":[],"set":{"parameter":"Comments","value":"x"}}""");
        var dry = Envelope(new JsonObject
        {
            ["scope"] = "instance",
            ["matchedCount"] = 2,
            ["applied"] = 1,
            ["failed"] = 1,
            ["affectedInstances"] = 2,
            ["results"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["name"] = "a", ["ok"] = true, ["after"] = "x" },
                new JsonObject { ["id"] = 2, ["name"] = "b", ["ok"] = false, ["reason"] = "read_only" },
            },
        });

        var preview = PreviewBuilder.Build(write, dry);
        preview.FailedCount.Should().Be(1);
        preview.Rows.Single(r => r.Element.Contains("ID 2")).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void SetParameter_Single_OneRow()
    {
        var write = Write("set_parameter", """{"id":5,"parameterName":"Mark","value":"A1"}""");
        var preview = PreviewBuilder.Build(write, Envelope(new JsonObject()));

        preview.Title.Should().Be("Sửa tham số");
        preview.TotalCount.Should().Be(1);
        preview.Rows.Should().ContainSingle();
        preview.Rows[0].Element.Should().Be("ID 5");
        preview.Rows[0].Detail.Should().Be("Mark → A1");
    }

    [Fact]
    public void Rename_OneRow()
    {
        var write = Write("rename_element", """{"id":7,"newName":"Tường A"}""");
        var preview = PreviewBuilder.Build(write, Envelope(new JsonObject()));

        preview.Title.Should().Be("Đổi tên");
        preview.Rows[0].Detail.Should().Be("Tên → Tường A");
    }

    [Fact]
    public void Value_Numeric_FormattedPlainly()
    {
        var write = Write("set_parameter", """{"id":1,"parameterName":"Height","value":60}""");
        var preview = PreviewBuilder.Build(write, Envelope(new JsonObject()));
        preview.Rows[0].Detail.Should().Be("Height → 60");
    }

    [Fact]
    public void Value_Boolean_FormattedAsYesNo()
    {
        var write = Write("set_parameter", """{"id":1,"parameterName":"IsExternal","value":true}""");
        var preview = PreviewBuilder.Build(write, Envelope(new JsonObject()));
        preview.Rows[0].Detail.Should().Be("IsExternal → Yes");
    }

    [Fact]
    public void Batch_CapsRowsAtMax()
    {
        var ids = new JsonArray();
        for (var i = 0; i < 50; i++) ids.Add(1000 + i);
        var write = new ToolCall("c", "set_parameter_batch", new JsonObject
        {
            ["ids"] = ids,
            ["parameterName"] = "Comments",
            ["value"] = "x",
        }.ToJsonString());
        var dry = Envelope(new JsonObject { ["total"] = 50, ["succeeded"] = 50, ["failed"] = 0 });

        var preview = PreviewBuilder.Build(write, dry);

        preview.TotalCount.Should().Be(50);
        preview.Rows.Count.Should().BeLessThanOrEqualTo(20, "rows are capped for display");
    }
}
