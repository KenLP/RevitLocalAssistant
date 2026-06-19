using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class AggregatorTests
{
    private static JsonObject Find(int count, params (long Id, string? LevelDisplay)[] els)
    {
        var arr = new JsonArray();
        foreach (var (id, lvl) in els)
        {
            var o = new JsonObject { ["id"] = id, ["name"] = $"R{id}" };
            if (lvl is not null)
                o["fields"] = new JsonObject { ["Level_display"] = lvl };
            arr.Add(o);
        }
        return new JsonObject
        {
            ["ok"] = true,
            ["data"] = new JsonObject { ["count"] = count, ["elements"] = arr, ["truncated"] = false },
        };
    }

    [Fact]
    public void Summarize_NoGroupBy_ReturnsTotal()
    {
        var env = Find(3, (1, null), (2, null), (3, null));
        var result = Aggregator.Summarize(env, null);

        result["ok"]!.GetValue<bool>().Should().BeTrue();
        result["data"]!["total"]!.GetValue<int>().Should().Be(3);
        result["data"]!["groups"].Should().BeNull();
    }

    [Fact]
    public void Summarize_GroupByLevel_CountsPerGroup()
    {
        var env = Find(5,
            (1, "L1"), (2, "L1"), (3, "L2"), (4, "L2"), (5, "L2"));
        var result = Aggregator.Summarize(env, "Level");

        result["data"]!["total"]!.GetValue<int>().Should().Be(5);
        var groups = (JsonArray)result["data"]!["groups"]!;
        groups.Should().HaveCount(2);
        // Sorted by count desc → L2 (3) first, then L1 (2)
        groups[0]!["value"]!.GetValue<string>().Should().Be("L2");
        groups[0]!["count"]!.GetValue<int>().Should().Be(3);
        groups[1]!["value"]!.GetValue<string>().Should().Be("L1");
        groups[1]!["count"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Summarize_WithLevelOrder_SortsByElevationNotCount()
    {
        // counts: L3=2, L1=1, L2=3  → by count desc would be L2,L3,L1; by elevation L1,L2,L3.
        var env = Find(6, (1, "L3"), (2, "L3"), (3, "L1"), (4, "L2"), (5, "L2"), (6, "L2"));
        var order = new Dictionary<string, double> { ["L1"] = 0, ["L2"] = 3.5, ["L3"] = 7 };

        var r = Aggregator.Summarize(env, "Level", order);
        var groups = (JsonArray)r["data"]!["groups"]!;
        groups.Select(g => g!["value"]!.GetValue<string>())
              .Should().ContainInOrder("L1", "L2", "L3");
    }

    [Fact]
    public void Summarize_MissingGroupValue_BucketsAsEmpty()
    {
        var env = Find(2, (1, "L1"), (2, null));
        var result = Aggregator.Summarize(env, "Level");
        var groups = (JsonArray)result["data"]!["groups"]!;
        groups.Should().Contain(g => g!["value"]!.GetValue<string>() == "(trống)");
    }

    [Fact]
    public void Summarize_ErrorEnvelope_PassedThrough()
    {
        var err = new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject { ["code"] = "not_found", ["message"] = "x" },
        };
        var result = Aggregator.Summarize(err, "Level");
        result["ok"]!.GetValue<bool>().Should().BeFalse();
    }
}
