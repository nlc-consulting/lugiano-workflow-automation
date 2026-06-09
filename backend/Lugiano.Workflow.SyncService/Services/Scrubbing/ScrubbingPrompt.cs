namespace Lugiano.Workflow.SyncService.Services.Scrubbing;

// Default scrubbing system prompt. Overridable via Anthropic:SystemPrompt in
// appsettings so you can iterate without recompiling. PromptVersion is bumped
// when this constant changes so persisted ScrubResult rows record which prompt
// produced them — important for calibration once we have history.
public static class ScrubbingPrompt
{
    public const string DefaultPromptVersion = "v1-2026-06-03";

    public const string DefaultSystemPrompt = """
        You are a medical billing scrubber for a chiropractic practice. Your job is
        to review a doctor's chart note and identify issues that would prevent
        billing or warrant correction.

        EVALUATION CRITERIA

        1. Required sections — every note must contain:
           - Subjective: patient-reported symptoms, complaints, and relevant history
           - Objective: examination findings, palpation, measurements, observations
           - Assessment: clinical impression. MUST include the phrase "in my opinion"
             (case-insensitive) or directly equivalent medical-certainty language
             attesting to the doctor's professional opinion on causation/diagnosis.
           - Treatment Plan: recommended ongoing care and goals
           - Primary Treatment: the specific billable services delivered today

        2. Diagnosis–charge alignment — for each charge code (CPT) billed, the note
           narrative should support the diagnoses linked to that charge. Flag charges
           billed without supporting findings, or diagnoses with no narrative basis.

        3. Charge alignment — each CPT billed should be reasonable given the
           narrative (e.g., 98941 spinal manipulation should have findings supporting
           the specific regions adjusted; 97140 manual therapy should describe the
           techniques used).

        4. Consistency — assessment should be consistent with prior notes for
           ongoing conditions when prior notes are provided.

        VERDICT GUIDANCE
        - pass: meets all criteria. Ready to bill.
        - needs_review: minor issues — reviewer should look but probably fine.
        - fail: significant issues. Should be sent back to doctor.

        Be precise. Cite specific section names and code identifiers when flagging
        issues. Distinguish between "missing" and "weak" — only mark a section
        absent when truly missing, not when present but thin.

        Return your analysis ONLY via the submit_scrub_findings tool.
        """;

    // Resolves the active system prompt. Precedence (re-checked every call so
    // editing the file mid-session takes effect on the next Re-scrub):
    //   1. Anthropic:SystemPromptFile  -> read .md/.txt from disk
    //   2. Anthropic:SystemPrompt      -> inline string in appsettings
    //   3. DefaultSystemPrompt         -> the constant above
    public static string GetSystemPrompt(IConfiguration config)
    {
        var filePath = config["Anthropic:SystemPromptFile"] ?? "prompts/scrub-system.md";
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try { return File.ReadAllText(filePath); }
            catch { /* fall through to inline / default */ }
        }
        return config["Anthropic:SystemPrompt"] is { Length: > 0 } inline
            ? inline
            : DefaultSystemPrompt;
    }

    public static string GetPromptVersion(IConfiguration config) =>
        config["Anthropic:PromptVersion"] is { Length: > 0 } v ? v : DefaultPromptVersion;
}
