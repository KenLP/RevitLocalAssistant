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

    // Phase 2: add tests for tool-call parsing, retry on malformed JSON,
    // Vietnamese → English intent extraction, glossary binding, etc.
}
