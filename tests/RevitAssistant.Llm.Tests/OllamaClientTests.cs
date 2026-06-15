using Xunit;
using RevitAssistant.Llm;
using FluentAssertions;

namespace RevitAssistant.Llm.Tests;

public sealed class OllamaClientTests
{
    [Fact]
    public void Constructor_DefaultUrl_DoesNotThrow()
    {
        var act = () => new OllamaClient();
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_CustomUrl_DoesNotThrow()
    {
        var act = () => new OllamaClient("http://127.0.0.1:11434", "qwen2.5:7b-instruct");
        act.Should().NotThrow();
    }

    // Integration test — only runs when Ollama is available.
    [Fact(Skip = "Requires Ollama running at localhost:11434")]
    public async Task ChatAsync_SimpleQuery_ReturnsResponse()
    {
        using var client = new OllamaClient();
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a helpful assistant. Answer briefly."),
            ChatMessage.User("Say 'hello' in one word."),
        };
        var response = await client.ChatAsync(messages);
        response.Should().NotBeNull();
        response.TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact(Skip = "Requires Ollama running at localhost:11434")]
    public async Task ChatAsync_WithToolSurface_EmitsToolCall()
    {
        using var client = new OllamaClient();
        var tools = ToolSpecAdapter.BuildToolSurface();
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are a Revit assistant. When the user asks about levels, " +
                "call the list_levels tool."),
            ChatMessage.User("Liệt kê các tầng trong project."),
        };
        var response = await client.ChatAsync(messages, tools);
        response.HasToolCalls.Should().BeTrue();
        response.ToolCalls.Should().Contain(tc => tc.FunctionName == "list_levels");
    }
}
