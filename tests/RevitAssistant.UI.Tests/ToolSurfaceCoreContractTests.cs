using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using RevitAssistant.Llm;
using RevitMCPAddin.Commands;
using Xunit;

namespace RevitAssistant.UI.Tests;

/// <summary>
/// Guards the contract between what we offer/dispatch and what the pinned
/// RevitMCPCore submodule actually registers.
///
/// Why this exists: a submodule re-pin silently dropped query_where,
/// update_where and import_parameters from Core. Every other unit test still
/// passed — they are pure logic and never reach Core — while the assistant's
/// primary query/edit path and the import commit path were dead at runtime.
/// These tests turn that class of breakage into a fast, obvious failure.
/// </summary>
public sealed class ToolSurfaceCoreContractTests
{
    /// <summary>
    /// Tools resolved entirely in the UI/orchestrator layer. They are
    /// deliberately NOT Core commands, so they are exempt from the check.
    /// </summary>
    private static readonly HashSet<string> VirtualTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "count_elements",      // deterministic counting in C#
        "aggregate_elements",  // deterministic sum/min/max/avg in C#
        "import_data",         // spreadsheet mapping, handled by ImportExecutor
        "echo_interpretation", // assistant-level, never dispatched
        "clarify",             // assistant-level, never dispatched
    };

    private static HashSet<string> CoreCommandNames()
    {
        var registry = new CommandRegistry();
        registry.RegisterDefaults();
        return registry.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "RevitAssistant.slnx")))
        {
            dir = dir.Parent;
        }
        return dir ?? throw new InvalidOperationException(
            "Could not locate the repo root (RevitAssistant.slnx) from the test output directory.");
    }

    /// <summary>Sanity: if the registry came back empty the other assertions would pass vacuously.</summary>
    [Fact]
    public void CoreRegistry_IsPopulated()
    {
        CoreCommandNames().Should().HaveCountGreaterThan(50,
            because: "RegisterDefaults() should register the full Core command set");
    }

    [Fact]
    public void EveryExposedTool_ExistsInCoreRegistry()
    {
        var core = CoreCommandNames();

        var missing = ToolSpecAdapter.BuildToolSurface()
            .Select(t => t.Name)
            .Where(name => !VirtualTools.Contains(name) && !core.Contains(name))
            .OrderBy(n => n)
            .ToList();

        missing.Should().BeEmpty(
            because: "every non-virtual tool offered to the LLM must be dispatchable to the " +
                     "pinned Core — otherwise the model calls it and gets 'unknown command' at runtime");
    }

    [Fact]
    public void EveryHardCodedCoreCall_ExistsInCoreRegistry()
    {
        var core = CoreCommandNames();
        var srcDir = Path.Combine(RepoRoot().FullName, "src");
        var callRx = new Regex(@"CallAsync\(\s*""([a-z_]+)""", RegexOptions.Compiled);

        var scanned = 0;
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;

            scanned++;
            foreach (Match m in callRx.Matches(File.ReadAllText(file)))
            {
                var name = m.Groups[1].Value;
                if (!core.Contains(name)) missing.Add(name);
            }
        }

        scanned.Should().BeGreaterThan(0, because: "the source scan must actually find files to be meaningful");
        missing.Should().BeEmpty(
            because: "every Core command hard-coded in a CallAsync(...) must exist in the pinned Core");
    }

    [Fact]
    public void WriteGatedTools_ExistInCoreRegistry()
    {
        var core = CoreCommandNames();

        // Mirrors OrchestratorChatService.WriteTools + ConfirmExecTools. A gate
        // naming a command Core no longer has silently stops gating anything.
        string[] gated =
        [
            "update_where", "set_parameter", "set_parameter_batch", "rename_element",
            "change_element_type", "set_level_elevation", "apply_view_template", "create_detail_line",
        ];

        var missing = gated.Where(n => !core.Contains(n)).OrderBy(n => n).ToList();

        missing.Should().BeEmpty(
            because: "a write gate that names a non-existent command protects nothing");
    }
}
