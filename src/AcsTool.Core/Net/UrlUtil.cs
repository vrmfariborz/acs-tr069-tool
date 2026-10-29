namespace AcsTool.Core.Net;

/// <summary>Helpers for URLs that may carry user:password (review m10).</summary>
public static class UrlUtil
{
    public const string UserInfoRejectedMessage =
        "Remove user:password from the URL: the proxy does not use it. Configure credentials on the CPE; the proxy relays its Authorization header.";

    public static bool HasUserInfo(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.UserInfo.Length > 0;

    /// <summary>Returns the URL without its user-info part (safe for logs and captures). Non-absolute input is returned unchanged.</summary>
    public static string RedactUserInfo(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.UserInfo.Length > 0
            ? u.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped)
            : url;
    }
}
