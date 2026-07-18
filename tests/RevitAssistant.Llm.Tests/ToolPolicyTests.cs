using FluentAssertions;
using RevitAssistant.Llm;
using Xunit;

namespace RevitAssistant.Llm.Tests;

/// <summary>
/// Invariants of the deny-by-default tool policy. These are the rules that make the
/// policy meaningful — without them an entry could silently be dispatchable with no
/// confirmation, which is exactly the hole the policy exists to close.
/// </summary>
public sealed class ToolPolicyTests
{
    [Fact]
    public void UnknownTool_IsNeitherCallableNorDispatchable()
    {
        ToolPolicy.IsLlmCallable("totally_made_up").Should().BeFalse();
        ToolPolicy.IsDispatchable("totally_made_up").Should().BeFalse();
        ToolPolicy.Find("totally_made_up").Should().BeNull();
    }

    [Theory]
    // Real, registered Core commands that are destructive and were never exposed.
    [InlineData("delete_elements")]
    [InlineData("create_wall")]
    [InlineData("move_element")]
    [InlineData("rotate_element")]
    [InlineData("ungroup_elements")]
    public void DangerousCoreCommands_AreNotReachable(string command)
    {
        ToolPolicy.IsLlmCallable(command).Should().BeFalse(
            because: $"'{command}' is registered in Core but must never be reachable from the model");
        ToolPolicy.IsDispatchable(command).Should().BeFalse(
            because: $"'{command}' must never be handed to Core");
    }

    [Theory]
    // Internal orchestration commands: used by C# directly, never named by the model.
    [InlineData("set_parameter_batch")]
    [InlineData("import_parameters")]
    [InlineData("create_sheet")]
    [InlineData("get_active_view")]
    public void InternalTools_AreDispatchable_ButNotLlmCallable(string command)
    {
        ToolPolicy.IsDispatchable(command).Should().BeTrue(
            because: "the orchestrator calls it directly in C#");
        ToolPolicy.IsLlmCallable(command).Should().BeFalse(
            because: "a hallucinated name must not be able to reach it");
    }

    [Fact]
    public void EveryModelWrite_HasAPreviewStrategy()
    {
        var offenders = ToolPolicy.All
            .Where(e => e.Kind == ToolKind.ModelWrite && e.Preview == PreviewStrategy.None)
            .Select(e => e.Name)
            .ToList();

        offenders.Should().BeEmpty(
            because: "a model write with no preview strategy would commit without showing the user anything");
    }

    [Fact]
    public void EveryModelWrite_RequiresConfirmation()
    {
        ToolPolicy.All.Where(e => e.Kind == ToolKind.ModelWrite)
            .Should().OnlyContain(e => e.RequiresConfirmation);
    }

    [Fact]
    public void AssistantAndVirtualTools_AreNeverDispatched()
    {
        ToolPolicy.All
            .Where(e => e.Kind is ToolKind.Assistant or ToolKind.Virtual)
            .Should().OnlyContain(e => !e.IsDispatchable,
                because: "these are resolved in-process and must never reach Core");
    }

    [Fact]
    public void NonWriteTools_NeverRequireConfirmation()
    {
        ToolPolicy.All
            .Where(e => e.Kind != ToolKind.ModelWrite)
            .Should().OnlyContain(e => !e.RequiresConfirmation);
    }

    /// <summary>
    /// The surface offered to the model and the policy must agree. A tool in the surface
    /// with no policy entry is dead on arrival (rejected at dispatch); a mismatch here is
    /// always a bug in one of the two lists.
    /// </summary>
    [Fact]
    public void EveryToolInTheSurface_HasAPolicyEntry()
    {
        var missing = ToolSpecAdapter.BuildToolSurface()
            .Select(t => t.Name)
            .Where(n => ToolPolicy.Find(n) is null)
            .ToList();

        missing.Should().BeEmpty(because: "an offered tool with no policy entry can never be dispatched");
    }

    [Fact]
    public void EveryToolInTheSurface_IsLlmCallable()
    {
        var notCallable = ToolSpecAdapter.BuildToolSurface()
            .Select(t => t.Name)
            .Where(n => !ToolPolicy.IsLlmCallable(n))
            .ToList();

        notCallable.Should().BeEmpty(
            because: "we would be advertising a tool to the model that the gate then rejects");
    }

    /// <summary>
    /// Writes the review flagged as reachable without preview/confirm. They are the
    /// reason the policy exists — pin them so they cannot silently regress to Read.
    /// </summary>
    [Theory]
    [InlineData("tag_all_in_view")]
    [InlineData("copy_parameters")]
    [InlineData("configure_schedule")]
    [InlineData("create_detail_line")]
    [InlineData("change_element_type")]
    [InlineData("apply_view_template")]
    [InlineData("set_level_elevation")]
    [InlineData("update_where")]
    public void KnownModelWrites_RequireConfirmation(string command)
    {
        ToolPolicy.RequiresConfirmation(command).Should().BeTrue(
            because: $"'{command}' mutates the model and must be previewed and confirmed");
    }
}
