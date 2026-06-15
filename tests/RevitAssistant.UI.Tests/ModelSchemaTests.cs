using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class ModelSchemaTests
{
    private static JsonObject Levels(params string[] names)
    {
        var arr = new JsonArray();
        foreach (var n in names) arr.Add(new JsonObject { ["name"] = n });
        return new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["levels"] = arr } };
    }

    private static JsonObject Categories(params (string Bic, string Name, int Count)[] cats)
    {
        var arr = new JsonArray();
        foreach (var c in cats)
            arr.Add(new JsonObject
            {
                ["builtInCategory"] = c.Bic,
                ["name"] = c.Name,
                ["instanceCount"] = c.Count,
            });
        return new JsonObject { ["ok"] = true, ["data"] = new JsonObject { ["categories"] = arr } };
    }

    [Fact]
    public void Build_IncludesRealLevelNames()
    {
        var schema = ModelSchema.Build(Levels("L1 - Block 35", "L2"), null);
        schema.Should().NotBeNull();
        schema!.Should().Contain("L1 - Block 35").And.Contain("L2");
    }

    [Fact]
    public void Build_IncludesCategoriesWithCounts()
    {
        var schema = ModelSchema.Build(null, Categories(("OST_Rooms", "Rooms", 42)));
        schema!.Should().Contain("OST_Rooms").And.Contain("42");
    }

    [Fact]
    public void Build_BothNullOrEmpty_ReturnsNull()
    {
        ModelSchema.Build(null, null).Should().BeNull();
        ModelSchema.Build(Levels(), Categories()).Should().BeNull();
    }
}
