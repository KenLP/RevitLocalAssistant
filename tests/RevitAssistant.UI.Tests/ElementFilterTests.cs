using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class ElementFilterTests
{
    // Element with projected fields (raw values, as find_elements returns them).
    private static JsonObject El(long id, string name, params (string K, object? V)[] fields)
    {
        var f = new JsonObject();
        foreach (var (k, v) in fields)
        {
            if (v is null) continue;
            f[k] = v switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(v.ToString()),
            };
        }
        return new JsonObject { ["id"] = id, ["name"] = name, ["fields"] = f };
    }

    private static JsonArray Arr(params JsonObject[] els)
    {
        var a = new JsonArray();
        foreach (var e in els) a.Add(e);
        return a;
    }

    private static JsonArray Filter(string param, string op, object? value = null)
    {
        var f = new JsonObject { ["parameterName"] = param, ["operator"] = op };
        if (value is not null)
            f["value"] = value switch
            {
                string s => JsonValue.Create(s),
                int i => JsonValue.Create(i),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString()),
            };
        return new JsonArray { f };
    }

    private static List<JsonNode> Run(JsonArray els, JsonArray filters)
        => ElementFilter.Apply(els, ElementFilter.Parse(filters));

    [Fact]
    public void EndsWith_Matches()
    {
        var els = Arr(El(1, "d1", ("Mark", "12OPN")), El(2, "d2", ("Mark", "12CLS")),
                      El(3, "d3", ("Mark", "OPN-1")));
        var r = Run(els, Filter("Mark", "ends_with", "OPN"));
        r.Should().HaveCount(1);
        r[0]!["id"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public void StartsWith_Matches()
    {
        var els = Arr(El(1, "a", ("Mark", "EXT-1")), El(2, "b", ("Mark", "INT-1")));
        Run(els, Filter("Mark", "starts_with", "EXT")).Should().HaveCount(1);
    }

    [Fact]
    public void Regex_FireRatingFormat()
    {
        var els = Arr(
            El(1, "ok60", ("Fire Rating", "60 MIN")),
            El(2, "ok120", ("Fire Rating", "120 MIN")),
            El(3, "bad", ("Fire Rating", "1 hour")),
            El(4, "none"));   // no Fire Rating
        var compliant = Run(els, Filter("Fire Rating", "regex", @"^(45|60|90|120|180)\s*MIN$"));
        compliant.Should().HaveCount(2);
    }

    [Fact]
    public void Regex_OverNumericStoredParam_UsesDisplayString()
    {
        // Core's real shape for a formatted numeric param: raw number + *_display.
        var els = Arr(
            El(1, "ok", ("Fire Rating", 60), ("Fire Rating_display", "60 MIN")),
            El(2, "bad", ("Fire Rating", 55), ("Fire Rating_display", "55 MIN")));
        var r = Run(els, Filter("Fire Rating", "regex", @"^(45|60|90|120|180)\s*MIN$"));
        r.Should().ContainSingle();
        r[0]!["id"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public void Contains_OverNumericStoredParam_UsesDisplayString()
    {
        var els = Arr(El(1, "a", ("Fire Rating", 60), ("Fire Rating_display", "60 MIN")));
        Run(els, Filter("Fire Rating", "contains", "MIN")).Should().ContainSingle();
    }

    [Fact]
    public void Numeric_Gt_OverStringFormatted_ParsesLeadingNumber()
    {
        var els = Arr(El(1, "a", ("Fire Rating", "60 MIN")), El(2, "b", ("Fire Rating", "30 MIN")));
        Run(els, Filter("Fire Rating", "gt", 45)).Should().ContainSingle()
            .And.Subject.First()!["id"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public void Neq_OnMissingValue_DoesNotMatch()
    {
        var els = Arr(El(1, "has", ("Mark", "A")), El(2, "missing"));
        var r = Run(els, Filter("Mark", "neq", "B"));
        r.Select(n => n!["id"]!.GetValue<long>()).Should().BeEquivalentTo(new long[] { 1 });
    }

    [Fact]
    public void NotRegex_FindsNonCompliant_IncludingMissing()
    {
        var els = Arr(
            El(1, "ok", ("Fire Rating", "60 MIN")),
            El(2, "bad", ("Fire Rating", "1 hour")),
            El(3, "none"));   // missing → not compliant
        var r = Run(els, Filter("Fire Rating", "not_regex", @"^(45|60|90|120|180)\s*MIN$"));
        r.Select(n => n!["id"]!.GetValue<long>()).Should().BeEquivalentTo(new long[] { 2, 3 });
    }

    [Fact]
    public void IsEmpty_FindsMissingValue()
    {
        var els = Arr(El(1, "has", ("Fire Rating", "60 MIN")), El(2, "missing"));
        var r = Run(els, Filter("Fire Rating", "is_empty"));
        r.Should().ContainSingle().And.Subject.First()!["id"]!.GetValue<long>().Should().Be(2);
    }

    [Fact]
    public void NotEmpty_FindsPresentValue()
    {
        var els = Arr(El(1, "has", ("Fire Rating", "60 MIN")), El(2, "missing"));
        Run(els, Filter("Fire Rating", "not_empty")).Should().HaveCount(1);
    }

    [Fact]
    public void NumericGte_UsesRawValue()
    {
        // raw doubles (e.g. width in ft) — the Core display-string bug is bypassed.
        var els = Arr(El(1, "a", ("Width", 2.0)), El(2, "b", ("Width", 10.0)), El(3, "c", ("Width", 5.0)));
        var r = Run(els, Filter("Width", "gte", 5));
        r.Should().HaveCount(2);
    }

    [Fact]
    public void Contains_CaseInsensitive()
    {
        var els = Arr(El(1, "a", ("Comments", "Đã DUYỆT")), El(2, "b", ("Comments", "chưa")));
        Run(els, Filter("Comments", "contains", "duyệt")).Should().HaveCount(1);
    }

    [Fact]
    public void Eq_ByName_TopLevelFallback()
    {
        var els = Arr(El(1, "Tường A"), El(2, "Tường B"));
        Run(els, Filter("Name", "eq", "Tường A")).Should().ContainSingle();
    }

    [Fact]
    public void MultipleConds_AreAnded()
    {
        var els = Arr(
            El(1, "a", ("Mark", "D-OPN"), ("Comments", "ext")),
            El(2, "b", ("Mark", "D-OPN"), ("Comments", "int")),
            El(3, "c", ("Mark", "W-CLS"), ("Comments", "ext")));
        var filters = new JsonArray
        {
            new JsonObject { ["parameterName"] = "Mark", ["operator"] = "ends_with", ["value"] = "OPN" },
            new JsonObject { ["parameterName"] = "Comments", ["operator"] = "eq", ["value"] = "ext" },
        };
        Run(els, filters).Should().ContainSingle().And.Subject.First()!["id"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public void Regex_InvalidPattern_NoThrowNoMatch()
    {
        var els = Arr(El(1, "a", ("Mark", "x")));
        var act = () => Run(els, Filter("Mark", "regex", "([unclosed"));
        act.Should().NotThrow();
        Run(els, Filter("Mark", "regex", "([unclosed")).Should().BeEmpty();
    }

    [Fact]
    public void Params_ListsDistinctReferenced()
    {
        var filters = new JsonArray
        {
            new JsonObject { ["parameterName"] = "Mark", ["operator"] = "contains", ["value"] = "x" },
            new JsonObject { ["parameterName"] = "Mark", ["operator"] = "ends_with", ["value"] = "y" },
            new JsonObject { ["parameterName"] = "Level", ["operator"] = "eq", ["value"] = "L1" },
        };
        var ps = ElementFilter.Params(ElementFilter.Parse(filters)).ToList();
        ps.Should().BeEquivalentTo(new[] { "Mark", "Level" });
    }
}
