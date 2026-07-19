namespace RevitAssistant.Compliance;

// ─── Phase 6 stubs ───────────────────────────────────────────────────────────

/// <summary>
/// A single compliance rule loaded from Rules/*.yaml.
/// </summary>
public sealed record ComplianceRule(
    string Id,
    string DescriptionVi,
    string DescriptionEn,
    string Category,
    string? ScopeFilter,   // optional element filter (e.g. Function=Exterior)
    string Assertion,      // e.g. "FireRating >= 60"
    string Severity        // "error" | "warning" | "info"
);

/// <summary>Result for one element against one rule.</summary>
public sealed record ComplianceFinding(
    long ElementId,
    string ElementName,
    ComplianceRule Rule,
    bool Passed,
    string? ActualValue
);

/// <summary>
/// Evaluates a set of rules against the live Revit model.
/// Dispatches find_elements via Core → collects findings → no LLM involved.
/// </summary>
public sealed class ComplianceEvaluator
{
    // Phase 6: inject IRevitCommandDispatcher (from Core submodule)
    public ComplianceEvaluator() { }

    /// <summary>
    /// NOT IMPLEMENTED YET (Phase 6).
    ///
    /// This throws rather than returning an empty list on purpose: an empty result from a
    /// compliance check reads as "no violations found", which is the most dangerous possible
    /// answer to give about an unchecked model. Failing loudly keeps a caller from shipping
    /// a green report that was never actually evaluated.
    /// </summary>
    public Task<IReadOnlyList<ComplianceFinding>> EvaluateAsync(
        IEnumerable<ComplianceRule> rules,
        CancellationToken ct = default)
        => throw new NotImplementedException(
            "ComplianceEvaluator is not implemented yet (Phase 6). It must not report " +
            "'no findings' for a model it never evaluated.");
}
