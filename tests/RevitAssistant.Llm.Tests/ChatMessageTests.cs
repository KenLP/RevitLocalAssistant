using Xunit;
using RevitAssistant.Llm;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.Llm.Tests;

public sealed class ChatMessageTests
{
    [Fact]
    public void System_CreatesMessageWithRoleSystem()
    {
        var msg = ChatMessage.System("you are helpful");
        msg.Role.Should().Be(ChatRole.System);
        msg.Content.Should().Be("you are helpful");
    }

    [Fact]
    public void User_CreatesMessageWithRoleUser()
    {
        var msg = ChatMessage.User("hello");
        msg.Role.Should().Be(ChatRole.User);
        msg.Content.Should().Be("hello");
    }

    [Fact]
    public void ToolResult_CreatesCorrectShape()
    {
        var msg = ChatMessage.ToolResult("call_123", """{"count":5}""");
        msg.Role.Should().Be(ChatRole.Tool);
        msg.ToolCallId.Should().Be("call_123");
        msg.Content.Should().Be("""{"count":5}""");
    }

    [Fact]
    public void ToJsonNode_SystemMessage_HasRoleSystem()
    {
        var node = ChatMessage.System("hi").ToJsonNode();
        node["role"]!.GetValue<string>().Should().Be("system");
        node["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void ToJsonNode_ToolResultMessage_HasCorrectRole()
    {
        var node = ChatMessage.ToolResult("id1", "{}").ToJsonNode();
        node["role"]!.GetValue<string>().Should().Be("tool");
        node["tool_call_id"]!.GetValue<string>().Should().Be("id1");
    }

    [Fact]
    public void ToJsonNode_AssistantWithToolCalls_SerializesToolCalls()
    {
        var toolCall = new ToolCall("call_1", "list_levels", "{}");
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = null,
            ToolCalls = [toolCall],
        };
        var node = msg.ToJsonNode();
        node["role"]!.GetValue<string>().Should().Be("assistant");
        var toolCallsArr = node["tool_calls"]!.AsArray();
        toolCallsArr.Should().HaveCount(1);
        toolCallsArr[0]!["function"]!["name"]!.GetValue<string>().Should().Be("list_levels");
    }
}

public sealed class ToolCallTests
{
    [Fact]
    public void ParseArguments_ValidJson_ReturnsObject()
    {
        var tc = new ToolCall("id", "fn", """{"category":"OST_Walls"}""");
        var args = tc.ParseArguments();
        args["category"]!.GetValue<string>().Should().Be("OST_Walls");
    }

    [Fact]
    public void ParseArguments_InvalidJson_Throws()
    {
        var tc = new ToolCall("id", "fn", "not-json{{{");
        var act = () => tc.ParseArguments();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ToJsonNode_ProducesOpenAiWireFormat()
    {
        var tc = new ToolCall("call_abc", "find_elements", "{}");
        var node = tc.ToJsonNode();
        node["id"]!.GetValue<string>().Should().Be("call_abc");
        node["type"]!.GetValue<string>().Should().Be("function");
        node["function"]!["name"]!.GetValue<string>().Should().Be("find_elements");
    }
}
