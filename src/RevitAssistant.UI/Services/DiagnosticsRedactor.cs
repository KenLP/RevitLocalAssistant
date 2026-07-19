using System.Text.RegularExpressions;

namespace RevitAssistant.UI;

/// <summary>
/// Strips identifying detail from text before it is written to the on-disk diagnostics log.
///
/// Feedback entries capture the assistant's reply and a snapshot of the backend conversation,
/// which routinely quote the model's full path ("D:\Projects\ClientName\Tower-A.rvt") and the
/// Windows profile directory. That is client and personnel information sitting in a plaintext
/// file that a user may later hand over when reporting a problem. The wording of the reply is
/// what makes feedback useful; the paths are not.
/// </summary>
public static class DiagnosticsRedactor
{
    // UNC first — "\\server\share\..." would otherwise be left behind by the drive pattern.
    private static readonly Regex UncPath =
        new(@"\\\\[^\s""',;]+", RegexOptions.Compiled);

    // Drive-letter paths, including the doubled backslashes of JSON-escaped text.
    private static readonly Regex DrivePath =
        new(@"[A-Za-z]:(\\{1,2})[^\s""',;]*", RegexOptions.Compiled);

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = UncPath.Replace(text!, "[path]");
        result = DrivePath.Replace(result, "[path]");

        // The account name also shows up outside paths (e.g. in a document's author field).
        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user) && user.Length >= 3)
            result = Regex.Replace(result, Regex.Escape(user), "[user]", RegexOptions.IgnoreCase);

        return result;
    }
}
