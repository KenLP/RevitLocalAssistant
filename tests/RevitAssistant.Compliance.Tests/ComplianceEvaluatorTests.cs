using Xunit;
using RevitAssistant.Compliance;
using FluentAssertions;

namespace RevitAssistant.Compliance.Tests;

public sealed class ComplianceEvaluatorTests
{
    /// <summary>
    /// Until Phase 6 lands, evaluation must fail loudly rather than return no findings —
    /// "no violations" for a model that was never checked is the one answer a compliance
    /// tool must never give by accident.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_IsNotImplemented_AndDoesNotReportACleanModel()
    {
        var evaluator = new ComplianceEvaluator();

        var act = async () => await evaluator.EvaluateAsync([]);

        await act.Should().ThrowAsync<NotImplementedException>(
            because: "returning an empty finding list would read as a passing compliance check");
    }

    // Phase 6: add tests for rule loading from YAML, assertion parsing,
    // pass/fail logic, Vietnamese descriptions, etc.
}
