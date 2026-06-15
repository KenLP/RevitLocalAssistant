using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class ResultTrimmerTests
{
    private static JsonObject EnvelopeWithElements(int count)
    {
        var arr = new JsonArray();
        for (var i = 0; i < count; i++)
            arr.Add(new JsonObject { ["id"] = 1000 + i, ["name"] = $"R{i}" });
        return new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["count"] = count, ["elements"] = arr },
        };
    }

    [Fact]
    public void Trim_LongArray_CappedWithNote()
    {
        var env = EnvelopeWithElements(100);
        var trimmed = ResultTrimmer.Trim(env, maxItems: 40);

        var elements = (JsonArray)trimmed["data"]!["elements"]!;
        elements.Count.Should().Be(41, "40 kept + 1 note");
        elements[^1]!["_note"]!.GetValue<string>().Should().Contain("40/100");
    }

    [Fact]
    public void Trim_ShortArray_Untouched()
    {
        var env = EnvelopeWithElements(5);
        var trimmed = ResultTrimmer.Trim(env, maxItems: 40);
        ((JsonArray)trimmed["data"]!["elements"]!).Count.Should().Be(5);
    }

    [Fact]
    public void Trim_DoesNotMutateOriginal()
    {
        var env = EnvelopeWithElements(100);
        ResultTrimmer.Trim(env, maxItems: 40);
        ((JsonArray)env["data"]!["elements"]!).Count.Should().Be(100, "original is untouched");
    }

    [Fact]
    public void Trim_PreservesScalarFields()
    {
        var env = EnvelopeWithElements(100);
        var trimmed = ResultTrimmer.Trim(env, maxItems: 40);
        trimmed["ok"]!.GetValue<bool>().Should().BeTrue();
        trimmed["data"]!["count"]!.GetValue<int>().Should().Be(100);
    }
}
