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

    public void Record(FeedbackEntry entry)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var line = new JsonObject
            {
                ["time"] = entry.Time.ToString("o"),
                ["rating"] = entry.Liked ? "up" : "down",
                ["message"] = entry.MessageText,
                ["reason"] = entry.Reason,
                ["context"] = entry.ContextSnapshot,
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
