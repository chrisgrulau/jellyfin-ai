using System;
using System.Linq;
using Jellyfin.Plugin.Ai.Calls;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// The settings page's call list: headlines, outcome chips, compact cost and duration, and the details
public sealed class CallPresenterTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static string? Detail(CallRow row, string term) => row.Details.FirstOrDefault(d => d.Term == term)?.Value;

    [Fact]
    public void An_answered_call_reads_as_a_sentence_with_its_cost_in_the_users_currency()
    {
        var row = CallPresenter.Present(new CallEntry
        {
            Time = T,
            Caller = "ingest",
            Purpose = "ingest.match",
            Provider = "anthropic",
            Model = "claude-opus-5-5",
            Outcome = CallEntry.Answered,
            SentBytes = 1536,
            InputTokens = 1200,
            OutputTokens = 300,
            Cost = 0.0108m,
            CostCurrency = "USD",
            DisplayCost = 0.0162m,
            DisplayCurrency = "AUD",
            DurationMs = 2345,
            Summary = "pick=2, sure=true",
        });

        Assert.Equal("Ingest asked which film or show this is — answered", row.Headline);
        Assert.Equal(CallPresenter.AnsweredIcon, row.Icon);
        Assert.Equal("Answered", row.OutcomeLabel);
        Assert.Equal("AUD 0.02", row.Cost);
        Assert.Equal("2.3 s", row.Duration);
        Assert.Equal(T, row.Time);
        Assert.Equal(
            new[] { "Purpose", "Provider", "Model", "Answer shape", "Tokens", "Sent", "Cost (provider's currency)", "Cost (your currency)", "Duration" },
            row.Details.Select(d => d.Term));
        Assert.Equal("claude-opus-5-5", Detail(row, "Model"));
        Assert.Equal("1,200 in, 300 out", Detail(row, "Tokens"));
        Assert.Equal("1.5 KB", Detail(row, "Sent"));
        Assert.Equal("USD 0.0108", Detail(row, "Cost (provider's currency)"));
        Assert.Equal("AUD 0.0162", Detail(row, "Cost (your currency)"));
        Assert.Equal("2,345 ms", Detail(row, "Duration"));
        Assert.Null(Detail(row, "Failure"));
    }

    [Theory]
    [InlineData("This would go over the monthly limit (AUD 4.99 of AUD 5.00 used).", "monthly limit reached")]
    [InlineData("This would go over anthropic's monthly limit (AUD 1.00 of AUD 1.00 used).", "provider's monthly limit reached")]
    [InlineData("The monthly spending limit is 0, so paid services aren't used.", "monthly limit is 0")]
    [InlineData("The cost can't be worked out in AUD (no recent exchange rates), so paid services wait.", "waiting for exchange rates")]
    [InlineData(null, "spending limits")]
    public void A_refused_call_says_which_limit(string? error, string reason)
    {
        var row = CallPresenter.Present(new CallEntry
        {
            Time = T,
            Caller = "subtitles",
            Purpose = "subtitles.audit",
            Provider = "anthropic",
            Outcome = CallEntry.Refused,
            Failure = "spending-limit",
            Error = error,
            DurationMs = 3,
        });

        Assert.Equal("Subtitles checked wording — refused: " + reason, row.Headline);
        Assert.Equal(CallPresenter.RefusedIcon, row.Icon);
        Assert.Equal("Refused by the spending limits", row.OutcomeLabel);
        Assert.Equal(string.Empty, row.Cost);
        Assert.Equal("3 ms", row.Duration);
        Assert.Equal("spending-limit", Detail(row, "Failure"));
        Assert.Equal("not charged", Detail(row, "Cost"));
        Assert.Null(Detail(row, "Tokens"));
    }

    [Fact]
    public void A_failed_call_says_why_in_words_and_keeps_the_class_in_the_details()
    {
        var row = CallPresenter.Present(new CallEntry
        {
            Time = T,
            Caller = "test",
            Purpose = "ai.test",
            Provider = "anthropic",
            Model = "claude-opus-5-5",
            Outcome = CallEntry.Failed,
            Failure = "authentication",
            Error = "Claude rejected the API key.",
            SentBytes = 200,
            DurationMs = 65_400,
        });

        Assert.Equal("Connection test — failed: API key rejected", row.Headline);
        Assert.Equal(CallPresenter.FailedIcon, row.Icon);
        Assert.Equal("Failed", row.OutcomeLabel);
        Assert.Equal(string.Empty, row.Cost);
        Assert.Equal("1 min 5 s", row.Duration);
        Assert.Equal("authentication", Detail(row, "Failure"));
        Assert.Equal("Claude rejected the API key.", Detail(row, "Message"));
        Assert.Equal("200 bytes", Detail(row, "Sent"));
        Assert.Equal("not charged", Detail(row, "Cost"));
    }

    [Fact]
    public void A_billed_failure_is_marked_as_charged_with_its_tokens_and_cost()
    {
        var row = CallPresenter.Present(new CallEntry
        {
            Time = T,
            Caller = "subtitles",
            Purpose = "subtitles.lines",
            Provider = "anthropic",
            Model = "claude-opus-5-5",
            Outcome = CallEntry.Failed,
            Failure = "bad-request",
            Error = "Claude declined to answer this request.",
            InputTokens = 100,
            OutputTokens = 300,
            Cost = 0.0064m,
            CostCurrency = "USD",
            DurationMs = 900,
        });

        Assert.Equal("Subtitles matched lines to speech — failed: request rejected (charged)", row.Headline);
        Assert.Equal("Failed (charged)", row.OutcomeLabel);

        // No exchange rates: the cost is shown in the provider's currency
        Assert.Equal("USD 0.0064", row.Cost);
        Assert.Equal("100 in, 300 out", Detail(row, "Tokens"));
        Assert.Equal("USD 0.0064", Detail(row, "Cost (provider's currency)"));
        Assert.Null(Detail(row, "Cost (your currency)"));
    }

    [Theory]
    [InlineData("not-configured", "No AI provider can be used.", "not set up")]
    [InlineData("not-configured", "No API key is set for Claude.", "no API key")]
    [InlineData("no-connection", null, "couldn't reach the provider")]
    [InlineData("provider-limit", null, "provider's quota or credit used up")]
    [InlineData("transient", null, "temporary problem")]
    [InlineData("cancelled", null, "cancelled")]
    [InlineData("something-new", null, "something-new")]
    public void Failure_classes_read_as_words(string failure, string? error, string words)
    {
        Assert.Equal(words, CallPresenter.Failure(failure, error));
    }

    [Theory]
    [InlineData("ingest", "ingest.episode", "Ingest asked which episode this is")]
    [InlineData("subtitles", "subtitles.new-thing", "Subtitles asked for help")]
    [InlineData("other", "other.x", "Another plugin asked for help")]
    public void Callers_and_purposes_read_as_words(string caller, string purpose, string what)
    {
        Assert.Equal(what, CallPresenter.What(caller, purpose));
    }

    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(59_949, "59.9 s")]
    [InlineData(125_000, "2 min 5 s")]
    public void Durations_are_compact(long ms, string text)
    {
        Assert.Equal(text, CallPresenter.Duration(ms));
    }

    [Theory]
    [InlineData("0", "USD 0.00")]
    [InlineData("0.004", "USD 0.0040")]
    [InlineData("1.5", "USD 1.50")]
    public void Money_keeps_fractions_of_a_cent(string amount, string text)
    {
        Assert.Equal(text, CallPresenter.Money(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "USD"));
    }
}
