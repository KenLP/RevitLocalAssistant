using Xunit;
using RevitAssistant.Llm;
using FluentAssertions;

namespace RevitAssistant.Llm.Tests;

public sealed class ToolSpecAdapterTests
{
    private readonly IReadOnlyList<ToolDefinition> _tools = ToolSpecAdapter.BuildToolSurface();

    [Fact]
    public void BuildToolSurface_HasTools()
    {
        _tools.Should().HaveCountGreaterThanOrEqualTo(12);
    }

    [Theory]
    [InlineData("get_document_info")]
    [InlineData("list_levels")]
    [InlineData("list_rooms")]
    [InlineData("list_categories")]
    [InlineData("list_families")]
    [InlineData("list_family_types")]
    [InlineData("list_materials")]
    [InlineData("list_phases")]
    [InlineData("list_sheets")]
    [InlineData("query_where")]
    [InlineData("update_where")]
    [InlineData("count_elements")]
    [InlineData("aggregate_elements")]
    [InlineData("get_element_info")]
    [InlineData("get_parameter")]
    [InlineData("get_selected_elements")]
    [InlineData("echo_interpretation")]
    [InlineData("clarify")]
    [InlineData("get_doors")]
    [InlineData("spatial_get_room_boundary")]
    [InlineData("spatial_raycast_headroom")]
    [InlineData("create_detail_line")]
    public void BuildToolSurface_ContainsTool(string toolName)
    {
        _tools.Should().Contain(t => t.Name == toolName,
            because: $"tool '{toolName}' must be in the surface");
    }

    [Theory]
    [InlineData("find_elements")]
    [InlineData("set_parameter")]
    [InlineData("set_parameter_batch")]
    [InlineData("rename_element")]
    [InlineData("list_elements")]
    public void BuildToolSurface_BlocksRawTools(string toolName)
    {
        _tools.Should().NotContain(t => t.Name == toolName,
            because: "raw id-based / instance-only tools are blocked; LLM uses query_where/update_where");
    }

    [Fact]
    public void AllTools_HaveNonEmptyNames()
    {
        _tools.Should().AllSatisfy(t => t.Name.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void AllTools_HaveNonEmptyDescriptions()
    {
        _tools.Should().AllSatisfy(t => t.Description.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void AllTools_HaveValidJsonParameters()
    {
        foreach (var tool in _tools)
        {
            var act = () => tool.ToJsonNode();
            act.Should().NotThrow(because: $"tool '{tool.Name}' parameters must produce valid JSON");
        }
    }

    [Fact]
    public void QueryWhere_HasCategoryRequired_AndRichOperators()
    {
        var tool = _tools.Single(t => t.Name == "query_where");
        var json = tool.ToJsonNode().ToJsonString();
        json.Should().Contain("\"category\"").And.Contain("\"required\"");
        json.Should().Contain("ends_with").And.Contain("not_regex").And.Contain("is_empty");
        json.Should().Contain("\"scope\"");   // instance vs type
    }

    [Fact]
    public void UpdateWhere_HasSetAndScope()
    {
        var tool = _tools.Single(t => t.Name == "update_where");
        var json = tool.ToJsonNode().ToJsonString();
        json.Should().Contain("\"set\"").And.Contain("\"atomic\"");
        json.Should().Contain("\"scope\"");
    }

    [Fact]
    public void EchoInterpretation_RequiresViAndEn()
    {
        var tool = _tools.Single(t => t.Name == "echo_interpretation");
        var json = tool.ToJsonNode().ToJsonString();
        json.Should().Contain("\"vi\"");
        json.Should().Contain("\"en\"");
    }
}
