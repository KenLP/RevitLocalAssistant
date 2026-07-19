using FluentAssertions;
using RevitAssistant.UI;
using Xunit;

namespace RevitAssistant.UI.Tests;

public sealed class DiagnosticsRedactorTests
{
    [Theory]
    [InlineData(@"Không mở được D:\Projects\ClientName\Tower-A.rvt")]
    [InlineData(@"path is C:\Users\someone\Documents\model.rvt here")]
    [InlineData(@"on \\fileserver\bim\shared\Tower.rvt")]
    public void FilePaths_AreRemoved(string text)
    {
        var redacted = DiagnosticsRedactor.Redact(text);

        redacted.Should().Contain("[path]");
        redacted.Should().NotContain(".rvt", because: "the file name identifies the client's project");
        redacted.Should().NotContain(@":\");
        redacted.Should().NotContain(@"\\file");
    }

    [Fact]
    public void JsonEscapedPaths_AreAlsoRemoved()
    {
        // ContextSnapshot is JSON, so paths arrive with doubled backslashes.
        var redacted = DiagnosticsRedactor.Redact(@"{""p"":""D:\\Projects\\Client\\Tower.rvt""}");

        redacted.Should().Contain("[path]");
        redacted.Should().NotContain("Tower.rvt");
    }

    [Fact]
    public void OrdinaryText_IsLeftIntact()
    {
        const string text = "Có 12 cửa chưa đánh Mark ở tầng L1.";

        DiagnosticsRedactor.Redact(text).Should().Be(text,
            because: "the wording is what makes the feedback useful");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NullOrEmpty_IsHandled(string? text) =>
        DiagnosticsRedactor.Redact(text).Should().Be(text);
}

public sealed class CsvExportTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"click\")")]
    [InlineData("=1+1")]
    [InlineData("+cmd|'/c calc'!A0")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A9)")]
    public void FormulaLikeValues_AreNeutralised(string value)
    {
        var cell = ChatMessageVm.CsvCell(value);

        // A leading tab makes the spreadsheet read it as text, not a formula.
        var inner = cell.StartsWith('"') ? cell[1..^1].Replace("\"\"", "\"") : cell;
        inner.Should().StartWith("\t");
        inner.Should().NotStartWith("=").And.NotStartWith("+").And.NotStartWith("-").And.NotStartWith("@");
    }

    [Fact]
    public void OrdinaryValue_IsUnchanged()
    {
        ChatMessageVm.CsvCell("101A").Should().Be("101A");
    }

    [Fact]
    public void ValueWithCommaOrQuote_IsStillQuotedAndEscaped()
    {
        ChatMessageVm.CsvCell("a,b").Should().Be("\"a,b\"");
        ChatMessageVm.CsvCell("say \"hi\"").Should().Be("\"say \"\"hi\"\"\"");
    }
}
