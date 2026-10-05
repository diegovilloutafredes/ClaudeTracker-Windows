namespace ClaudeTracker.Core;

/// <summary>
/// What a failed in-page <c>fetch</c> means, read from the token the fetch script throws
/// (e.g. <c>"Error: HTTP_401"</c>) — so tokens match by substring.
/// </summary>
public enum FetchFailure
{
    /// <summary>
    /// Cloudflare answered with a challenge page (<c>cf-mitigated: challenge</c>). Says nothing
    /// about the session, so it must never count toward expiry; only a real reload passes it.
    /// </summary>
    Challenge,
    /// <summary>HTTP 401 or 403.</summary>
    Unauthorized,
    RateLimited,
    NotFound,
    /// <summary>Any other HTTP status.</summary>
    Http,
    /// <summary>No HTTP status at all: a transport or script failure.</summary>
    Network,
}

public static class FetchFailures
{
    public static FetchFailure Classify(string message)
    {
        if (message.Contains("CF_CHALLENGE", StringComparison.Ordinal)) return FetchFailure.Challenge;
        if (message.Contains("HTTP_401", StringComparison.Ordinal) || message.Contains("HTTP_403", StringComparison.Ordinal)) return FetchFailure.Unauthorized;
        if (message.Contains("HTTP_429", StringComparison.Ordinal)) return FetchFailure.RateLimited;
        if (message.Contains("HTTP_404", StringComparison.Ordinal)) return FetchFailure.NotFound;
        if (message.Contains("HTTP_", StringComparison.Ordinal)) return FetchFailure.Http;
        return FetchFailure.Network;
    }

    /// <summary>
    /// The words shown for a failure that has no HTTP status. Chromium reports every fetch
    /// that could not reach the network as the same "TypeError: Failed to fetch", and the
    /// 30 s abort as a "TimeoutError": those two get plain words. Anything else is shown as
    /// thrown — it is unexpected, and the raw text is what makes it diagnosable.
    /// </summary>
    public static string NetworkDetail(string message)
    {
        if (message.Contains("Failed to fetch", StringComparison.Ordinal)) return Unreachable;
        if (message.Contains("TimeoutError", StringComparison.Ordinal)) return L.T("claude.ai took too long to answer");
        return message;
    }

    /// <summary>What the popover says when claude.ai could not be reached at all.</summary>
    public static string Unreachable => L.T("Couldn't reach claude.ai");
}

public enum ApiErrorKind { NoOrganization, InvalidResponse, Unauthorized, RateLimited, HttpError, NetworkError }

/// <summary>An error from a claude.ai API call, with the message the popover shows.</summary>
public sealed class ApiException(ApiErrorKind kind, string? detail = null) : Exception(Describe(kind, detail))
{
    public ApiErrorKind Kind { get; } = kind;

    private static string Describe(ApiErrorKind kind, string? detail) => kind switch
    {
        ApiErrorKind.NoOrganization => L.T("No organization found"),
        ApiErrorKind.InvalidResponse => L.T("Invalid API response"),
        ApiErrorKind.Unauthorized => L.T("Session expired — please sign in again"),
        ApiErrorKind.RateLimited => L.T("Rate limited — retrying shortly"),
        ApiErrorKind.HttpError => L.F("Server error: %@", detail ?? ""),
        _ => L.F("Network error: %@", detail ?? ""),
    };
}
