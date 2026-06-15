using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class AggregatorNumericTests
{
    // Build a find_elements envelope where each element projects fields[param] = raw double.
    private static JsonObject Find(string param, params (long Id, string Name, double Raw, string? Level)[] els)
    {
        var arr = new JsonArray();
        foreach (var (id, name, raw, lvl) in els)
        {
            var fields = new JsonObject { [param] = raw };
            if (lvl is not null) fields["Level_display"] = lvl;
            arr.Add(new JsonObject { ["id"] = id, ["name"] = name, ["fields"] = fields });
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["count"] = els.Length, ["elements"] = arr, ["truncated"] = false },
        };
    }

    [Fact]
    public void Sum_ConvertsFt3ToM3()
    {
        // 100 ft³ + 100 ft³ = 200 ft³ → 200 * 0.0283168… ≈ 5.663 m³
        var env = Find("Volume", (1, "F1", 100, null), (2, "F2", 100, null));
        var (factor, label) = Aggregator.ResolveUnit("m3", "Volume");
        var r = Aggregator.SummarizeNumeric(env, "Volume", factor, label);

        r["data"]!["unit"]!.GetValue<string>().Should().Be("m³");
        r["data"]!["count"]!.GetValue<int>().Should().Be(2);
        r["data"]!["sum"]!.GetValue<double>().Should().BeApproximately(5.663, 0.01);
    }

    [Fact]
    public void MinMax_TrackElementIdAndName()
    {
        var env = Find("Area",
            (10, "Big", 1000, null), (11, "Small", 100, null), (12, "Mid", 500, null));
        var (factor, label) = Aggregator.ResolveUnit("m2", "Area");
        var r = Aggregator.SummarizeNumeric(env, "Area", factor, label);

        var data = r["data"]!;
        data["max"]!["id"]!.GetValue<long>().Should().Be(10);
        data["max"]!["name"]!.GetValue<string>().Should().Be("Big");
        data["min"]!["id"]!.GetValue<long>().Should().Be(11);
        data["min"]!["name"]!.GetValue<string>().Should().Be("Small");
        // 1000 ft² * 0.0929 ≈ 92.903 m²
        data["max"]!["value"]!.GetValue<double>().Should().BeApproximately(92.903, 0.01);
    }

    [Fact]
    public void Avg_Computed()
    {
        var env = Find("Length", (1, "a", 10, null), (2, "b", 20, null), (3, "c", 30, null));
        var r = Aggregator.SummarizeNumeric(env, "Length", 1.0, "");
        r["data"]!["avg"]!.GetValue<double>().Should().BeApproximately(20.0, 0.001);
    }

    [Fact]
    public void Top_ReturnsLargestFirst()
    {
        var env = Find("Area",
            (1, "a", 100, null), (2, "b", 300, null), (3, "c", 200, null));
        var r = Aggregator.SummarizeNumeric(env, "Area", 1.0, "", top: 2);
        var top = (JsonArray)r["data"]!["top"]!;
        top.Should().HaveCount(2);
        top[0]!["name"]!.GetValue<string>().Should().Be("b");
        top[1]!["name"]!.GetValue<string>().Should().Be("c");
    }

    [Fact]
    public void GroupBy_PerGroupStats()
    {
        var env = Find("Area",
            (1, "a", 100, "L1"), (2, "b", 200, "L1"), (3, "c", 50, "L2"));
        var r = Aggregator.SummarizeNumeric(env, "Area", 1.0, "", groupBy: "Level");
        var groups = (JsonArray)r["data"]!["groups"]!;
        groups.Should().HaveCount(2);
        // sorted by sum desc → L1 (300) first
        groups[0]!["value"]!.GetValue<string>().Should().Be("L1");
        groups[0]!["sum"]!.GetValue<double>().Should().BeApproximately(300, 0.001);
        groups[0]!["count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void ElementsMissingParam_AreSkipped()
    {
        var env = Find("Volume", (1, "a", 100, null));
        // add one element with no fields
        ((JsonArray)env["data"]!["elements"]!).Add(new JsonObject { ["id"] = 2, ["name"] = "b" });
        var r = Aggregator.SummarizeNumeric(env, "Volume", 1.0, "");
        r["data"]!["count"]!.GetValue<int>().Should().Be(1, "elements without the param are skipped");
    }

    [Fact]
    public void NoValues_MinMaxNull()
    {
        var env = new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["count"] = 0, ["elements"] = new JsonArray() },
        };
        var r = Aggregator.SummarizeNumeric(env, "Area", 1.0, "");
        r["data"]!["count"]!.GetValue<int>().Should().Be(0);
        r["data"]!["min"].Should().BeNull();
        r["data"]!["max"].Should().BeNull();
    }

    [Theory]
    [InlineData("Volume", null, "m³")]        // inferred
    [InlineData("Area", null, "m²")]          // inferred
    [InlineData("Length", null, "m")]         // inferred
    [InlineData("Fire Rating", null, "")]     // unitless
    [InlineData("Area", "m2", "m²")]          // explicit matches dimension
    [InlineData("Volume", "m3", "m³")]        // explicit matches dimension
    [InlineData("Area", "internal", "ft²")]   // raw, labelled by dimension
    [InlineData("Volume", "internal", "ft³")]
    public void ResolveUnit_InfersAndOverrides(string param, string? unit, string expectedLabel)
    {
        var (_, label) = Aggregator.ResolveUnit(unit, param);
        label.Should().Be(expectedLabel);
    }

    [Fact]
    public void ResolveUnit_MismatchedUnit_FallsBackToParameterDimension()
    {
        // Model swaps m2 onto a Volume param → must NOT mislabel; use Volume's m³.
        var (factor, label) = Aggregator.ResolveUnit("m2", "Volume");
        label.Should().Be("m³");
        factor.Should().BeApproximately(Aggregator.Ft3ToM3, 1e-9);
    }

    [Fact]
    public void ResolveUnit_UnknownUnit_FallsBackToDimension()
    {
        var (factor, label) = Aggregator.ResolveUnit("cm", "Area");
        label.Should().Be("m²");
        factor.Should().BeApproximately(Aggregator.Ft2ToM2, 1e-9);
    }

    [Fact]
    public void Truncated_AddsIncompleteNote()
    {
        var env = new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject
            {
                ["count"] = 5000,
                ["truncated"] = true,
                ["elements"] = new JsonArray
                {
                    new JsonObject { ["id"] = 1, ["name"] = "a", ["fields"] = new JsonObject { ["Area"] = 10.0 } },
                },
            },
        };
        var r = Aggregator.SummarizeNumeric(env, "Area", 1.0, "");
        r["data"]!["truncated"]!.GetValue<bool>().Should().BeTrue();
        r["data"]!["note"]!.GetValue<string>().Should().Contain("CHƯA đầy đủ");
    }

    [Fact]
    public void Top_IsCappedAt40()
    {
        var els = new (long, string, double, string?)[60];
        for (var i = 0; i < 60; i++) els[i] = (i + 1, $"e{i}", i + 1, null);
        var env = Find("Area", els);
        var r = Aggregator.SummarizeNumeric(env, "Area", 1.0, "", top: 60);
        ((JsonArray)r["data"]!["top"]!).Count.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void ErrorEnvelope_PassedThrough()
    {
        var err = new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["message"] = "x" } };
        Aggregator.SummarizeNumeric(err, "Area", 1.0, "")["ok"]!.GetValue<bool>().Should().BeFalse();
    }
}
