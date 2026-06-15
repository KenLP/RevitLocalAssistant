using Xunit;
using RevitAssistant.Llm;
using FluentAssertions;

namespace RevitAssistant.Llm.Tests;

public sealed class BimGlossaryTests
{
    // ── Category lookups ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("tường", "OST_Walls")]
    [InlineData("TƯỜNG", "OST_Walls")]        // case-insensitive
    [InlineData("cửa", "OST_Doors")]
    [InlineData("cửa sổ", "OST_Windows")]
    [InlineData("sàn", "OST_Floors")]
    [InlineData("trần", "OST_Ceilings")]
    [InlineData("mái", "OST_Roofs")]
    [InlineData("cột", "OST_Columns")]
    [InlineData("dầm", "OST_StructuralFraming")]
    [InlineData("phòng", "OST_Rooms")]
    [InlineData("tầng", "OST_Levels")]
    [InlineData("thang bộ", "OST_Stairs")]
    public void TryGetCategory_KnownViTerms_ReturnsCorrectCategory(string viTerm, string expectedCategory)
    {
        var result = BimGlossary.TryGetCategory(viTerm);
        result.Should().Be(expectedCategory);
    }

    [Fact]
    public void TryGetCategory_UnknownTerm_ReturnsNull()
    {
        BimGlossary.TryGetCategory("xyz_unknown_term").Should().BeNull();
    }

    [Fact]
    public void TryGetCategory_TrimsWhitespace()
    {
        BimGlossary.TryGetCategory("  tường  ").Should().Be("OST_Walls");
    }

    // ── Parameter lookups ────────────────────────────────────────────────────

    [Theory]
    [InlineData("mã hiệu", "Mark")]
    [InlineData("tên", "Name")]
    [InlineData("chú thích", "Comments")]
    [InlineData("cấp chống cháy", "Fire Rating")]
    [InlineData("chống cháy", "Fire Rating")]
    [InlineData("chiều cao", "Height")]
    [InlineData("chiều dài", "Length")]
    [InlineData("diện tích", "Area")]
    [InlineData("phân khu", "Department")]
    public void TryGetParameter_KnownViTerms_ReturnsCorrectParameter(string viTerm, string expectedParam)
    {
        var result = BimGlossary.TryGetParameter(viTerm);
        result.Should().Be(expectedParam);
    }

    [Fact]
    public void TryGetParameter_UnknownTerm_ReturnsNull()
    {
        BimGlossary.TryGetParameter("no_such_param_xyz").Should().BeNull();
    }

    // ── Prompt snippet ───────────────────────────────────────────────────────

    [Fact]
    public void BuildPromptSnippet_ContainsOstWalls()
    {
        var snippet = BimGlossary.BuildPromptSnippet();
        snippet.Should().Contain("OST_Walls");
    }

    [Fact]
    public void BuildPromptSnippet_ContainsFireRating()
    {
        var snippet = BimGlossary.BuildPromptSnippet();
        snippet.Should().Contain("Fire Rating");
    }

    [Fact]
    public void BuildPromptSnippet_IsNotEmpty()
    {
        var snippet = BimGlossary.BuildPromptSnippet();
        snippet.Should().NotBeNullOrWhiteSpace();
        snippet.Length.Should().BeGreaterThan(200,
            because: "should contain meaningful VI→EN mappings");
    }

    [Fact]
    public void CategoryByVi_HasAtLeastTenEntries()
    {
        BimGlossary.CategoryByVi.Count.Should().BeGreaterThan(10);
    }

    [Fact]
    public void ParameterByVi_HasAtLeastTenEntries()
    {
        BimGlossary.ParameterByVi.Count.Should().BeGreaterThan(10);
    }
}
