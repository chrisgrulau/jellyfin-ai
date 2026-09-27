using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Ai.Calls;

/// <summary>
/// Turns call-log entries into what the settings page shows: a friendly headline, an outcome chip, compact cost and
/// duration, and the technical details. Plain formatting only (no clock, no settings), so it can be tested on its own;
/// the page adds the time relative to now, which depends on the viewer's clock and time zone.
/// </summary>
internal static class CallPresenter
{
    /// <summary>The chip icon for an answered call.</summary>
    internal const string AnsweredIcon = "✅";

    /// <summary>The chip icon for a call the spending limits refused.</summary>
    internal const string RefusedIcon = "⛔";

    /// <summary>The chip icon for a failed call.</summary>
    internal const string FailedIcon = "⚠";

    /// <summary>
    /// Presents one call.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <returns>The row.</returns>
    internal static CallRow Present(CallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (icon, label) = Chip(entry);
        return new CallRow
        {
            Time = entry.Time,
            Caller = entry.Caller,
            Outcome = entry.Outcome,
            Icon = icon,
            OutcomeLabel = label,
            Headline = Headline(entry),
            Cost = CompactCost(entry),
            Duration = Duration(entry.DurationMs),
            Details = Details(entry),
        };
    }

    /// <summary>
    /// The outcome chip: an icon and its words.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <returns>The icon and label.</returns>
    internal static (string Icon, string Label) Chip(CallEntry entry) => entry.Outcome switch
    {
        CallEntry.Answered => (AnsweredIcon, "Answered"),
        CallEntry.Refused => (RefusedIcon, "Refused by the spending limits"),
        _ => (FailedIcon, Billed(entry) ? "Failed (charged)" : "Failed"),
    };

    /// <summary>
    /// A one-line description of the call: who asked, what for, and how it ended.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <returns>E.g. <c>Subtitles checked wording — refused: monthly limit reached</c>.</returns>
    internal static string Headline(CallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return What(entry.Caller, entry.Purpose) + " — " + entry.Outcome switch
        {
            CallEntry.Answered => "answered",
            CallEntry.Refused => "refused: " + Refusal(entry.Error),
            _ => "failed: " + Failure(entry.Failure, entry.Error) + (Billed(entry) ? " (charged)" : string.Empty),
        };
    }

    /// <summary>
    /// Who asked and what for, in words.
    /// </summary>
    /// <param name="caller">The logged caller.</param>
    /// <param name="purpose">The purpose tag.</param>
    /// <returns>E.g. <c>Ingest asked which episode this is</c>.</returns>
    internal static string What(string? caller, string? purpose)
    {
        if (string.Equals(caller, "test", StringComparison.Ordinal))
        {
            return "Connection test";
        }

        var who = caller switch
        {
            "ingest" => "Ingest",
            "subtitles" => "Subtitles",
            _ => "Another plugin",
        };
        return who + " " + purpose switch
        {
            "ingest.match" => "asked which film or show this is",
            "ingest.episode" => "asked which episode this is",
            "subtitles.audit" => "checked wording",
            "subtitles.lines" => "matched lines to speech",
            "subtitles.match" => "checked a subtitle matches",
            _ => "asked for help",
        };
    }

    /// <summary>
    /// A short reason for a refusal by the spending limits, from the ledger's message.
    /// </summary>
    /// <param name="error">The logged message.</param>
    /// <returns>E.g. <c>monthly limit reached</c>.</returns>
    internal static string Refusal(string? error)
    {
        var e = error ?? string.Empty;
        if (e.Contains("'s monthly limit", StringComparison.OrdinalIgnoreCase))
        {
            return "provider's monthly limit reached";
        }

        if (e.Contains("monthly limit (", StringComparison.OrdinalIgnoreCase))
        {
            return "monthly limit reached";
        }

        if (e.Contains("limit is 0", StringComparison.OrdinalIgnoreCase))
        {
            return "monthly limit is 0";
        }

        if (e.Contains("exchange rates", StringComparison.OrdinalIgnoreCase) || e.Contains("converted", StringComparison.OrdinalIgnoreCase))
        {
            return "waiting for exchange rates";
        }

        if (e.Contains("can't be read", StringComparison.OrdinalIgnoreCase))
        {
            return "spending record busy";
        }

        return "spending limits";
    }

    /// <summary>
    /// A short reason for a failure, from its class.
    /// </summary>
    /// <param name="failure">The failure class.</param>
    /// <param name="error">The logged message (only to tell a missing provider from a missing key).</param>
    /// <returns>E.g. <c>API key rejected</c>.</returns>
    internal static string Failure(string? failure, string? error) => failure switch
    {
        "authentication" => "API key rejected",
        "provider-limit" => "provider's rate limit",
        "bad-request" => "request rejected",
        "no-connection" => "couldn't reach the provider",
        "not-configured" => error is not null && error.Contains("key", StringComparison.OrdinalIgnoreCase) ? "no API key" : "not set up",
        "cancelled" => "cancelled",
        "transient" => "temporary problem",
        null or "" => "unknown problem",
        _ => failure,
    };

    /// <summary>
    /// The cost, compact: in the settings' currency when it was converted, else in the provider's.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <returns>E.g. <c>AUD 0.0096</c>; empty when nothing was charged.</returns>
    internal static string CompactCost(CallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DisplayCost is { } shown && !string.IsNullOrEmpty(entry.DisplayCurrency))
        {
            return Money(shown, entry.DisplayCurrency);
        }

        return entry.Cost is { } cost ? Money(cost, entry.CostCurrency) : string.Empty;
    }

    /// <summary>
    /// An amount of money: two decimals, or four below one cent.
    /// </summary>
    /// <param name="amount">The amount.</param>
    /// <param name="currency">The currency code.</param>
    /// <returns>E.g. <c>USD 0.0064</c>.</returns>
    internal static string Money(decimal amount, string? currency)
    {
        var text = amount.ToString(amount > 0m && amount < 0.01m ? "0.0000" : "0.00", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(currency) ? text : currency + " " + text;
    }

    /// <summary>
    /// An amount of money as recorded: at least two decimals, up to six.
    /// </summary>
    /// <param name="amount">The amount.</param>
    /// <param name="currency">The currency code.</param>
    /// <returns>E.g. <c>USD 0.0108</c>.</returns>
    internal static string Exact(decimal amount, string? currency)
    {
        var text = amount.ToString("0.00####", CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(currency) ? text : currency + " " + text;
    }

    /// <summary>
    /// A duration, compact.
    /// </summary>
    /// <param name="ms">Milliseconds.</param>
    /// <returns><c>850 ms</c>, <c>2.3 s</c> or <c>1 min 5 s</c>.</returns>
    internal static string Duration(long ms)
    {
        ms = Math.Max(0, ms);
        if (ms < 1000)
        {
            return ms.ToString(CultureInfo.InvariantCulture) + " ms";
        }

        if (ms < 60_000)
        {
            return (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s";
        }

        var seconds = ms / 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min {seconds % 60} s");
    }

    /// <summary>
    /// A size in bytes, compact.
    /// </summary>
    /// <param name="bytes">Bytes.</param>
    /// <returns><c>412 bytes</c>, <c>1.2 KB</c> or <c>3.4 MB</c>.</returns>
    internal static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };

    /// <summary>
    /// The technical details, for the expandable area: only what is known.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <returns>Terms and values, in order.</returns>
    internal static IReadOnlyList<CallDetail> Details(CallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = new List<CallDetail>();
        void Add(string term, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                list.Add(new CallDetail(term, value));
            }
        }

        Add("Purpose", entry.Purpose);
        Add("Provider", entry.Provider);
        Add("Model", entry.Model);
        Add("Failure", entry.IsError ? entry.Failure : null);
        Add("Message", entry.Error);
        Add("Answer shape", entry.Summary);
        if (entry.InputTokens is not null || entry.OutputTokens is not null)
        {
            Add("Tokens", string.Create(CultureInfo.InvariantCulture, $"{entry.InputTokens ?? 0:N0} in, {entry.OutputTokens ?? 0:N0} out"));
        }

        Add("Sent", entry.SentBytes > 0 ? Bytes(entry.SentBytes) : null);
        if (entry.Cost is { } cost)
        {
            Add("Cost (provider's currency)", Exact(cost, entry.CostCurrency));
            if (entry.DisplayCost is { } shown && !string.Equals(entry.DisplayCurrency, entry.CostCurrency, StringComparison.Ordinal))
            {
                Add("Cost (your currency)", Exact(shown, entry.DisplayCurrency));
            }
        }
        else
        {
            Add("Cost", entry.Outcome == CallEntry.Answered ? "unknown" : "not charged");
        }

        Add("Duration", entry.DurationMs.ToString("N0", CultureInfo.InvariantCulture) + " ms");
        return list;
    }

    // A failure the provider billed for (a reply it couldn't use, or an unexpected failure recorded at its estimate)
    private static bool Billed(CallEntry entry)
        => entry.Outcome == CallEntry.Failed && (entry.Cost > 0m || entry.InputTokens is not null);
}
