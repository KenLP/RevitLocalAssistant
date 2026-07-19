namespace RevitAssistant.Llm;

/// <param name="Url">The endpoint that should actually be used.</param>
/// <param name="Rejected">True when the configured endpoint was refused and <see cref="Url"/> is the safe fallback.</param>
/// <param name="Reason">Why it was refused — shown to the user. Null when nothing was refused.</param>
public sealed record LlmEndpointDecision(string Url, bool Rejected, string? Reason);

/// <summary>
/// Decides which LLM endpoint the add-in is allowed to talk to.
///
/// The product promise is that the model runs locally and nothing leaves the machine, so
/// a loopback endpoint is the only thing accepted by default. Pointing the add-in at a
/// remote host would ship the user's prompts — which quote real project and parameter
/// data — off-box, and over plain HTTP it would do so in cleartext. That needs to be a
/// deliberate act, not a stray environment variable.
///
/// Remote use is still possible: set REVIT_ASSISTANT_ALLOW_REMOTE_LLM=1 and use https.
/// </summary>
public static class LlmEndpointPolicy
{
    public const string DefaultUrl = "http://localhost:11434";

    public static LlmEndpointDecision Evaluate(string? configuredUrl, bool allowRemote)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return new LlmEndpointDecision(DefaultUrl, Rejected: false, Reason: null);

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri))
        {
            return new LlmEndpointDecision(DefaultUrl, Rejected: true,
                $"Địa chỉ LLM '{configuredUrl}' không hợp lệ — dùng mặc định {DefaultUrl}.");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new LlmEndpointDecision(DefaultUrl, Rejected: true,
                $"Giao thức '{uri.Scheme}' không được hỗ trợ — dùng mặc định {DefaultUrl}.");
        }

        if (IsLoopback(uri))
            return new LlmEndpointDecision(configuredUrl, Rejected: false, Reason: null);

        if (!allowRemote)
        {
            return new LlmEndpointDecision(DefaultUrl, Rejected: true,
                $"'{uri.Host}' không phải máy cục bộ. Trợ lý chạy offline nên đã dùng " +
                $"{DefaultUrl} thay thế. Nếu thực sự muốn gửi dữ liệu ra ngoài, đặt " +
                "REVIT_ASSISTANT_ALLOW_REMOTE_LLM=1 và dùng https.");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return new LlmEndpointDecision(DefaultUrl, Rejected: true,
                $"'{uri.Host}' là máy từ xa nhưng dùng http — nội dung hội thoại sẽ đi " +
                $"dạng không mã hoá. Đã dùng {DefaultUrl} thay thế; hãy chuyển sang https.");
        }

        return new LlmEndpointDecision(configuredUrl, Rejected: false, Reason: null);
    }

    /// <summary>True for localhost, 127.0.0.0/8 and ::1.</summary>
    public static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;   // covers "localhost", 127.*, [::1]
        return System.Net.IPAddress.TryParse(uri.Host, out var ip)
            && System.Net.IPAddress.IsLoopback(ip);
    }
}
