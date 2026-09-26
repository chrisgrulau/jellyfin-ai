using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Bridge;
using Jellyfin.Plugin.Ai.Calls;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// FEAT-04: a log of AI calls and the last error on the settings page
public sealed class CallLogTests : IDisposable
{
    private const string Key = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-calls-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();

    public CallLogTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // 1 EUR = 1.10 USD = 1.65 AUD, so 1 USD = 1.5 AUD
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    private string LogPath => Path.Combine(_dir, CallLog.FileName);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CallLog Log() => new(LogPath, _clock);

    private static PluginConfiguration Config(bool keep = true)
    {
        var c = new PluginConfiguration { AllowIngest = true, AllowSubtitles = true, Currency = "AUD", KeepCallLog = keep };
        c.Providers.Add(new ProviderSettings { Id = KnownProviders.Anthropic, Enabled = true });
        return c;
    }

    private static SpendLimits Aud(decimal? overall = 5m) => new("AUD", overall, new Dictionary<string, decimal>(), 0m);

    private static CallContext Context(string caller = "ingest", bool keep = true, AiSpending? spending = null) => new(caller, keep, Aud(), spending?.Rates.Current, [Key]);

    private static string BridgeRequest(string caller = "ingest", string purpose = "ingest.match")
        => "{\"version\":1,\"caller\":\"" + caller + "\",\"purpose\":\"" + purpose + "\",\"instructions\":\"PRIVATE-INSTRUCTIONS pick one\","
            + "\"data\":{\"file\":\"PRIVATE-DATA.mkv\"},\"schema\":{\"type\":\"object\"},\"maxOutputTokens\":1000}";

    private static AiRequest Request(string purpose = "ingest.match") => new()
    {
        Purpose = purpose,
        Instructions = "PRIVATE-INSTRUCTIONS",
        Data = "{\"file\":\"PRIVATE-DATA.mkv\"}",
        Schema = new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") },
        MaxOutputTokens = 1000,
    };

    private static CallEntry Entry(DateTimeOffset time, string caller = "ingest", string outcome = CallEntry.Answered)
        => new() { Time = time, Caller = caller, Purpose = caller + ".match", Outcome = outcome, Error = outcome == CallEntry.Answered ? null : "down" };

    [Fact]
    public async Task An_answered_bridge_call_is_logged_with_its_cost_but_never_what_was_sent()
    {
        using var spending = new AiSpending(_dir);
        var keys = new ApiKeyStore(Path.Combine(_dir, "keys.json"));
        var log = Log();
        var fake = new Fake { Answer = new { pick = 2, sure = true, reason = "PRIVATE-ANSWER-TEXT" } };

        var reply = await AiBridge.AnswerAsync(BridgeRequest(), Config(), keys, spending, log, _ => (new MeteredModel(fake, spending, Aud()), null), TestContext.Current.CancellationToken);

        Assert.Contains("\"ok\":true", reply, StringComparison.Ordinal);
        var e = Assert.Single(log.Recent(10, null));
        Assert.Equal("ingest", e.Caller);
        Assert.Equal("ingest.match", e.Purpose);
        Assert.Equal(KnownProviders.Anthropic, e.Provider);
        Assert.Equal(ClaudeModel.DefaultModel, e.Model);
        Assert.Equal(CallEntry.Answered, e.Outcome);
        Assert.Null(e.Failure);
        Assert.Equal(100, e.InputTokens);
        Assert.Equal(300, e.OutputTokens);

        // 100 in + 300 out at 4/20 USD per million = 0.0064 USD = 0.0096 AUD
        Assert.Equal(0.0064m, e.Cost);
        Assert.Equal("USD", e.CostCurrency);
        Assert.Equal(0.0096m, e.DisplayCost);
        Assert.Equal("AUD", e.DisplayCurrency);
        Assert.True(e.SentBytes > 0);
        Assert.Equal("pick=2, sure=true, reason=text (19 chars)", e.Summary);

        // Neither the instructions, the data nor the answer's text reach the file
        var file = await File.ReadAllTextAsync(LogPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("PRIVATE", file, StringComparison.Ordinal);
        Assert.Contains("ingest.match", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_call_the_limits_refuse_is_logged_as_refused()
    {
        using var spending = new AiSpending(_dir);
        var log = Log();
        var model = new MeteredModel(new Fake(), spending, Aud(0m));

        await Assert.ThrowsAsync<AiException>(() => log.AskAsync(model, Request(), Context(spending: spending), TestContext.Current.CancellationToken));

        var e = Assert.Single(log.Recent(10, null));
        Assert.Equal(CallEntry.Refused, e.Outcome);
        Assert.Equal("spending-limit", e.Failure);
        Assert.Null(e.Cost);
        Assert.False(string.IsNullOrEmpty(e.Error));
        Assert.Same(e, log.LastError());
    }

    [Fact]
    public async Task A_failed_call_is_logged_with_its_failure_class()
    {
        using var spending = new AiSpending(_dir);
        var log = Log();
        var model = new MeteredModel(new Fake { Throw = new AiException("Claude rejected the API key.") { Failure = FailureClass.Authentication } }, spending, Aud());

        await Assert.ThrowsAsync<AiException>(() => log.AskAsync(model, Request(), Context(spending: spending), TestContext.Current.CancellationToken));

        var e = Assert.Single(log.Recent(10, null));
        Assert.Equal(CallEntry.Failed, e.Outcome);
        Assert.Equal("authentication", e.Failure);
        Assert.Equal("Claude rejected the API key.", e.Error);
        Assert.Null(e.Cost);
        Assert.Null(e.InputTokens);
    }

    [Fact]
    public async Task A_billed_failure_is_logged_with_its_tokens_and_cost()
    {
        using var spending = new AiSpending(_dir);
        var log = Log();
        var billed = new AiException("Claude declined to answer this request.") { Failure = FailureClass.BadRequest, Charged = true, ChargedModel = ClaudeModel.DefaultModel, InputTokens = 100, OutputTokens = 300 };
        var model = new MeteredModel(new Fake { Throw = billed }, spending, Aud());

        await Assert.ThrowsAsync<AiException>(() => log.AskAsync(model, Request(), Context(spending: spending), TestContext.Current.CancellationToken));

        var e = Assert.Single(log.Recent(10, null));
        Assert.Equal(CallEntry.Failed, e.Outcome);
        Assert.Equal("bad-request", e.Failure);
        Assert.Equal(100, e.InputTokens);
        Assert.Equal(300, e.OutputTokens);
        Assert.Equal(0.0064m, e.Cost);
        Assert.Equal(0.0096m, e.DisplayCost);
    }

    [Fact]
    public async Task An_unexpected_failure_keeps_only_its_type_not_its_message()
    {
        using var spending = new AiSpending(_dir);
        var log = Log();
        var model = new MeteredModel(new Fake { Throw = new InvalidOperationException("PRIVATE-DATA.mkv broke it") }, spending, Aud());

        await Assert.ThrowsAsync<InvalidOperationException>(() => log.AskAsync(model, Request(), Context(spending: spending), TestContext.Current.CancellationToken));

        var e = Assert.Single(log.Recent(10, null));
        Assert.Equal("transient", e.Failure);
        Assert.Equal("The AI plugin failed (InvalidOperationException).", e.Error);
        Assert.NotNull(e.Cost); // recorded at the estimate: it may have been billed
    }

    [Fact]
    public void Error_messages_are_redacted_and_shortened()
    {
        var log = Log();

        log.RecordProblem(Context(), "ingest.match", KnownProviders.Anthropic, 10, "Rejected key " + Key + " and Bearer abcdefghijklmnop. " + string.Concat(Enumerable.Repeat("more words ", 100)), "authentication");

        var e = Assert.Single(log.Recent(10, null));
        Assert.DoesNotContain(Key, e.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", e.Error, StringComparison.Ordinal);
        Assert.Contains("[redacted]", e.Error, StringComparison.Ordinal);
        Assert.True(e.Error!.Length <= CallLog.MaxText + 1);
        Assert.EndsWith("…", e.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bridge_refusals_are_logged_except_for_plugins_switched_off()
    {
        using var spending = new AiSpending(_dir);
        var keys = new ApiKeyStore(Path.Combine(_dir, "keys.json"));
        var log = Log();
        var off = Config();
        off.AllowSubtitles = false;

        await AiBridge.AnswerAsync(BridgeRequest("subtitles", "subtitles.match"), off, keys, spending, log, null, TestContext.Current.CancellationToken);
        Assert.Empty(log.Recent(10, null));

        // A malformed request and one without a key are logged, without their contents
        await AiBridge.AnswerAsync("{nope PRIVATE", Config(), keys, spending, log, null, TestContext.Current.CancellationToken);
        await AiBridge.AnswerAsync(BridgeRequest(), Config(), keys, spending, log, null, TestContext.Current.CancellationToken);

        var calls = log.Recent(10, null);
        Assert.Equal(2, calls.Count);
        Assert.Equal(("ingest", "not-configured"), (calls[0].Caller, calls[0].Failure));
        Assert.Equal(("other", "bad-request"), (calls[1].Caller, calls[1].Failure));
        Assert.DoesNotContain("PRIVATE", await File.ReadAllTextAsync(LogPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_logged_when_the_log_is_switched_off()
    {
        using var spending = new AiSpending(_dir);
        var keys = new ApiKeyStore(Path.Combine(_dir, "keys.json"));
        var log = Log();

        await AiBridge.AnswerAsync(BridgeRequest(), Config(keep: false), keys, spending, log, _ => (new MeteredModel(new Fake(), spending, Aud()), null), TestContext.Current.CancellationToken);
        log.RecordProblem(Context(keep: false), "ingest.match", null, 0, "no", "bad-request");

        Assert.Empty(log.Recent(10, null));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void Calls_are_listed_newest_first_filtered_by_caller_and_limited()
    {
        var log = Log();
        var t = _clock.Now;
        log.Record(Entry(t.AddMinutes(1), "ingest"));
        log.Record(Entry(t.AddMinutes(2), "subtitles"));
        log.Record(Entry(t.AddMinutes(3), "ingest", CallEntry.Failed));
        log.Record(Entry(t.AddMinutes(4), "test"));

        Assert.Equal(new[] { 4, 3, 2, 1 }, log.Recent(100, null).Select(e => (e.Time - t).Minutes));
        Assert.Equal(new[] { 3, 1 }, log.Recent(100, "ingest").Select(e => (e.Time - t).Minutes));
        Assert.Equal(new[] { 4, 3 }, log.Recent(2, "").Select(e => (e.Time - t).Minutes));
        Assert.Single(log.Recent(0, null));
        Assert.Empty(log.Recent(10, "nobody"));

        var view = log.View(10, "subtitles", enabled: true);
        Assert.True(view.Enabled);
        Assert.Equal("subtitles", Assert.Single(view.Calls).Caller);
    }

    [Fact]
    public void The_last_error_is_shown_only_while_it_is_newer_than_the_last_answer()
    {
        var log = Log();
        var t = _clock.Now;
        log.Record(Entry(t, "ingest"));
        Assert.Null(log.LastError());

        log.Record(Entry(t.AddMinutes(1), "subtitles", CallEntry.Failed));
        Assert.Equal("subtitles", log.LastError()!.Caller);
        Assert.Equal("subtitles", log.View(10, "ingest", enabled: true).LastError!.Caller);

        log.Record(Entry(t.AddMinutes(2), "ingest"));
        Assert.Null(log.LastError());
    }

    [Fact]
    public void The_log_is_kept_across_restarts_and_skips_damaged_lines()
    {
        var log = Log();
        log.Record(Entry(_clock.Now, "ingest"));
        File.AppendAllText(LogPath, "{\"time\":\"cut off\n");
        log.Record(Entry(_clock.Now.AddMinutes(1), "subtitles"));

        var again = Log();
        Assert.Equal(new[] { "subtitles", "ingest" }, again.Recent(10, null).Select(e => e.Caller));

        // Trimming drops the damaged line from the file
        again.Trim();
        Assert.Equal(2, File.ReadAllLines(LogPath).Length);
    }

    [Fact]
    public void The_log_keeps_the_last_thousand_calls_and_thirty_days()
    {
        var log = Log();
        var start = _clock.Now;
        for (var i = 0; i < 1250; i++)
        {
            log.Record(Entry(start.AddSeconds(i), i % 2 == 0 ? "ingest" : "subtitles"));
        }

        // Trimmed as it grows, with a little slack so the file isn't rewritten on every call
        Assert.True(log.Recent(CallLog.MaxEntries, null).Count <= CallLog.MaxEntries);
        log.Trim();
        Assert.Equal(CallLog.MaxEntries, File.ReadAllLines(LogPath).Length);
        Assert.Equal(start.AddSeconds(1249), log.Recent(1, null)[0].Time);
        Assert.Equal(start.AddSeconds(250), Log().Recent(CallLog.MaxEntries, null)[^1].Time);

        // A month later, only calls from the last 30 days are kept
        _clock.Now = start.AddDays(30).AddSeconds(1000);
        log.Record(Entry(_clock.Now, "test"));
        log.Trim();
        Assert.True(Log().Recent(CallLog.MaxEntries, null).All(e => e.Time >= _clock.Now - CallLog.KeptFor));
        Assert.Equal(250 + 1, File.ReadAllLines(LogPath).Length);
        Assert.True(new FileInfo(LogPath).Length <= CallLog.MaxFileBytes);
    }

    [Fact]
    public void The_file_stays_under_its_size_cap()
    {
        var log = Log();
        var big = new string('e', CallLog.MaxText);
        for (var i = 0; i < 3000; i++)
        {
            // Far larger lines than the log writes itself, to reach the cap before the count
            log.Record(Entry(_clock.Now, "ingest", CallEntry.Failed) with { Error = big + big + big, Summary = big + big });
        }

        Assert.True(new FileInfo(LogPath).Length <= CallLog.MaxFileBytes + 4096);
        log.Trim();
        Assert.True(new FileInfo(LogPath).Length <= CallLog.MaxFileBytes * 3 / 4);
    }

    [Fact]
    public void The_file_is_owner_only_and_can_be_cleared()
    {
        var log = Log();
        log.Record(Entry(_clock.Now));
        log.Trim();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LogPath));
        }

        log.Clear();
        Assert.Empty(log.Recent(10, null));
        Assert.Null(log.LastError());
        Assert.False(File.Exists(LogPath));
        Assert.Empty(Log().Recent(10, null));
    }

    [Fact]
    public void A_trimmed_file_is_owner_only_too()
    {
        var log = Log();
        _clock.Now = _clock.Now.AddDays(-40);
        log.Record(Entry(_clock.Now));
        _clock.Now = _clock.Now.AddDays(40);
        log.Record(Entry(_clock.Now));
        log.Trim();

        Assert.Single(File.ReadAllLines(LogPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LogPath));
        }
    }

    [Theory]
    [InlineData("{\"ok\":true}", "ok=true")]
    [InlineData("{\"items\":[1,2,3],\"more\":{\"a\":1},\"none\":null}", "items=list (3 items), more=object (1 fields), none=null")]
    [InlineData("\"free text answer\"", "text (16 chars)")]
    public void Answers_are_summarised_by_their_shape(string json, string summary)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(summary, CallLog.Summarise(doc.RootElement));
    }

    private sealed class Clock : TimeProvider
    {
        // Today (whole seconds), so the exchange rates written for today count as current
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fake : IAiModel
    {
        public string Provider => KnownProviders.Anthropic;

        public string Model => ClaudeModel.DefaultModel;

        public object Answer { get; init; } = new { ok = true };

        public Exception? Throw { get; init; }

        public Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken)
            => Throw is not null
                ? throw Throw
                : Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(Answer), Provider, Model, 100, 300));
    }
}
