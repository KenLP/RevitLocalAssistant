namespace RevitAssistant.Llm;

/// <summary>How a tool is executed — drives gating and confirmation.</summary>
public enum ToolKind
{
    /// <summary>Assistant-level; never dispatched to Revit (echo_interpretation, clarify).</summary>
    Assistant,

    /// <summary>Resolved entirely in the UI/orchestrator layer, never dispatched to Core.</summary>
    Virtual,

    /// <summary>Read-only in Core: no transaction, no confirmation.</summary>
    Read,

    /// <summary>
    /// Core marks it non-read-only (needs a transaction) but it leaves no persistent
    /// change — it creates and deletes its own scratch element inside the transaction.
    /// Dispatched like a read: confirming it would block a query that changes nothing.
    /// </summary>
    TransientWrite,

    /// <summary>Mutates the model. Requires preview + explicit user confirmation.</summary>
    ModelWrite,
}

/// <summary>How the confirmation preview for a <see cref="ToolKind.ModelWrite"/> is built.</summary>
public enum PreviewStrategy
{
    /// <summary>Not a model write — nothing to preview.</summary>
    None,

    /// <summary>Preview is rendered from the dry-run result (rich per-element diff).</summary>
    FromDryRun,

    /// <summary>
    /// Preview text is rendered from the call arguments. A dry-run still runs first to
    /// validate the arguments against the real model and to capture the commit digest.
    /// </summary>
    FromArgs,
}

/// <param name="LlmExposed">
/// May the model name this tool? False = internal orchestration call only (hard-coded in C#),
/// so a hallucinated name can never reach it.
/// </param>
public sealed record ToolPolicyEntry(
    string Name,
    ToolKind Kind,
    PreviewStrategy Preview,
    bool LlmExposed)
{
    public bool RequiresConfirmation => Kind == ToolKind.ModelWrite;

    public bool IsDispatchable =>
        Kind is ToolKind.Read or ToolKind.TransientWrite or ToolKind.ModelWrite;
}

/// <summary>
/// Deny-by-default registry of every tool this add-in may dispatch.
///
/// Why: the orchestrator used to classify only a handful of names into write gates and let
/// everything else fall through to a plain dispatch. Core registers ~90 commands including
/// delete/create/move, so a model hallucinating a real command name could reach them with no
/// preview and no confirmation. Nothing outside this table is dispatchable, and every
/// <see cref="ToolKind.ModelWrite"/> must carry a preview strategy.
///
/// Enforced twice: in the orchestrator (rejects the tool call) and again in the Revit bridge
/// (refuses to hand the name to Core), so a future caller that bypasses the orchestrator
/// still cannot smuggle a command through.
/// </summary>
public static class ToolPolicy
{
    private static readonly IReadOnlyDictionary<string, ToolPolicyEntry> Map = Build();

    public static IReadOnlyCollection<ToolPolicyEntry> All => (IReadOnlyCollection<ToolPolicyEntry>)Map.Values;

    public static ToolPolicyEntry? Find(string? name) =>
        name is not null && Map.TryGetValue(name, out var e) ? e : null;

    /// <summary>Gate for names coming from the model.</summary>
    public static bool IsLlmCallable(string? name) => Find(name) is { LlmExposed: true };

    /// <summary>Gate for anything about to be handed to Core.</summary>
    public static bool IsDispatchable(string? name) => Find(name) is { IsDispatchable: true };

    public static bool RequiresConfirmation(string? name) => Find(name) is { RequiresConfirmation: true };

    public static PreviewStrategy PreviewFor(string? name) => Find(name)?.Preview ?? PreviewStrategy.None;

    private static IReadOnlyDictionary<string, ToolPolicyEntry> Build()
    {
        var entries = new List<ToolPolicyEntry>
        {
            // ── Assistant-level — never dispatched ───────────────────────────
            new("echo_interpretation", ToolKind.Assistant, PreviewStrategy.None, LlmExposed: true),
            new("clarify",             ToolKind.Assistant, PreviewStrategy.None, LlmExposed: true),

            // ── Virtual — resolved in the UI layer ───────────────────────────
            new("count_elements",     ToolKind.Virtual, PreviewStrategy.None, LlmExposed: true),
            new("aggregate_elements", ToolKind.Virtual, PreviewStrategy.None, LlmExposed: true),
            new("import_data",        ToolKind.Virtual, PreviewStrategy.None, LlmExposed: true),

            // ── Read ─────────────────────────────────────────────────────────
            new("get_document_info",    ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_levels",          ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_rooms",           ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_categories",      ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_families",        ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_family_types",    ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_materials",       ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_phases",          ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("list_sheets",          ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("query_where",          ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_element_info",     ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_parameter",        ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_selected_elements",ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_element_rooms",    ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_tags_in_view",     ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            new("get_doors",            ToolKind.Read, PreviewStrategy.None, LlmExposed: true),
            // Upstream renamed get_room_boundary → spatial_get_room_boundary in v0.8.x.
            new("spatial_get_room_boundary", ToolKind.Read, PreviewStrategy.None, LlmExposed: true),

            // Read-only in Core (no transaction), but it does write a PDF to disk.
            new("export_view_pdf",      ToolKind.Read, PreviewStrategy.None, LlmExposed: true),

            // ── Transient write — needs a transaction, leaves nothing behind ──
            // Creates a temporary 3D view for the raycast and deletes it in a finally block.
            //
            // NOT offered to the model. Its schema costs ~292 tokens and it needs raw (x,y)
            // coordinates a chat user never supplies, so it was pure context overhead — and
            // the tool list plus system prompt already exceed num_ctx, which makes Ollama
            // truncate the system prompt and the model lose its instructions entirely.
            // Still dispatchable for internal callers.
            // Upstream renamed raycast_headroom → spatial_raycast_headroom in v0.8.x.
            new("spatial_raycast_headroom", ToolKind.TransientWrite, PreviewStrategy.None, LlmExposed: false),

            // ── Model writes — preview + confirm required ─────────────────────
            new("update_where",        ToolKind.ModelWrite, PreviewStrategy.FromDryRun, LlmExposed: true),
            new("tag_all_in_view",     ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),
            new("copy_parameters",     ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),
            new("configure_schedule",  ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),
            // Not offered to the model for the same reason as raycast_headroom: ~339 tokens
            // of schema for a tool that needs raw start/end coordinates.
            new("create_detail_line",  ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: false),
            new("change_element_type", ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),
            new("apply_view_template", ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),
            new("set_level_elevation", ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: true),

            // Deliberately absent from the advertised surface (the model is told to use
            // query_where), but accepted when it names it anyway: it is read-only and the
            // orchestrator intercepts it to run the richer client-side filtering.
            new("find_elements",      ToolKind.Read,       PreviewStrategy.None,       LlmExposed: true),

            // ── Internal orchestration only — the model can never name these ──
            new("get_active_view",    ToolKind.Read,       PreviewStrategy.None,       LlmExposed: false),
            // Undo path: restores before-values captured from a confirmed update_where.
            new("set_parameter_batch",ToolKind.ModelWrite, PreviewStrategy.FromDryRun, LlmExposed: false),
            // Import path: both run behind ImportExecutor's own dry-run + confirm flow.
            new("import_parameters",  ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: false),
            new("create_sheet",       ToolKind.ModelWrite, PreviewStrategy.FromArgs,   LlmExposed: false),
        };

        return entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }
}
