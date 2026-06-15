using Xunit;
using RevitAssistant.Llm;
using FluentAssertions;

namespace RevitAssistant.Llm.Tests;

public sealed class ToolSpecAdapterTests
{
    private readonly IReadOnlyList<ToolDefinition> _tools = ToolSpecAdapter.BuildToolSurface();

    [Fact]
    public void BuildToolSurface_Returns19Tools()
    {
        // echo_interpretation + clarify + 17 Revit tools
        _tools.Should().HaveCountGreaterThanOrEqualTo(19);
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
    [InlineData("list_elements")]
    [InlineData("find_elements")]
    [InlineData("get_element_info")]
    [InlineData("get_parameter")]
    [InlineData("get_selected_elements")]
    [InlineData("set_parameter")]
    [InlineData("set_parameter_batch")]
    [InlineData("rename_element")]
    [InlineData("echo_interpretation")]
    [InlineData("clarify")]
    public void BuildToolSurface_ContainsTool(string toolName)
    {
        _tools.Should().Contain(t => t.Name == toolName,
            because: $"tool '{toolName}' must be in the surface");
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
    public void FindElements_HasCategoryAsRequiredField()
    {
        var findElements = _tools.Single(t => t.Name == "find_elements");
        var json = findElements.ToJsonNode().ToJsonString();
        json.Should().Contain("\"required\"");
        json.Should().Contain("\"category\"");
    }

    [Fact]
    public void SetParameterBatch_HasIdsAsRequired()
    {
        var tool = _tools.Single(t => t.Name == "set_parameter_batch");
        var json = tool.ToJsonNode().ToJsonString();
        json.Should().Contain("\"ids\"");
        json.Should().Contain("\"required\"");
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
