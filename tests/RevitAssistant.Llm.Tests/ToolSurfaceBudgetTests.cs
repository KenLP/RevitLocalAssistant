using System.Text.Json.Nodes;
using FluentAssertions;
using RevitAssistant.Llm;
using Xunit;

namespace RevitAssistant.Llm.Tests;

/// <summary>
/// Keeps the per-request fixed cost — tool definitions plus system prompt — inside the
/// context window.
///
/// Found the hard way on a live run: the surface had grown to ~9.5k tokens against a
/// num_ctx of 8192. Ollama truncates from the front of the prompt, so the part that got
/// dropped was the SYSTEM PROMPT — the model lost "always answer in Vietnamese", lost the
/// workflow rules, lost the list of valid tools, then answered in Spanish and invented a
/// tool called "query". Every unit test still passed and the code was correct; only the
/// product was broken. Nothing guarded this, so nothing caught it.
///
/// Adding a tool is cheap to do and expensive to get wrong — a fat JSON schema costs
/// hundreds of tokens whether or not the model can realistically call it.
/// </summary>
public sealed class ToolSurfaceBudgetTests
{
    /// <summary>
    /// Rough tokens-per-character for English prose + JSON schemas. Deliberately
    /// conservative: underestimating the token count is the dangerous direction.
    /// </summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Fixed cost must leave most of the window for the actual conversation — tool
    /// results (element tables) and history are what fill the rest.
    /// </summary>
    private const double MaxFractionOfContext = 0.70;

    private static int EstimatedFixedTokens()
    {
        var tools = new JsonArray();
        foreach (var t in ToolSpecAdapter.BuildToolSurface()) tools.Add(t.ToJsonNode());

        var chars = tools.ToJsonString().Length + AssistantPrompt.Build().Length;
        return chars / CharsPerToken;
    }

    [Fact]
    public void ToolSurfacePlusPrompt_FitsWellInsideTheContextWindow()
    {
        var fixedTokens = EstimatedFixedTokens();
        var budget = (int)(OllamaClient.DefaultNumCtx * MaxFractionOfContext);

        fixedTokens.Should().BeLessThan(budget,
            because: $"tools + system prompt cost ~{fixedTokens} tokens of the " +
                     $"{OllamaClient.DefaultNumCtx}-token window; above {budget} there is too " +
                     "little room left for the conversation, and once the total exceeds the " +
                     "window Ollama silently truncates the system prompt itself. Either drop " +
                     "a tool from ToolSpecAdapter or raise OllamaClient.DefaultNumCtx.");
    }

    [Fact]
    public void ToolSurfacePlusPrompt_NeverExceedsTheWindowOutright()
    {
        EstimatedFixedTokens().Should().BeLessThan(OllamaClient.DefaultNumCtx,
            because: "past this point the model receives a truncated system prompt and " +
                     "loses its instructions entirely — the failure is silent");
    }

    /// <summary>
    /// The two tools that triggered the overflow. Both need raw coordinates a chat user
    /// never supplies, so they cost ~630 tokens for capability the model cannot use.
    /// </summary>
    [Theory]
    [InlineData("raycast_headroom")]
    [InlineData("create_detail_line")]
    public void CoordinateOnlyTools_StayOffTheModelSurface(string toolName)
    {
        ToolSpecAdapter.BuildToolSurface().Should().NotContain(t => t.Name == toolName,
            because: "it is dispatchable via ToolPolicy but must not spend context budget");

        ToolPolicy.IsLlmCallable(toolName).Should().BeFalse();
        ToolPolicy.IsDispatchable(toolName).Should().BeTrue(
            because: "internal callers and a future UI should still be able to use it");
    }

    /// <summary>No single tool should quietly eat a large slice of the window.</summary>
    [Fact]
    public void NoSingleTool_DominatesTheSurface()
    {
        var oversized = ToolSpecAdapter.BuildToolSurface()
            .Select(t => (t.Name, Tokens: t.ToJsonNode().ToJsonString().Length / CharsPerToken))
            .Where(x => x.Tokens > 800)
            .ToList();

        oversized.Should().BeEmpty(
            because: "a single tool costing this much crowds out the conversation");
    }
}
