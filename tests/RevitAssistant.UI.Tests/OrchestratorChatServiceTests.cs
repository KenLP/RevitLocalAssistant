using Xunit;
using RevitAssistant.UI;
using RevitAssistant.Llm;
using FluentAssertions;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI.Tests;

public sealed class OrchestratorChatServiceTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<ChatResponse> _responses;
        public int CallCount { get; private set; }
        public FakeLlm(params ChatResponse[] responses) => _responses = new(responses);

        public Task<ChatResponse> ChatAsync(
            IList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class FakeBridge : IRevitBridge
    {
        private readonly Func<string, JsonObject, bool, JsonObject> _handler;
        public List<(string Cmd, JsonObject Args, bool DryRun)> Calls { get; } = new();
        public FakeBridge(Func<string, JsonObject, bool, JsonObject> handler) => _handler = handler;

        public Task<JsonObject> CallAsync(string command, JsonObject parameters,
            bool dryRun = false, CancellationToken ct = default)
        {
            Calls.Add((command, parameters, dryRun));
            return Task.FromResult(_handler(command, parameters, dryRun));
        }
    }

    // ── Builders ─────────────────────────────────────────────────────────────

    private static ChatResponse Text(string t) => new(t, Array.Empty<ToolCall>(), "stop");
    private static ChatResponse Calls(params ToolCall[] tcs) => new(null, tcs, "tool_calls");
    private static ToolCall Tc(string name, string argsJson, string id) => new(id, name, argsJson);
    private static ToolCall Echo(string id = "e1") =>
        Tc("echo_interpretation", """{"vi":"Hiểu là: thử","en":"Understood"}""", id);

    private static JsonObject Ok(JsonObject? data = null) =>
        new() { ["ok"] = true, ["data"] = data ?? new JsonObject() };
    private static JsonObject Err(string code, string msg) =>
        new() { ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = msg } };

    // Pass an empty schema string so the service skips the live list_levels +
    // list_categories fetch; tests then see only the tool calls they script.
    private static OrchestratorChatService Make(FakeLlm llm, FakeBridge bridge) => new(llm, bridge, "");

    // ── Read flow ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadFlow_RunsToolThenReturnsFinalText()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("find_elements", """{"category":"OST_Rooms"}""", "r1")),
            Text("Có 3 phòng."));
        var bridge = new FakeBridge((cmd, _, _) =>
            cmd == "find_elements" ? Ok(new JsonObject { ["count"] = 3 }) : Ok());

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("liệt kê phòng");

        turn.Pending.Should().BeNull();
        turn.Replies.Should().Contain(r => r.Text.Contains("Hiểu là"));
        turn.Replies.Should().Contain(r => r.Text == "Có 3 phòng.");
        bridge.Calls.Should().ContainSingle(c => c.Cmd == "find_elements" && !c.DryRun);
    }

    [Fact]
    public async Task CountElements_GroupBy_AggregatesViaFindElements()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("count_elements", """{"category":"OST_Rooms","groupBy":"Level"}""", "c1")),
            Text("Tầng L2 có 3 phòng, L1 có 2 phòng."));
        var bridge = new FakeBridge((cmd, args, _) =>
        {
            cmd.Should().Be("find_elements", "count_elements must query via find_elements");
            // groupBy is projected as a field
            ((JsonArray)args["fields"]!)[0]!.GetValue<string>().Should().Be("Level");
            return new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject
                {
                    ["count"] = 5,
                    ["elements"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 1, ["fields"] = new JsonObject { ["Level_display"] = "L1" } },
                        new JsonObject { ["id"] = 2, ["fields"] = new JsonObject { ["Level_display"] = "L1" } },
                        new JsonObject { ["id"] = 3, ["fields"] = new JsonObject { ["Level_display"] = "L2" } },
                        new JsonObject { ["id"] = 4, ["fields"] = new JsonObject { ["Level_display"] = "L2" } },
                        new JsonObject { ["id"] = 5, ["fields"] = new JsonObject { ["Level_display"] = "L2" } },
                    },
                },
            };
        });

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("bao nhiêu phòng mỗi tầng");

        turn.Pending.Should().BeNull();
        turn.Replies.Should().Contain(r => r.Text.Contains("L2"));
        bridge.Calls.Should().ContainSingle(c => c.Cmd == "find_elements" && !c.DryRun);
    }

    [Fact]
    public async Task AggregateElements_SumsViaFindElements_WithUnitConversion()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("aggregate_elements",
                """{"category":"OST_Floors","parameter":"Volume","unit":"m3"}""", "a1")),
            Text("Tổng thể tích sàn ≈ 5.663 m³."));
        var bridge = new FakeBridge((cmd, args, _) =>
        {
            cmd.Should().Be("find_elements");
            ((JsonArray)args["fields"]!)[0]!.GetValue<string>().Should().Be("Volume");
            return new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject
                {
                    ["count"] = 2,
                    ["elements"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 1, ["name"] = "F1", ["fields"] = new JsonObject { ["Volume"] = 100.0 } },
                        new JsonObject { ["id"] = 2, ["name"] = "F2", ["fields"] = new JsonObject { ["Volume"] = 100.0 } },
                    },
                },
            };
        });

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("tổng m3 sàn");

        turn.Pending.Should().BeNull();
        bridge.Calls.Should().ContainSingle(c => c.Cmd == "find_elements" && !c.DryRun);
        turn.Replies.Should().Contain(r => r.Text.Contains("5.663"));
    }

    // ── Write flow ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteFlow_DryRunsAndReturnsPending_NoCommitYet()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("set_parameter_batch",
                """{"ids":[1,2],"parameterName":"Comments","value":"OK"}""", "w1")));
        var bridge = new FakeBridge((_, _, _) =>
            Ok(new JsonObject { ["total"] = 2, ["succeeded"] = 2, ["failed"] = 0 }));

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("đặt comments cho tường");

        turn.Pending.Should().NotBeNull();
        turn.Pending!.TotalCount.Should().Be(2);
        bridge.Calls.Should().ContainSingle();
        bridge.Calls[0].DryRun.Should().BeTrue("nothing is committed before confirm");
    }

    [Fact]
    public async Task Confirm_CommitsForReal_ThenContinuesToFinalText()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("set_parameter_batch",
                """{"ids":[1,2],"parameterName":"Comments","value":"OK"}""", "w1")),
            Text("Đã cập nhật xong."));
        var bridge = new FakeBridge((_, _, dry) => dry
            ? Ok(new JsonObject { ["total"] = 2, ["succeeded"] = 2, ["failed"] = 0 })
            : Ok(new JsonObject { ["changeSummary"] = "Set 'Comments' on 2/2 elements" }));

        var svc = Make(llm, bridge);
        await svc.SendAsync("đặt comments");
        var turn = await svc.ConfirmAsync();

        bridge.Calls.Should().HaveCount(2);
        bridge.Calls[1].DryRun.Should().BeFalse("confirm commits for real");
        turn.Pending.Should().BeNull();
        turn.Replies.Should().Contain(r => r.Text.Contains("✅"));
        turn.Replies.Should().Contain(r => r.Text == "Đã cập nhật xong.");
    }

    [Fact]
    public async Task DryRunFails_FeedsBackSilently_ModelSelfCorrects()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("set_parameter_batch",
                """{"ids":[1],"parameterName":"Nope","value":"x"}""", "w1")),
            Text("Xin lỗi, tham số không tồn tại."));
        var bridge = new FakeBridge((_, _, _) => Err("not_found", "Parameter 'Nope' not found."));

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("đặt nope");

        turn.Pending.Should().BeNull();
        // No alarming red bubble for the intermediate failure — only the final answer.
        turn.Replies.Should().NotContain(r => r.IsError);
        turn.Replies.Should().Contain(r => r.Text == "Xin lỗi, tham số không tồn tại.");
        bridge.Calls.Should().ContainSingle(c => c.DryRun);
    }

    [Fact]
    public async Task FirstSend_FetchesLiveSchema_WhenNoStaticSchema()
    {
        var llm = new FakeLlm(Text("Chào bạn."));
        var bridge = new FakeBridge((cmd, _, _) => cmd switch
        {
            "list_levels" => Ok(new JsonObject { ["levels"] = new JsonArray() }),
            "list_categories" => Ok(new JsonObject { ["categories"] = new JsonArray() }),
            _ => Ok(),
        });

        // No schema arg → live fetch path.
        var svc = new OrchestratorChatService(llm, bridge);
        await svc.SendAsync("xin chào");

        bridge.Calls.Should().Contain(c => c.Cmd == "list_levels");
        bridge.Calls.Should().Contain(c => c.Cmd == "list_categories");
    }

    [Fact]
    public async Task Reset_StartsFreshConversation()
    {
        var llm = new FakeLlm(Text("một"), Text("hai"));
        var bridge = new FakeBridge((_, _, _) => Ok());
        var svc = Make(llm, bridge);

        await svc.SendAsync("lần 1");
        svc.Reset();
        var turn = await svc.SendAsync("lần 2");

        turn.Replies.Should().Contain(r => r.Text == "hai");
        llm.CallCount.Should().Be(2);
    }

    // ── Clarify ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clarify_ReturnsQuestion_NoPendingNoToolCall()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("clarify", """{"question":"Bạn muốn sửa tầng nào?"}""", "c1")));
        var bridge = new FakeBridge((_, _, _) => Ok());

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("sửa cửa");

        turn.Pending.Should().BeNull();
        turn.Replies.Should().Contain(r => r.Text.Contains("tầng nào"));
        bridge.Calls.Should().BeEmpty("clarify must not touch Revit");
    }

    // ── Cancel keeps conversation usable ─────────────────────────────────────

    [Fact]
    public async Task Cancel_ThenNewSend_Works()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("set_parameter_batch",
                """{"ids":[1],"parameterName":"Comments","value":"x"}""", "w1")),
            Text("Đã hiểu, không thay đổi gì."));
        var bridge = new FakeBridge((_, _, _) =>
            Ok(new JsonObject { ["total"] = 1, ["succeeded"] = 1, ["failed"] = 0 }));

        var svc = Make(llm, bridge);
        var first = await svc.SendAsync("đặt comments");
        first.Pending.Should().NotBeNull();

        svc.CancelPending();
        var second = await svc.SendAsync("thôi khỏi");

        second.Replies.Should().Contain(r => r.Text == "Đã hiểu, không thay đổi gì.");
    }

    [Fact]
    public async Task Confirm_WithNothingPending_ReturnsNotice()
    {
        var svc = Make(new FakeLlm(), new FakeBridge((_, _, _) => Ok()));
        var turn = await svc.ConfirmAsync();
        turn.Replies.Should().Contain(r => r.Text.Contains("Không có thay đổi"));
    }

    [Fact]
    public void Constructor_NullArgs_Throws()
    {
        var bridge = new FakeBridge((_, _, _) => Ok());
        ((Action)(() => new OrchestratorChatService(null!, bridge))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new OrchestratorChatService(new FakeLlm(), null!))).Should().Throw<ArgumentNullException>();
    }
}
