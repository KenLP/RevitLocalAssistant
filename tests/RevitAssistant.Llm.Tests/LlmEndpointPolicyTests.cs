using FluentAssertions;
using RevitAssistant.Llm;
using Xunit;

namespace RevitAssistant.Llm.Tests;

/// <summary>
/// The add-in's promise is that nothing leaves the machine. These pin the conditions
/// under which it is allowed to talk to anything other than loopback.
/// </summary>
public sealed class LlmEndpointPolicyTests
{
    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    [InlineData("https://localhost:11434")]
    public void LoopbackEndpoints_AreAccepted(string url)
    {
        var decision = LlmEndpointPolicy.Evaluate(url, allowRemote: false);

        decision.Rejected.Should().BeFalse();
        decision.Url.Should().Be(url);
    }

    [Theory]
    [InlineData("http://192.168.1.50:11434")]
    [InlineData("https://ollama.example.com")]
    [InlineData("http://evil.example.com:11434")]
    public void RemoteEndpoints_AreRefused_WithoutOptIn(string url)
    {
        var decision = LlmEndpointPolicy.Evaluate(url, allowRemote: false);

        decision.Rejected.Should().BeTrue();
        decision.Url.Should().Be(LlmEndpointPolicy.DefaultUrl,
            because: "the fallback must be local, never the rejected remote host");
        decision.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RemoteOverPlainHttp_IsRefused_EvenWithOptIn()
    {
        var decision = LlmEndpointPolicy.Evaluate("http://ollama.example.com", allowRemote: true);

        decision.Rejected.Should().BeTrue(
            because: "prompts quote real project data and would travel in cleartext");
        decision.Url.Should().Be(LlmEndpointPolicy.DefaultUrl);
    }

    [Fact]
    public void RemoteOverHttps_IsAccepted_WithOptIn()
    {
        const string url = "https://ollama.example.com";

        var decision = LlmEndpointPolicy.Evaluate(url, allowRemote: true);

        decision.Rejected.Should().BeFalse();
        decision.Url.Should().Be(url);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("file:///C:/windows/system32")]
    [InlineData("ftp://example.com")]
    public void MalformedOrUnsupportedSchemes_FallBackToLoopback(string url)
    {
        var decision = LlmEndpointPolicy.Evaluate(url, allowRemote: true);

        decision.Rejected.Should().BeTrue();
        decision.Url.Should().Be(LlmEndpointPolicy.DefaultUrl);
    }

    [Fact]
    public void NoConfiguration_UsesLoopbackDefault_WithoutComplaining()
    {
        var decision = LlmEndpointPolicy.Evaluate(null, allowRemote: false);

        decision.Rejected.Should().BeFalse();
        decision.Url.Should().Be(LlmEndpointPolicy.DefaultUrl);
    }
}
