using Xunit;
using RevitAssistant.UI;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class TableExtractorTests
{
    private static JsonObject Ok(JsonObject data) => new() { ["ok"] = true, ["data"] = data };

    [Fact]
    public void QueryWhereRows_BecomeTable_AllRows()
    {
        var env = Ok(new JsonObject
        {
            ["count"] = 3,
            ["rows"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["name"] = "A", ["Fire Rating"] = "60 MIN" },
                new JsonObject { ["id"] = 2, ["name"] = "B", ["Fire Rating"] = "90 MIN" },
                new JsonObject { ["id"] = 3, ["name"] = "C", ["Fire Rating"] = "NR" },
            },
        });

        var t = TableExtractor.TryExtract(env)!;
        t.Columns.Should().ContainInOrder("ID", "Tên", "Fire Rating");
        t.Rows.Should().HaveCount(3, "the table renders every row — no dropping");
        t.Rows[0][0].Should().Be("1");
        t.Rows[2][2].Should().Be("NR");
    }

    [Fact]
    public void CountGroups_BecomeTable()
    {
        var env = Ok(new JsonObject
        {
            ["groupBy"] = "Level",
            ["groups"] = new JsonArray
            {
                new JsonObject { ["value"] = "L1", ["count"] = 5 },
                new JsonObject { ["value"] = "L2", ["count"] = 3 },
            },
        });
        var t = TableExtractor.TryExtract(env)!;
        t.Columns.Should().ContainInOrder("Hạng mục", "Số lượng");
        t.Rows.Should().HaveCount(2);
        t.Rows[0].Should().ContainInOrder("L1", "5");
    }

    [Fact]
    public void SkipsScopeAndNoteKeys()
    {
        var env = Ok(new JsonObject
        {
            ["rows"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["name"] = "A", ["Fire Rating_scope"] = "type" },
            },
        });
        var t = TableExtractor.TryExtract(env)!;
        t.Columns.Should().NotContain(c => c.Contains("_scope"));
    }

    [Fact]
    public void NonTabular_ReturnsNull()
    {
        TableExtractor.TryExtract(Ok(new JsonObject { ["title"] = "Project X" }))
            .Should().BeNull();
    }

    [Fact]
    public void ErrorEnvelope_ReturnsNull()
    {
        TableExtractor.TryExtract(new JsonObject { ["ok"] = false }).Should().BeNull();
    }

    private sealed class TableChat : IChatService
    {
        private readonly ResultTable _t;
        public TableChat(ResultTable t) => _t = t;
        public Task<ChatTurn> SendAsync(string i, CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(new[] { new ChatReply("Có 2 mục:") }, null, 0, new[] { _t }));
        public Task<ChatTurn> ConfirmAsync(CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(System.Array.Empty<ChatReply>()));
        public Task<ChatTurn> UndoAsync(CancellationToken ct = default)
            => Task.FromResult(new ChatTurn(System.Array.Empty<ChatReply>()));
        public void CancelPending() { }
        public void Reset() { }
        public string SnapshotContext() => "";
    }

    [Fact]
    public async Task ViewModel_RendersTableBubble_AfterText()
    {
        var table = new ResultTable(new[] { "ID", "Tên" },
            new IReadOnlyList<string>[] { new[] { "1", "A" }, new[] { "2", "B" } }, 2, false);
        var vm = new ChatViewModel(new TableChat(table)) { InputText = "liệt kê" };

        await vm.SendCommand.ExecuteAsync(null);

        vm.Messages.Should().Contain(m => m.IsTable);
        var tbl = vm.Messages.First(m => m.IsTable);
        tbl.Table!.Rows.Should().HaveCount(2);
        tbl.CanRate.Should().BeFalse("table bubbles aren't rated");
    }

    [Fact]
    public void MinMaxObjectCell_FlattensToValue()
    {
        var env = Ok(new JsonObject
        {
            ["groups"] = new JsonArray
            {
                new JsonObject
                {
                    ["value"] = "L1",
                    ["max"] = new JsonObject { ["value"] = 92.9, ["id"] = 7, ["name"] = "Big" },
                },
            },
        });
        var t = TableExtractor.TryExtract(env)!;
        var maxCol = t.Columns.ToList().IndexOf("Lớn nhất");
        maxCol.Should().BeGreaterThanOrEqualTo(0);
        t.Rows[0][maxCol].Should().Contain("92.9").And.Contain("Big");
    }
}
