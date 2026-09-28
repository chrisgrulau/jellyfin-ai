using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// Words a provider's failure the same way for every provider, after common's shared rules have classified it: no
/// connection, a transient problem (tried again later), a used-up quota or credit (with when it resets, if the provider
/// said), a refused key (never retried), or a rejected request.
/// </summary>
internal static class ProviderErrors
{
    /// <summary>The most characters of a provider's own explanation kept in a message.</summary>
    internal const int MaxDetail = 200;

    /// <summary>
    /// The failure for an HTTP error reply.
    /// </summary>
    /// <param name="name">The provider's name for people (<c>OpenAI</c>).</param>
    /// <param name="model">The model asked.</param>
    /// <param name="failure">The class common gave it.</param>
    /// <param name="status">The HTTP status, if any.</param>
    /// <param name="detail">What the provider said, keys already removed.</param>
    /// <param name="retryAfter">How long it asked to wait, if it said.</param>
    /// <param name="inner">The cause.</param>
    /// <returns>The exception to throw.</returns>
    internal static AiException Http(string name, string model, FailureClass failure, int? status, string? detail, TimeSpan? retryAfter, Exception? inner)
    {
        var said = Short(detail);
        var message = failure switch
        {
            FailureClass.Authentication when status == 403 => $"{name} didn't allow this key to use {model}{Said(said)}.",
            FailureClass.Authentication => $"{name} didn't accept the API key.",
            FailureClass.ProviderLimit => $"{name}'s quota or credit is used up{(retryAfter is { } w ? " (it says to try again " + Wait(w) + ")" : string.Empty)}{Said(said)}.",
            FailureClass.Transient when status == 429 => $"{name} is rate-limiting requests; they'll be tried later.",
            FailureClass.Transient => $"{name} had a temporary problem{(status is { } s ? string.Create(CultureInfo.InvariantCulture, $" (HTTP {s})") : string.Empty)}; it'll be tried later.",
            FailureClass.NoConnection => $"{name} couldn't be reached.",
            _ when status == 404 => $"{name} doesn't know the model {model}{Said(said)}.",
            _ => $"{name} rejected the request{Said(said)}.",
        };
        return inner is null
            ? new AiException(message) { Failure = failure, RetryAfter = retryAfter }
            : new AiException(message, inner) { Failure = failure, RetryAfter = retryAfter };
    }

    /// <summary>
    /// The provider's own explanation from an error body: the <c>error.message</c> (OpenAI and Gemini style) or
    /// <c>message</c>, else the text, shortened.
    /// </summary>
    /// <param name="body">The error body, keys already removed.</param>
    /// <returns>The explanation, or <c>null</c>.</returns>
    internal static string? DetailOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    return Short(m.GetString());
                }

                if (root.TryGetProperty("error", out var s) && s.ValueKind == JsonValueKind.String)
                {
                    return Short(s.GetString());
                }

                if (root.TryGetProperty("message", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    return Short(t.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text itself
        }

        return Short(body);
    }

    /// <summary>
    /// A wait in words.
    /// </summary>
    /// <param name="wait">The wait.</param>
    /// <returns>E.g. <c>in 20 seconds</c>, <c>in about 3 hours</c>, <c>in about 2 days</c>.</returns>
    internal static string Wait(TimeSpan wait)
    {
        static string N(double v, string unit) => string.Create(CultureInfo.InvariantCulture, $"{v:0} {unit}{(Math.Round(v) == 1 ? string.Empty : "s")}");
        return wait.TotalSeconds < 90 ? "in " + N(Math.Max(1, wait.TotalSeconds), "second")
            : wait.TotalMinutes < 90 ? "in about " + N(wait.TotalMinutes, "minute")
            : wait.TotalHours < 36 ? "in about " + N(wait.TotalHours, "hour")
            : "in about " + N(wait.TotalDays, "day");
    }

    private static string Said(string? detail) => string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail.TrimEnd('.');

    private static string? Short(string? text)
    {
        var t = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length == 0)
        {
            return null;
        }

        return t.Length <= MaxDetail ? t : t[..(char.IsHighSurrogate(t[MaxDetail - 1]) ? MaxDetail - 1 : MaxDetail)].TrimEnd() + "…";
    }

    /// <summary>
    /// Takes the JSON out of an answer that wrapped it in a Markdown code fence (some local models do, in JSON mode).
    /// </summary>
    /// <param name="text">The answer text.</param>
    /// <returns>The text without a surrounding fence.</returns>
    internal static string Unfence(string text)
    {
        var t = (text ?? string.Empty).Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal) || !t.EndsWith("```", StringComparison.Ordinal) || t.Length < 6)
        {
            return t;
        }

        var body = t[3..^3];
        var newline = body.IndexOf('\n', StringComparison.Ordinal);
        return (newline >= 0 && body[..newline].All(char.IsAsciiLetter) ? body[(newline + 1)..] : body).Trim();
    }
}
