using Xunit;
using RevitAssistant.Compliance;
using FluentAssertions;

namespace RevitAssistant.Compliance.Tests;

public sealed class ComplianceEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_EmptyRules_ReturnsEmptyFindings()
    {
        var evaluator = new ComplianceEvaluator();
        var findings = await evaluator.EvaluateAsync([]);
        findings.Should().BeEmpty();
    }

    // Phase 6: add tests for rule loading from YAML, assertion parsing,
    // pass/fail logic, Vietnamese descriptions, etc.
}
