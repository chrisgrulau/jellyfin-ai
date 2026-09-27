using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Api;
using Jellyfin.Plugin.Ai.Bridge;
using Jellyfin.Plugin.Ai.Budgets;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// One budget page: common's real spending client against this server (FAM-04 style), and the speech providers' limits
public sealed class SpendingBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-spendbridge-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static PluginConfiguration Config(decimal overall = 5m, decimal? deepgram = null, bool allow = true)
    {
        var c = new PluginConfiguration { Currency = "USD", OverallMonthlyBudget = overall, AllowSubtitlesSpending = allow };
        BudgetRules.Normalise(c);
        if (deepgram is { } d)
        {
            var p = c.Providers.Single(p => p.Id == KnownProviders.Deepgram);
            p.BudgetMode = ProviderBudgetMode.Amount;
            p.BudgetValue = d;
        }

        return c;
    }

    private AiSpending Spending() => new(_dir, null, _clock);

    // Common's client, compiled into this plugin as into Subtitles, talking to this server's entry point work
    private SpendingBridgeClient Client(PluginConfiguration config, AiSpending spending, string caller = "subtitles")
        => new(caller, (json, ct) => SpendingBridge.AnswerAsync(json, config, spending, null, _clock, ct));

    [Fact]
    public void The_server_is_where_the_client_looks_for_it()
    {
        Assert.Equal(SpendingBridgeClient.AssemblyName, typeof(SpendingBridge).Assembly.GetName().Name);
        Assert.Equal(SpendingBridgeClient.TypeName, typeof(SpendingBridge).FullName);
        Assert.Equal(SpendingBridgeClient.Version, SpendingBridge.Version);
        var method = typeof(SpendingBridge).GetMethod(SpendingBridgeClient.MethodName, BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(CancellationToken)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<string>), method.ReturnType);
    }

    [Fact]
    public void The_clients_entry_point_finder_locates_the_server()
    {
        // Not called: the entry point reaches the plugin instance, whose Jellyfin base types tests can't load
        var find = typeof(SpendingBridgeClient).GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(find);
        Assert.IsType<Func<string, CancellationToken, Task<string>>>(find.Invoke(null, null));
        Assert.True(new SpendingBridgeClient("subtitles").IsInstalled);
    }

    [Fact]
    public async Task A_reservation_is_made_settled_and_counted_once()
    {
        using var spending = Spending();
        var config = Config();
        var client = Client(config, spending);
        var ct = TestContext.Current.CancellationToken;

        var reserved = await client.ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(1m, "USD"), ct);
        Assert.True(reserved.Ok);
        Assert.True((await client.SettleAsync(reserved.ReservationId!.Value, Money.Of(0.25m, "USD"), ct)).Ok);

        // Another plugin's purpose, metered by Subtitles for it (Ingest's short transcripts)
        var released = await client.ReserveAsync("ingest.episode", KnownProviders.OpenAiSpeech, Money.Of(1m, "USD"), ct);
        Assert.True((await client.ReleaseAsync(released.ReservationId!.Value, ct)).Ok);
        Assert.Equal("bad-request", (await client.ReleaseAsync(released.ReservationId!.Value, ct)).Failure);

        var summary = await client.SummaryAsync(ct);
        Assert.True(summary.Ok);
        Assert.Equal("USD", summary.Currency);
        Assert.Equal(5m, summary.Limit);
        Assert.Equal(0.25m, summary.Spent);
        Assert.Equal(0.25m, summary.PerProvider[KnownProviders.Deepgram]);
        Assert.False(summary.PerProvider.ContainsKey(KnownProviders.OpenAiSpeech));
    }

    [Fact]
    public async Task The_limits_set_here_refuse_speech_to_text_too()
    {
        using var spending = Spending();
        var config = Config(overall: 5m, deepgram: 1m);
        var client = Client(config, spending);
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await client.ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(0.8m, "USD"), ct)).Ok);
        var refused = await client.ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(0.3m, "USD"), ct);
        Assert.False(refused.Ok);
        Assert.Equal("provider-limit", refused.Failure);
        Assert.Contains("deepgram", refused.Error, StringComparison.Ordinal);
        Assert.Equal(1m, (await client.SummaryAsync(ct)).ProviderLimits[KnownProviders.Deepgram]);

        // Claude's spending counts against the same overall limit
        spending.Ledger.TryReserve(KnownProviders.Anthropic, "ingest.match", Money.Of(4m, "USD"), AiSpending.LimitsOf(config), null);
        Assert.Equal("provider-limit", (await client.ReserveAsync("subtitles.sync", KnownProviders.OpenAiSpeech, Money.Of(0.5m, "USD"), ct)).Failure);

        // A limit of 0 means no paid use
        using var other = Spending();
        Assert.Contains("is 0", (await Client(Config(overall: 0m), other).ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(0.01m, "USD"), ct)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cost_that_cant_be_converted_is_refused_not_counted_as_free()
    {
        using var spending = Spending();
        var config = Config();
        config.Currency = "AUD";
        var refused = await Client(config, spending).ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(0.1m, "USD"), TestContext.Current.CancellationToken);
        Assert.Equal("provider-limit", refused.Failure);
        Assert.Contains("AUD", refused.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reservation_left_open_for_an_hour_expires_at_its_estimate()
    {
        using var spending = Spending();
        var client = Client(Config(), spending);
        var ct = TestContext.Current.CancellationToken;
        var reserved = await client.ReserveAsync("subtitles.generate", KnownProviders.Deepgram, Money.Of(2m, "USD"), ct);

        _clock.Now += TimeSpan.FromMinutes(61);
        Assert.Equal(2m, (await client.SummaryAsync(ct)).Spent);
        var late = await client.SettleAsync(reserved.ReservationId!.Value, Money.Of(0.5m, "USD"), ct);
        Assert.False(late.Ok);
        Assert.Equal("bad-request", late.Failure);
        Assert.Equal(2m, (await client.SummaryAsync(ct)).Spent);
    }

    [Fact]
    public async Task Subtitles_own_spending_this_month_is_carried_once()
    {
        using var spending = Spending();
        var client = Client(Config(), spending);
        var ct = TestContext.Current.CancellationToken;
        var month = SpendingBridgeClient.MonthOf(_clock.GetLocalNow());

        Assert.True((await client.CarryAsync(KnownProviders.OpenAiSpeech, Money.Of(1.5m, "USD"), month, ct)).Ok);
        Assert.True((await client.CarryAsync(KnownProviders.OpenAiSpeech, Money.Of(1.75m, "USD"), month, ct)).Ok);
        Assert.Equal("bad-request", (await client.CarryAsync(KnownProviders.OpenAiSpeech, Money.Of(9m, "USD"), "2026-08", ct)).Failure);
        Assert.Equal("bad-request", (await client.CarryAsync("anthropic", Money.Of(9m, "USD"), month, ct)).Failure);

        Assert.Equal(1.75m, (await client.SummaryAsync(ct)).Spent);
    }

    [Theory]
    [InlineData("{\"version\":2,\"caller\":\"subtitles\",\"op\":\"summary\"}", "unsupported-version")]
    [InlineData("{\"version\":1,\"caller\":\"ingest\",\"op\":\"summary\"}", "not-allowed")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"spend\"}", "bad-request")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"reserve\",\"purpose\":\"subtitles.sync\",\"provider\":\"anthropic\",\"estimate\":{\"amount\":1,\"currency\":\"USD\"}}", "bad-request")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"reserve\",\"purpose\":\"subtitles.sync\",\"provider\":\"deepgram\",\"estimate\":{\"amount\":-1,\"currency\":\"USD\"}}", "bad-request")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"reserve\",\"purpose\":\"subtitles.sync\",\"provider\":\"deepgram\",\"estimate\":{\"amount\":1,\"currency\":\"XYZ\"}}", "bad-request")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"reserve\",\"purpose\":\"../x\",\"provider\":\"deepgram\",\"estimate\":{\"amount\":1,\"currency\":\"USD\"}}", "bad-request")]
    [InlineData("{\"version\":1,\"caller\":\"subtitles\",\"op\":\"settle\",\"reservationId\":\"nope\"}", "bad-request")]
    [InlineData("[1]", "bad-request")]
    [InlineData("not json", "bad-request")]
    public async Task Bad_or_unexpected_requests_are_refused_with_a_failure_name(string request, string failure)
    {
        using var spending = Spending();
        var reply = SpendingBridgeClient.Read(await SpendingBridge.AnswerAsync(request, Config(), spending, null, _clock, TestContext.Current.CancellationToken));
        Assert.False(reply.Ok);
        Assert.Equal(failure, reply.Failure);
    }

    [Fact]
    public async Task With_the_setting_off_Subtitles_is_told_to_use_its_own_budget()
    {
        using var spending = Spending();
        var reply = await Client(Config(allow: false), spending).ReserveAsync("subtitles.sync", KnownProviders.Deepgram, Money.Of(1m, "USD"), TestContext.Current.CancellationToken);
        Assert.Equal("not-allowed", reply.Failure);
        Assert.True(SpendingBridgeClient.MeansOwnBudget(reply.Failure));
        Assert.Equal(0m, spending.Ledger.ThisMonth(AiSpending.LimitsOf(Config()), null).Total);
    }

    [Fact]
    public async Task Through_the_bridged_meter_a_call_is_counted_here_only()
    {
        using var spending = Spending();
        var own = new SpendLedger(Path.Combine(_dir, "own.json"), _clock);
        var ownLimits = new SpendLimits("USD", 5m, new Dictionary<string, decimal>(), 0m);
        var meter = new BridgedSpendMeter(Client(Config(), spending), () => new LocalSpendMeter(own, ownLimits, () => null), p => p == "openai" ? KnownProviders.OpenAiSpeech : p);

        await MeteredCall.RunAsync(meter, "openai", "subtitles.sync", Money.Of(1m, "USD"), _ => Task.FromResult(1), _ => Money.Of(0.2m, "USD"), null, TestContext.Current.CancellationToken);

        Assert.Equal(0.2m, spending.Ledger.ThisMonth(AiSpending.LimitsOf(Config()), null).PerProvider[KnownProviders.OpenAiSpeech]);
        Assert.Equal(0m, own.ThisMonth(ownLimits, null).Total);
    }

    [Fact]
    public void Speech_providers_have_limits_but_no_switch()
    {
        var c = Config(overall: 10m, deepgram: 2m);
        var speech = c.Providers.Single(p => p.Id == KnownProviders.OpenAiSpeech);
        speech.BudgetMode = ProviderBudgetMode.PercentOfOverall;
        speech.BudgetValue = 30;

        var limits = AiSpending.LimitsOf(c);
        Assert.Equal(2m, limits.PerProvider[KnownProviders.Deepgram]);
        Assert.Equal(3m, limits.PerProvider[KnownProviders.OpenAiSpeech]);

        // A percentage needs an overall limit, for speech as for the others; they're added up with the others
        c.NoOverallLimit = true;
        Assert.Contains(BudgetRules.Check(c), m => m.Severity == BudgetSeverity.Error && m.Message.Contains("OpenAI speech-to-text", StringComparison.Ordinal));
        c.NoOverallLimit = false;
        c.OverallMonthlyBudget = 1m;
        Assert.Contains(BudgetRules.Check(c), m => m.Message.Contains("add up to", StringComparison.Ordinal));
    }

    [Fact]
    public void Speech_rows_are_listed_while_Subtitles_is_installed_or_in_use()
    {
        var c = Config();
        Assert.Equal(KnownProviders.Speech, AiController.SpeechShown(c, new Dictionary<string, decimal>(), subtitlesInstalled: true));
        Assert.Empty(AiController.SpeechShown(c, new Dictionary<string, decimal>(), subtitlesInstalled: false));
        Assert.Equal([KnownProviders.OpenAiSpeech], AiController.SpeechShown(c, new Dictionary<string, decimal> { [KnownProviders.OpenAiSpeech] = 1m }, subtitlesInstalled: false));
        Assert.Equal([KnownProviders.Deepgram], AiController.SpeechShown(Config(deepgram: 1m), new Dictionary<string, decimal>(), subtitlesInstalled: false));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
