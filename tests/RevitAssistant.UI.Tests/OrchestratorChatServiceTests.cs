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
        /// <summary>Batches recorded separately so tests can assert single-transaction use.</summary>
        public List<(IReadOnlyList<(string Command, JsonObject Parameters)> Steps, bool StopOnError, bool DryRun)>
            Batches { get; } = new();
        public FakeBridge(Func<string, JsonObject, bool, JsonObject> handler) => _handler = handler;

        public Task<JsonObject> CallAsync(string command, JsonObject parameters,
            bool dryRun = false, CancellationToken ct = default)
        {
            Calls.Add((command, parameters, dryRun));
            return Task.FromResult(_handler(command, parameters, dryRun));
        }

        public Task<JsonObject> CallBatchAsync(
            IReadOnlyList<(string Command, JsonObject Parameters)> steps,
            bool stopOnError = true, bool dryRun = false, CancellationToken ct = default)
        {
            Batches.Add((steps, stopOnError, dryRun));
            // Run each step through the same handler and shape it like the dispatcher does.
            var results = new JsonArray();
            foreach (var (cmd, args) in steps)
            {
                Calls.Add((cmd, args, dryRun));
                results.Add(_handler(cmd, args, dryRun));
            }
            return Task.FromResult(Ok(new JsonObject
            {
                ["count"] = steps.Count,
                ["results"] = results,
            }));
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

    [Fact]
    public async Task FindElements_FiltersClientSide_ProjectsParamNotServerFilter()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("find_elements",
                """{"category":"OST_Doors","filters":[{"parameterName":"Mark","operator":"ends_with","value":"OPN"}]}""", "f1")),
            Text("Có 1 cửa kết thúc bằng OPN."));
        JsonObject? sentArgs = null;
        var bridge = new FakeBridge((cmd, args, _) =>
        {
            sentArgs = args;
            return new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject
                {
                    ["count"] = 3,
                    ["elements"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 1, ["name"] = "d1", ["fields"] = new JsonObject { ["Mark"] = "10OPN" } },
                        new JsonObject { ["id"] = 2, ["name"] = "d2", ["fields"] = new JsonObject { ["Mark"] = "10CLS" } },
                        new JsonObject { ["id"] = 3, ["name"] = "d3", ["fields"] = new JsonObject { ["Mark"] = "OPN-9" } },
                    },
                },
            };
        });

        var svc = Make(llm, bridge);
        await svc.SendAsync("cửa nào Mark kết thúc OPN");

        sentArgs.Should().NotBeNull();
        sentArgs!["filters"].Should().BeNull("filtering is client-side, not pushed to Core");
        ((JsonArray)sentArgs["fields"]!).Select(n => n!.GetValue<string>())
            .Should().Contain("Mark");
    }

    [Fact]
    public async Task CountElements_WithRichFilter_ProjectsFilterParam()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("count_elements",
                """{"category":"OST_Doors","filters":[{"parameterName":"Fire Rating","operator":"is_empty"}]}""", "c1")),
            Text("Có 2 cửa thiếu Fire Rating."));
        JsonObject? sentArgs = null;
        var bridge = new FakeBridge((cmd, args, _) =>
        {
            sentArgs = args;
            return new JsonObject
            {
                ["ok"] = true,
                ["data"] = new JsonObject
                {
                    ["count"] = 3,
                    ["elements"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 1, ["name"] = "d1", ["fields"] = new JsonObject { ["Fire Rating"] = "60 MIN" } },
                        new JsonObject { ["id"] = 2, ["name"] = "d2" },
                        new JsonObject { ["id"] = 3, ["name"] = "d3" },
                    },
                },
            };
        });

        var svc = Make(llm, bridge);
        await svc.SendAsync("bao nhiêu cửa thiếu Fire Rating");

        ((JsonArray)sentArgs!["fields"]!).Select(n => n!.GetValue<string>())
            .Should().Contain("Fire Rating");
    }

    // ── Write flow ───────────────────────────────────────────────────────────

    // Writes are exercised through update_where — the write tool the model is actually
    // allowed to name. set_parameter_batch is internal-only (ToolPolicy.LlmExposed=false),
    // so a model naming it is rejected before dispatch.
    private const string UpdateWhereArgs =
        """{"category":"OST_Walls","set":{"parameter":"Comments","value":"OK"}}""";

    private static JsonObject UpdateWhereDry() => Ok(new JsonObject
    {
        ["matchedCount"] = 2,
        ["applied"] = 2,
        ["failed"] = 0,
        ["affectedInstances"] = 2,
        ["scope"] = "instance",
    });

    /// <summary>Doc identity must be stable so the preview↔commit check passes.</summary>
    private static JsonObject DocInfo() => Ok(new JsonObject
    {
        ["title"] = "Test.rvt",
        ["pathName"] = @"C:\models\Test.rvt",
    });

    [Fact]
    public async Task WriteFlow_DryRunsAndReturnsPending_NoCommitYet()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")));
        var bridge = new FakeBridge((cmd, _, _) =>
            cmd == "get_document_info" ? DocInfo() : UpdateWhereDry());

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("đặt comments cho tường");

        turn.Pending.Should().NotBeNull();
        turn.Pending!.TotalCount.Should().Be(2);
        bridge.Calls.Should().NotContain(c => c.Cmd == "update_where" && !c.DryRun,
            "nothing is committed before confirm");
        bridge.Calls.Should().ContainSingle(c => c.Cmd == "update_where" && c.DryRun);
    }

    [Fact]
    public async Task Confirm_CommitsForReal_ThenContinuesToFinalText()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")),
            Text("Đã cập nhật xong."));
        var bridge = new FakeBridge((cmd, _, dry) =>
            cmd == "get_document_info" ? DocInfo()
            : dry ? UpdateWhereDry()
            : Ok(new JsonObject { ["changeSummary"] = "Set 'Comments' on 2/2 elements" }));

        var svc = Make(llm, bridge);
        await svc.SendAsync("đặt comments");
        var turn = await svc.ConfirmAsync();

        bridge.Calls.Should().ContainSingle(c => c.Cmd == "update_where" && !c.DryRun,
            "confirm commits exactly once, for real");
        turn.Pending.Should().BeNull();
        turn.Replies.Should().Contain(r => r.Text.Contains("✅"));
        turn.Replies.Should().Contain(r => r.Text == "Đã cập nhật xong.");
    }

    [Fact]
    public async Task DryRunFails_FeedsBackSilently_ModelSelfCorrects()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")),
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

    // ── Deny-by-default (P0-C) ───────────────────────────────────────────────

    [Theory]
    [InlineData("delete_elements")]        // real Core command, never exposed
    [InlineData("move_element")]           // real Core command, never exposed
    [InlineData("set_parameter_batch")]    // internal-only, not model-callable
    [InlineData("totally_made_up_tool")]   // hallucinated
    public async Task DisallowedTool_IsNeverDispatched_AndModelSelfCorrects(string toolName)
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc(toolName, """{"ids":[1]}""", "x1")),
            Text("Xin lỗi, tôi không làm được việc đó."));
        var bridge = new FakeBridge((_, _, _) => Ok());

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("xoá hết đi");

        bridge.Calls.Should().NotContain(c => c.Cmd == toolName,
            because: $"'{toolName}' must never reach Revit");
        turn.Pending.Should().BeNull("a blocked tool must not produce a confirmable write");
        turn.Replies.Should().Contain(r => r.Text == "Xin lỗi, tôi không làm được việc đó.");
    }

    [Theory]
    [InlineData("tag_all_in_view")]
    [InlineData("copy_parameters")]
    [InlineData("configure_schedule")]
    public async Task PreviouslyUngatedWrites_NowRequireConfirmation(string toolName)
    {
        var llm = new FakeLlm(Calls(Echo(), Tc(toolName, """{"category":"OST_Doors"}""", "w1")));
        var bridge = new FakeBridge((cmd, _, _) =>
            cmd == "get_document_info" ? DocInfo() : Ok(new JsonObject { ["changeSummary"] = "x" }));

        var svc = Make(llm, bridge);
        var turn = await svc.SendAsync("làm đi");

        turn.Pending.Should().NotBeNull($"'{toolName}' mutates the model and must be confirmed");
        bridge.Calls.Should().NotContain(c => c.Cmd == toolName && !c.DryRun,
            because: "nothing may be committed before the user confirms");
    }

    // ── Preview ↔ commit binding (P0-D) ──────────────────────────────────────

    [Fact]
    public async Task Confirm_RefusesToCommit_WhenDocumentChangedSincePreview()
    {
        var docCalls = 0;
        var llm = new FakeLlm(Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")));
        var bridge = new FakeBridge((cmd, _, _) =>
        {
            if (cmd != "get_document_info") return UpdateWhereDry();
            docCalls++;
            // Second lookup (at confirm time) reports a different project.
            return Ok(new JsonObject
            {
                ["title"] = docCalls == 1 ? "Test.rvt" : "OtherProject.rvt",
                ["pathName"] = docCalls == 1 ? @"C:\models\Test.rvt" : @"C:\models\Other.rvt",
            });
        });

        var svc = Make(llm, bridge);
        await svc.SendAsync("đặt comments");
        var turn = await svc.ConfirmAsync();

        bridge.Calls.Should().NotContain(c => c.Cmd == "update_where" && !c.DryRun,
            because: "the preview was computed against a different document");
        turn.Replies.Should().Contain(r => r.IsError);
    }

    [Fact]
    public async Task Confirm_RefusesToCommit_WhenModelChangedSincePreview()
    {
        var dryRuns = 0;
        var llm = new FakeLlm(Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")));
        var bridge = new FakeBridge((cmd, _, dry) =>
        {
            if (cmd == "get_document_info") return DocInfo();
            if (!dry) return Ok(new JsonObject { ["changeSummary"] = "committed" });
            dryRuns++;
            // Re-check at confirm time now matches MORE elements than the preview showed.
            return Ok(new JsonObject
            {
                ["matchedCount"] = dryRuns == 1 ? 2 : 7,
                ["applied"] = dryRuns == 1 ? 2 : 7,
                ["failed"] = 0,
                ["affectedInstances"] = dryRuns == 1 ? 2 : 7,
                ["scope"] = "instance",
            });
        });

        var svc = Make(llm, bridge);
        await svc.SendAsync("đặt comments");
        var turn = await svc.ConfirmAsync();

        bridge.Calls.Should().NotContain(c => c.Cmd == "update_where" && !c.DryRun,
            because: "the model changed, so committing would touch a different element set than approved");
        turn.Replies.Should().Contain(r => r.IsError);
    }

    // ── Undo safety (P1) ─────────────────────────────────────────────────────

    /// <summary>update_where result whose before-values are display strings.</summary>
    private static JsonObject UpdateWhereCommitted(string scope = "instance") => Ok(new JsonObject
    {
        ["setParameter"] = "Comments",
        ["scope"] = scope,
        ["results"] = new JsonArray(
            new JsonObject { ["id"] = 101, ["ok"] = true, ["before"] = "old A" },
            new JsonObject { ["id"] = 102, ["ok"] = true, ["before"] = "old B" }),
    });

    private static FakeBridge WriteBridge(
        Func<string, JsonObject, bool, JsonObject>? overrides = null,
        string scope = "instance",
        string storageType = "String") =>
        new((cmd, args, dry) =>
        {
            var custom = overrides?.Invoke(cmd, args, dry);
            if (custom is not null) return custom;
            return cmd switch
            {
                "get_document_info" => DocInfo(),
                "get_parameter" => Ok(new JsonObject { ["storageType"] = storageType }),
                "update_where" => dry ? UpdateWhereDry() : UpdateWhereCommitted(scope),
                "set_parameter_batch" => Ok(new JsonObject { ["failed"] = 0, ["succeeded"] = 2 }),
                _ => Ok(),
            };
        });

    private async Task<(OrchestratorChatService Svc, FakeBridge Bridge, ChatTurn Confirmed)>
        CommitAWriteAsync(FakeBridge bridge)
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")),
            Text("Xong."));
        var svc = Make(llm, bridge);
        await svc.SendAsync("đặt comments");
        var confirmed = await svc.ConfirmAsync();
        return (svc, bridge, confirmed);
    }

    [Fact]
    public async Task Undo_IsOffered_ForInstanceScopedStringParameter()
    {
        var (_, _, confirmed) = await CommitAWriteAsync(WriteBridge());
        confirmed.CanUndo.Should().BeTrue();
    }

    [Fact]
    public async Task Undo_IsNotOffered_ForTypeScopedEdit()
    {
        // The restore would address instance ids for a parameter living on the type.
        var (_, _, confirmed) = await CommitAWriteAsync(WriteBridge(scope: "type"));
        confirmed.CanUndo.Should().BeFalse();
    }

    [Theory]
    [InlineData("Double")]     // before-value came back unit-formatted ("2100 mm")
    [InlineData("Integer")]
    [InlineData("ElementId")]  // before-value came back as the target's NAME
    public async Task Undo_IsNotOffered_WhenBeforeValueCannotRoundTrip(string storageType)
    {
        var (_, _, confirmed) = await CommitAWriteAsync(WriteBridge(storageType: storageType));
        confirmed.CanUndo.Should().BeFalse(
            because: $"a {storageType} display string cannot be written back faithfully");
    }

    [Fact]
    public async Task Undo_RestoresInOneTransaction_AndOnlyAfterProvingItWouldSucceed()
    {
        var (svc, bridge, _) = await CommitAWriteAsync(WriteBridge());
        bridge.Batches.Clear();

        var turn = await svc.UndoAsync();

        turn.Replies.Should().Contain(r => r.Text.Contains("✅"));

        // A single batch per pass — all groups share one Revit transaction, so the
        // restore cannot land halfway.
        bridge.Batches.Should().HaveCount(2, "one rehearsal, one commit");
        bridge.Batches[0].DryRun.Should().BeTrue("the restore is proven before it is applied");
        bridge.Batches[1].DryRun.Should().BeFalse();
        bridge.Batches.Should().OnlyContain(b => b.StopOnError,
            because: "any failing step must roll the whole restore back");
    }

    [Fact]
    public async Task Undo_KeepsUndoState_WhenTheRestoreWouldFail()
    {
        var bridge = WriteBridge((cmd, _, dry) =>
            // The rehearsal reports failures, so nothing should be written.
            cmd == "set_parameter_batch" && dry
                ? Ok(new JsonObject { ["failed"] = 2, ["succeeded"] = 0 })
                : null);

        var (svc, b, _) = await CommitAWriteAsync(bridge);
        b.Batches.Clear();

        var first = await svc.UndoAsync();

        first.Replies.Should().Contain(r => r.IsError);
        b.Batches.Should().NotContain(x => !x.DryRun,
            because: "the rehearsal failed, so the model must be left alone");

        // The state survives so the user can retry rather than being stranded.
        var second = await svc.UndoAsync();
        second.Replies.Should().NotContain(r => r.Text.Contains("Không có thao tác nào để hoàn tác"));
    }

    [Fact]
    public async Task Cancel_ThenNewSend_Works()
    {
        var llm = new FakeLlm(
            Calls(Echo(), Tc("update_where", UpdateWhereArgs, "w1")),
            Text("Đã hiểu, không thay đổi gì."));
        var bridge = new FakeBridge((cmd, _, _) =>
            cmd == "get_document_info" ? DocInfo() : UpdateWhereDry());

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
