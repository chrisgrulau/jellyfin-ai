using System;

namespace Jellyfin.Plugin.Ai.Calls;

/// <summary>
/// One AI call attempt, as kept in the call log. It never holds what was sent (instructions, data or schema): only its
/// size. The answer is summarised by its shape (field names, numbers and yes/no values; text only by its length).
/// </summary>
public sealed record CallEntry
{
    /// <summary>What <see cref="Outcome"/> is for a call that was answered.</summary>
    public const string Answered = "answered";

    /// <summary>What <see cref="Outcome"/> is for a call the spending limits refused (nothing was sent).</summary>
    public const string Refused = "refused";

    /// <summary>What <see cref="Outcome"/> is for a call that failed (see <see cref="Failure"/>).</summary>
    public const string Failed = "failed";

    /// <summary>Gets when the call was made (UTC).</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Gets who asked: <c>ingest</c>, <c>subtitles</c>, <c>test</c> (the settings page) or <c>other</c>.</summary>
    public string Caller { get; init; } = string.Empty;

    /// <summary>Gets what the call was for (<c>ingest.match</c> …).</summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>Gets the provider id, if one was chosen.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the model asked, or the one that answered.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the outcome: <see cref="Answered"/>, <see cref="Refused"/> or <see cref="Failed"/>.</summary>
    public string Outcome { get; init; } = Failed;

    /// <summary>Gets the failure class when it failed (<c>authentication</c>, <c>provider-limit</c>, <c>transient</c>,
    /// <c>bad-request</c>, <c>no-connection</c>, <c>not-configured</c>, <c>cancelled</c> …).</summary>
    public string? Failure { get; init; }

    /// <summary>Gets the size of what was sent (instructions, data and schema), in UTF-8 bytes.</summary>
    public long SentBytes { get; init; }

    /// <summary>Gets the input tokens billed, when known.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Gets the output tokens billed (thinking included), when known.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Gets what the call was recorded at on the spend ledger, in <see cref="CostCurrency"/>; <c>null</c> if it
    /// wasn't charged.</summary>
    public decimal? Cost { get; init; }

    /// <summary>Gets the provider's currency for <see cref="Cost"/>.</summary>
    public string? CostCurrency { get; init; }

    /// <summary>Gets the cost in the settings' currency (with the extra percentage), when exchange rates were known.</summary>
    public decimal? DisplayCost { get; init; }

    /// <summary>Gets the settings' currency for <see cref="DisplayCost"/>.</summary>
    public string? DisplayCurrency { get; init; }

    /// <summary>Gets how long the call took, in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Gets a short summary of the answer's shape, when answered.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets why it failed or was refused, redacted and shortened.</summary>
    public string? Error { get; init; }

    /// <summary>Gets a value indicating whether the call didn't produce an answer (refused or failed).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => !string.Equals(Outcome, Answered, StringComparison.Ordinal);
}

/// <summary>
/// The call log as the settings page shows it.
/// </summary>
/// <param name="Enabled">Whether calls are being logged.</param>
/// <param name="Calls">Recent calls, newest first.</param>
/// <param name="LastError">The most recent failure or refusal, when it is newer than the last answered call.</param>
public sealed record CallLogView(bool Enabled, System.Collections.Generic.IReadOnlyList<CallEntry> Calls, CallEntry? LastError);
