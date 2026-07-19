using System.IO;
using System.Text.Json.Nodes;

namespace RevitAssistant.UI;

/// <summary>
/// Appends thumbs-down feedback as JSON lines to
/// %APPDATA%\RevitAssistant\feedback.jsonl, so misbehaviours can be reviewed and
/// the prompt/tools improved. Failures are swallowed — feedback must never break chat.
/// </summary>
public sealed class FileFeedbackSink : IFeedbackSink
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileFeedbackSink(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitAssistant", "feedback.jsonl");
    }

    public string FilePath => _path;

    /// <summary>
    /// Deletes the diagnostics log. The user owns this data and must be able to get rid of
    /// it without hunting through %APPDATA% by hand.
    /// </summary>
    public void Clear()
    {
        try
        {
            lock (_gate)
                if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // same contract as Record: diagnostics must never break the chat
        }
    }

    public void Record(FeedbackEntry entry)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Model paths and the account name are not what makes feedback useful, but they
            // are what makes this file sensitive to hand over. Scrub before it touches disk.
            var line = new JsonObject
            {
                ["time"] = entry.Time.ToString("o"),
                ["rating"] = entry.Liked ? "up" : "down",
                ["message"] = DiagnosticsRedactor.Redact(entry.MessageText),
                ["reason"] = DiagnosticsRedactor.Redact(entry.Reason),
                ["context"] = DiagnosticsRedactor.Redact(entry.ContextSnapshot),
            }.ToJsonString();

            lock (_gate)
                File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch
        {
            // never propagate — logging feedback must not disrupt the chat
        }
    }
}
