using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Ai.Calls;

/// <summary>
/// One call as the settings page lists it: a headline, an outcome chip, compact cost and duration, and the technical
/// details for an expandable area. Built by <see cref="CallPresenter"/>; the page adds only the relative time.
/// </summary>
public sealed record CallRow
{
    /// <summary>Gets when the call was made (UTC); the page shows it relative to now, with the exact time on hover.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Gets who asked: <c>ingest</c>, <c>subtitles</c>, <c>test</c> or <c>other</c>.</summary>
    public string Caller { get; init; } = string.Empty;

    /// <summary>Gets the outcome: <see cref="CallEntry.Answered"/>, <see cref="CallEntry.Refused"/> or
    /// <see cref="CallEntry.Failed"/>.</summary>
    public string Outcome { get; init; } = CallEntry.Failed;

    /// <summary>Gets the outcome chip's icon (✅, ⛔ or ⚠).</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Gets the outcome in words, for the chip's tooltip and screen readers.</summary>
    public string OutcomeLabel { get; init; } = string.Empty;

    /// <summary>Gets a one-line description, e.g. <c>Ingest asked which film or show this is — answered</c>.</summary>
    public string Headline { get; init; } = string.Empty;

    /// <summary>Gets the cost, compact and in the settings' currency when known (<c>AUD 0.0096</c>); empty when nothing
    /// was charged.</summary>
    public string Cost { get; init; } = string.Empty;

    /// <summary>Gets how long it took, compact (<c>850 ms</c>, <c>2.3 s</c>, <c>1 min 5 s</c>).</summary>
    public string Duration { get; init; } = string.Empty;

    /// <summary>Gets the technical details (model, tokens, bytes sent, failure class …), in order.</summary>
    public IReadOnlyList<CallDetail> Details { get; init; } = [];
}

/// <summary>
/// One term of a call's details.
/// </summary>
/// <param name="Term">What it is (<c>Model</c>, <c>Tokens</c> …).</param>
/// <param name="Value">Its value, formatted.</param>
public sealed record CallDetail(string Term, string Value);

/// <summary>
/// A page of the call log as the settings page shows it.
/// </summary>
/// <param name="Enabled">Whether calls are being logged.</param>
/// <param name="Calls">This page of calls, newest first.</param>
/// <param name="LastError">The most recent failure or refusal, when it is newer than the last answered call.</param>
/// <param name="Next">The cursor for the next (older) page, as <c>before</c>; <c>null</c> when there are no more.</param>
public sealed record CallLogView(bool Enabled, IReadOnlyList<CallRow> Calls, CallRow? LastError, long? Next);
