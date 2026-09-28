using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Bridge;
using Jellyfin.Plugin.Ai.Calls;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Health;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Ai;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// OpenAI, Gemini and OpenAI-compatible providers: model choice, metering, routing and health
public sealed class ProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-providers-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private readonly FakeHttp _fake = new();
    private readonly HttpClient _http;

    public ProviderTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // 1 EUR = 1.10 USD = 1.65 AUD, so 1 USD = 1.5 AUD
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
        _http = _fake.Client();
    }

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static PluginConfiguration Config(params ProviderSettings[] providers)
    {
        var c = new PluginConfiguration { AllowIngest = true, Currency = "AUD", OverallMonthlyBudget = 5m };
        foreach (var p in providers)
        {
            c.Providers.Add(p);
        }

        Budgets.BudgetRules.Normalise(c);
        return c;
    }

    private static ProviderSettings On(string id, string url = "", string model = "") => new() { Id = id, Enabled = true, BaseUrl = url, Model = model };

    private ApiKeyStore Keys(params string[] withKeys)
    {
        var keys = new ApiKeyStore(Path.Combine(_dir, "keys.json"));
        foreach (var id in withKeys)
        {
            keys.Set(id, "key-for-" + id + "-12345");
        }

        return keys;
    }

    private static SpendLimits Aud() => new("AUD", 5m, new Dictionary<string, decimal>(), 0m);

    private static string BridgeRequest()
        => "{\"version\":1,\"caller\":\"ingest\",\"purpose\":\"ingest.match\",\"instructions\":\"Pick one.\",\"data\":{\"file\":\"x\"},\"schema\":{\"type\":\"object\"},\"maxOutputTokens\":1000}";

    // ---- Model families and the automatic choice ----

    [Fact]
    public void The_newest_priced_model_of_a_family_is_picked()
    {
        var sol = ModelCatalog.Family(KnownProviders.OpenAi, null)!;
        Assert.Equal("sol", sol.Id);
        Assert.True(sol.IsDefault);

        var listed = new[] { "gpt-5.6-sol", "gpt-6-sol", "gpt-6-sol-2026-07-01", "gpt-6-luna", "gpt-7-sol", "gpt-6-sol-realtime" };

        // gpt-7-sol has no price yet, and dated snapshots and variants never match
        Assert.Equal("gpt-6-sol", ModelCatalog.Newest(sol, listed, id => id != "gpt-7-sol"));
        Assert.Equal("gpt-7-sol", ModelCatalog.Newest(sol, listed, _ => true));
        Assert.Equal("gpt-6-luna", ModelCatalog.Newest(ModelCatalog.Family(KnownProviders.OpenAi, "luna")!, listed, _ => true));
        Assert.Null(ModelCatalog.Newest(ModelCatalog.Family(KnownProviders.OpenAi, "astra")!, listed, _ => true));
    }

    [Fact]
    public void A_stable_gemini_is_preferred_and_a_preview_used_only_when_there_is_none()
    {
        var flash = ModelCatalog.Family(KnownProviders.Google, "flash")!;
        var pro = ModelCatalog.Family(KnownProviders.Google, "pro")!;
        var listed = new[] { "gemini-3.8-flash", "gemini-3.9-flash-preview", "gemini-3.8-flash-lite", "gemini-3.1-pro-preview", "gemini-2.5-flash" };

        Assert.Equal("gemini-3.8-flash", ModelCatalog.Newest(flash, listed, _ => true));
        Assert.Equal("gemini-3.1-pro-preview", ModelCatalog.Newest(pro, listed, _ => true));
        Assert.Null(ModelCatalog.Family(KnownProviders.OpenAiCompatible, null));
        Assert.Null(ModelCatalog.Family(KnownProviders.Anthropic, null));
    }

    [Fact]
    public void Every_family_fallback_has_a_shipped_price()
    {
        using var spending = new AiSpending(_dir);

        Assert.All(ModelCatalog.Families, f => Assert.True(AiModels.Priced(spending, f.Provider, f.Fallback), f.Fallback));

        // gpt-6-sol: 2 / 10 USD per million; gemini-3.8-flash: 0.75 / 3.75
        Assert.Equal(Money.Of(0.03m, "USD"), spending.Cost(KnownProviders.OpenAi, "gpt-6-sol", 10_000, 1_000));
        Assert.Equal(Money.Of(0.01125m, "USD"), spending.Cost(KnownProviders.Google, "gemini-3.8-flash", 10_000, 1_000));
    }

    [Fact]
    public async Task The_choice_is_checked_once_a_day_and_changes_are_logged()
    {
        var logger = new ListLogger();
        using var resolver = new ModelResolver(logger, _clock);
        var family = ModelCatalog.Family(KnownProviders.OpenAi, null)!;
        var lists = 0;
        var offered = new List<string> { "gpt-5.6-sol" };
        Task<IReadOnlyList<string>> List(CancellationToken ct)
        {
            lists++;
            return Task.FromResult<IReadOnlyList<string>>([.. offered]);
        }

        var first = await resolver.ResolveAsync(family, List, _ => true, TestContext.Current.CancellationToken);
        offered.Add("gpt-6-sol");
        var same = await resolver.ResolveAsync(family, List, _ => true, TestContext.Current.CancellationToken);
        _clock.Now += TimeSpan.FromDays(1);
        var next = await resolver.ResolveAsync(family, List, _ => true, TestContext.Current.CancellationToken);

        Assert.Equal("gpt-5.6-sol", first.Model);
        Assert.Equal("gpt-5.6-sol", same.Model);
        Assert.Equal("gpt-6-sol", next.Model);
        Assert.Equal(2, lists);
        Assert.Contains(logger.Lines, l => l.Contains("gpt-6-sol (was gpt-5.6-sol)", StringComparison.Ordinal));
        Assert.Equal("gpt-6-sol", Assert.Single(resolver.Current()).Model);
    }

    [Fact]
    public async Task An_unreadable_list_keeps_the_last_choice_or_uses_the_fallback()
    {
        var logger = new ListLogger();
        using var resolver = new ModelResolver(logger, _clock);
        var family = ModelCatalog.Family(KnownProviders.Google, null)!;
        static Task<IReadOnlyList<string>> Broken(CancellationToken ct) => throw new AiException("Gemini couldn't be reached.") { Failure = FailureClass.NoConnection };

        var fallback = await resolver.ResolveAsync(family, Broken, _ => true, TestContext.Current.CancellationToken);

        Assert.Equal(family.Fallback, fallback.Model);
        Assert.False(fallback.FromList);
        Assert.Contains("couldn't be reached", fallback.Problem, StringComparison.Ordinal);
        Assert.Contains(logger.Lines, l => l.Contains("couldn't be read", StringComparison.Ordinal));

        // Tried again after an hour, not a day
        _clock.Now += ModelResolver.RetryEvery;
        var listed = await resolver.ResolveAsync(family, _ => Task.FromResult<IReadOnlyList<string>>(["gemini-3.9-flash"]), _ => true, TestContext.Current.CancellationToken);
        Assert.Equal("gemini-3.9-flash", listed.Model);
    }

    // ---- Building models ----

    [Fact]
    public async Task OpenAI_uses_the_current_model_of_its_family_and_is_metered()
    {
        using var spending = new AiSpending(_dir);
        using var resolver = new ModelResolver(NullLogger.Instance, _clock);
        _fake.Reply("{\"object\":\"list\",\"data\":[{\"id\":\"gpt-6-sol\",\"object\":\"model\",\"created\":1,\"owned_by\":\"openai\"},{\"id\":\"gpt-5.6-sol\",\"object\":\"model\",\"created\":1,\"owned_by\":\"openai\"}]}");
        var services = new ModelServices(Keys(KnownProviders.OpenAi), spending, resolver, null, _http);

        var (model, problem) = await AiModels.CreateAsync(Config(On(KnownProviders.OpenAi)), KnownProviders.OpenAi, null, services, null, TestContext.Current.CancellationToken);

        Assert.Null(problem);
        Assert.IsType<MeteredModel>(model);
        Assert.Equal("gpt-6-sol", model!.Model);
    }

    [Fact]
    public async Task A_pinned_model_is_used_as_it_is()
    {
        using var spending = new AiSpending(_dir);
        var services = new ModelServices(Keys(KnownProviders.Google), spending, null, null, _http);

        var (model, _) = await AiModels.CreateAsync(Config(On(KnownProviders.Google, model: "gemini-2.5-pro")), KnownProviders.Google, null, services, null, TestContext.Current.CancellationToken);

        Assert.Equal("gemini-2.5-pro", model!.Model);
        Assert.Empty(_fake.Seen);
        (model as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(KnownProviders.OpenAi, "OpenAI API key")]
    [InlineData(KnownProviders.Google, "Gemini API key")]
    public async Task A_paid_provider_needs_its_key(string id, string words)
    {
        using var spending = new AiSpending(_dir);

        var (model, problem) = await AiModels.CreateAsync(Config(On(id)), id, null, new ModelServices(Keys(), spending), null, TestContext.Current.CancellationToken);

        Assert.Null(model);
        Assert.Contains(words, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_compatible_service_is_free_and_needs_no_key()
    {
        using var spending = new AiSpending(_dir);
        var config = Config(On(KnownProviders.OpenAiCompatible, "http://127.0.0.1:11434/v1", "llama3.3"));

        // Even with no paid use allowed at all
        config.OverallMonthlyBudget = 0;
        var (model, problem) = await AiModels.CreateAsync(config, KnownProviders.OpenAiCompatible, null, new ModelServices(Keys(), spending, TestHttp: _http), null, TestContext.Current.CancellationToken);

        Assert.Null(problem);
        Assert.IsType<FreeModel>(model);
    }

    [Theory]
    [InlineData("http://api.example.com/v1", "llama", "https://")]
    [InlineData("https://api.example.com/v1", "", "name the model")]
    [InlineData("https://api.example.com/v1", "llama", "prices aren't known")]
    public async Task A_remote_compatible_service_needs_https_a_model_and_prices(string url, string model, string words)
    {
        using var spending = new AiSpending(_dir);

        var (built, problem) = await AiModels.CreateAsync(Config(On(KnownProviders.OpenAiCompatible, url, model)), KnownProviders.OpenAiCompatible, null, new ModelServices(Keys(), spending), null, TestContext.Current.CancellationToken);

        Assert.Null(built);
        Assert.Contains(words, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_priced_compatible_service_is_metered_at_its_prices_or_what_it_reports()
    {
        using var spending = new AiSpending(_dir);
        var settings = On(KnownProviders.OpenAiCompatible, "https://openrouter.ai/api/v1", "openai/gpt-6-sol");
        settings.InputPrice = 2m;
        settings.OutputPrice = 10m;
        var config = Config(settings);
        var services = new ModelServices(Keys(KnownProviders.OpenAiCompatible), spending, TestHttp: _http);

        _fake.Reply(OpenAiChatModelTests.Completion("{\"pick\":1}", model: "openai/gpt-6-sol"))
            .Reply(OpenAiChatModelTests.Completion("{\"pick\":1}", model: "openai/gpt-6-sol", usage: "{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150,\"cost\":0.5}"));

        var (model, _) = await AiModels.CreateAsync(config, KnownProviders.OpenAiCompatible, null, services, null, TestContext.Current.CancellationToken);
        var metered = Assert.IsType<MeteredModel>(model);
        await metered.AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken);

        // 120 in + 30 out at 2 / 10 USD per million
        Assert.Equal(Money.Of(0.00054m, "USD"), metered.LastCost);

        // The service's own figure wins
        await metered.AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken);
        Assert.Equal(Money.Of(0.5m, "USD"), metered.LastCost);
        Assert.Equal(Authorization(KnownProviders.OpenAiCompatible), _fake.Seen[0].Authorization);
    }

    [Fact]
    public async Task A_reply_without_token_counts_is_recorded_at_the_estimate()
    {
        using var spending = new AiSpending(_dir);
        var fake = new Scripted(KnownProviders.OpenAi, "gpt-6-sol", _ => Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(new { ok = true }), KnownProviders.OpenAi, "gpt-6-sol", 0, 0) { UsageKnown = false }));
        var metered = new MeteredModel(fake, spending, Aud());

        await metered.AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken);

        Assert.True(metered.LastCost!.Value.Amount > 0);
    }

    [Fact]
    public async Task A_free_call_is_logged_as_free_and_not_metered()
    {
        using var spending = new AiSpending(_dir);
        var log = new CallLog(Path.Combine(_dir, CallLog.FileName), _clock);
        var free = new FreeModel(new Scripted(KnownProviders.OpenAiCompatible, "llama3.3", _ => Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(new { ok = true }), KnownProviders.OpenAiCompatible, "llama3.3", 10, 5))));

        await log.AskAsync(free, OpenAiChatModelTests.Request(), new CallContext("test", true, Aud(), spending.Rates.Current, []), TestContext.Current.CancellationToken);

        var entry = Assert.Single(log.Recent(10, null));
        Assert.True(entry.Unmetered);
        Assert.Equal("free", CallPresenter.Present(entry).Cost);
        Assert.Equal(0m, spending.Ledger.ThisMonth(Aud(), spending.Rates.Current).Total);
    }

    // ---- Which provider answers ----

    [Fact]
    public async Task The_default_provider_answers_and_the_next_one_steps_in_when_it_cant()
    {
        using var spending = new AiSpending(_dir);
        var config = Config(On(KnownProviders.Anthropic), On(KnownProviders.OpenAi), On(KnownProviders.Google));
        config.DefaultProvider = KnownProviders.Google;
        config.FallbackProviders.Add(KnownProviders.Anthropic);
        config.FallbackProviders.Add(KnownProviders.OpenAi);
        var asked = new List<string>();
        var log = new CallLog(Path.Combine(_dir, CallLog.FileName), _clock);
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);

        var reply = await AiBridge.AnswerAsync(BridgeRequest(), config, new ModelServices(Keys(), spending), log, health, (p, _) =>
        {
            asked.Add(p);
            return Task.FromResult<(IAiModel?, string?)>(p switch
            {
                KnownProviders.Google => (null, "Add a Gemini API key first (from Google AI Studio)."),
                KnownProviders.Anthropic => (new Scripted(p, "claude-opus-5-5", _ => throw new AiException("Claude didn't accept the API key.") { Failure = FailureClass.Authentication }), null),
                _ => (new Scripted(p, "gpt-6-sol", _ => Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(new { pick = 1 }), p, "gpt-6-sol", 1, 1))), null),
            });
        }, TestContext.Current.CancellationToken);

        var read = AiBridgeClient.Read(reply);
        Assert.True(read.Ok);
        Assert.Equal("gpt-6-sol", read.Model);
        Assert.Equal([KnownProviders.Google, KnownProviders.Anthropic, KnownProviders.OpenAi], asked);

        // Each attempt is in the call log; the refused key makes Anthropic's trouble systemic
        Assert.Equal(3, log.Recent(10, null).Count);
        var anthropic = health.Health(config).Single(h => h.Provider == KnownProviders.Anthropic);
        Assert.True(anthropic.Systemic);
        Assert.Contains("console.anthropic.com", anthropic.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "Transient")]
    [InlineData(false, "BadRequest")]
    public async Task A_charged_or_rejected_request_isnt_sent_elsewhere(bool charged, string failure)
    {
        using var spending = new AiSpending(_dir);
        var config = Config(On(KnownProviders.Anthropic), On(KnownProviders.OpenAi));
        config.FallbackProviders.Add(KnownProviders.OpenAi);
        var asked = new List<string>();

        var reply = await AiBridge.AnswerAsync(BridgeRequest(), config, new ModelServices(Keys(), spending), null, null, (p, _) =>
        {
            asked.Add(p);
            return Task.FromResult<(IAiModel?, string?)>((new Scripted(p, "m", _ => throw new AiException("No good.") { Failure = Enum.Parse<FailureClass>(failure), Charged = charged }), null));
        }, TestContext.Current.CancellationToken);

        Assert.False(AiBridgeClient.Read(reply).Ok);
        Assert.Equal([KnownProviders.Anthropic], asked);
    }

    [Fact]
    public async Task When_no_provider_can_answer_the_last_reason_is_given()
    {
        using var spending = new AiSpending(_dir);
        var config = Config(On(KnownProviders.Anthropic), On(KnownProviders.OpenAi));
        config.FallbackProviders.Add(KnownProviders.OpenAi);

        var reply = await AiBridge.AnswerAsync(BridgeRequest(), config, new ModelServices(Keys(), spending), null, null, (p, _) =>
            Task.FromResult<(IAiModel?, string?)>((new Scripted(p, "m", _ => throw new AiException(p + " is out of credit.") { Failure = FailureClass.ProviderLimit }), null)), TestContext.Current.CancellationToken);

        var read = AiBridgeClient.Read(reply);
        Assert.Equal("provider-limit", read.Failure);
        Assert.Equal("openai is out of credit.", read.Error);
    }

    [Fact]
    public async Task A_provider_over_its_spending_limit_passes_to_the_next()
    {
        using var spending = new AiSpending(_dir);
        var config = Config(On(KnownProviders.Anthropic), On(KnownProviders.OpenAi));
        config.FallbackProviders.Add(KnownProviders.OpenAi);
        var zero = new SpendLimits("AUD", 5m, new Dictionary<string, decimal> { [KnownProviders.Anthropic] = 0m }, 0m);

        var reply = await AiBridge.AnswerAsync(BridgeRequest(), config, new ModelServices(Keys(), spending), null, null, (p, _) =>
            Task.FromResult<(IAiModel?, string?)>((new MeteredModel(new Scripted(p, p == KnownProviders.Anthropic ? ClaudeModel.DefaultModel : "gpt-6-sol", r => Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(new { pick = 1 }), p, p == KnownProviders.Anthropic ? ClaudeModel.DefaultModel : "gpt-6-sol", 10, 10))), spending, zero), null)), TestContext.Current.CancellationToken);

        Assert.Equal("gpt-6-sol", AiBridgeClient.Read(reply).Model);
    }

    // ---- Health ----

    [Fact]
    public void One_off_failures_stay_quiet_and_repeated_ones_make_a_banner()
    {
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);
        var config = Config(On(KnownProviders.OpenAi));
        health.Record(Failed(KnownProviders.OpenAi, "transient"));
        for (var i = 0; i < 4; i++)
        {
            health.Record(Answered(KnownProviders.OpenAi));
        }

        Assert.False(health.Health(config).Single().Systemic);

        for (var i = 0; i < ProviderHealthLog.FailuresForSystemic; i++)
        {
            health.Record(Failed(KnownProviders.OpenAi, "no-connection"));
        }

        var bad = health.Health(config).Single();
        Assert.True(bad.Systemic);
        Assert.Contains("status.openai.com", bad.Problem, StringComparison.Ordinal);

        // Cleared by a few successes in a row, and it survives a restart
        for (var i = 0; i < ProviderHealthLog.SuccessesToClear; i++)
        {
            health.Record(Answered(KnownProviders.OpenAi));
        }

        Assert.False(new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock).Health(config).Single().Systemic);
    }

    [Fact]
    public void Refusals_by_the_spending_limits_and_missing_set_up_say_nothing_about_a_provider()
    {
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);
        health.Record(new CallEntry { Provider = KnownProviders.Google, Outcome = CallEntry.Refused, Failure = "spending-limit" });
        health.Record(Failed(KnownProviders.Google, "not-configured"));
        health.Record(Failed(KnownProviders.Google, "cancelled"));

        Assert.Empty(health.Health(Config()));
    }

    [Theory]
    [InlineData(KnownProviders.Google, "provider-limit", "Google AI Studio")]
    [InlineData(KnownProviders.OpenAi, "authentication", "platform.openai.com/api-keys")]
    [InlineData(KnownProviders.OpenAiCompatible, "no-connection", "Is it running")]
    public void The_banner_says_what_to_do_for_that_provider(string provider, string failure, string words)
    {
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);
        var config = Config(On(KnownProviders.OpenAiCompatible, "http://192.168.1.5:8080/v1", "m"));
        for (var i = 0; i < ProviderHealthLog.FailuresForSystemic; i++)
        {
            health.Record(Failed(provider, failure));
        }

        var h = health.Health(config).Single();
        Assert.True(h.Systemic);
        Assert.Contains(words, h.Problem, StringComparison.Ordinal);

        // A new key clears the record
        health.Forget(provider);
        Assert.Empty(health.Health(config));
    }

    [Fact]
    public void A_day_old_problem_fades()
    {
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);
        health.Record(Failed(KnownProviders.Anthropic, "authentication"));
        Assert.True(health.Health(Config()).Single().Systemic);

        _clock.Now += TimeSpan.FromHours(26);

        Assert.False(health.Health(Config()).Single().Systemic);
    }

    [Fact]
    public async Task Health_is_kept_when_the_call_log_is_off()
    {
        using var spending = new AiSpending(_dir);
        var log = new CallLog(Path.Combine(_dir, CallLog.FileName), _clock);
        var health = new ProviderHealthLog(Path.Combine(_dir, ProviderHealthLog.FileName), _clock);
        var failing = new Scripted(KnownProviders.Google, "gemini-3.8-flash", _ => throw new AiException("Gemini didn't accept the API key.") { Failure = FailureClass.Authentication });

        await Assert.ThrowsAsync<AiException>(() => log.AskAsync(failing, OpenAiChatModelTests.Request(), new CallContext("ingest", false, Aud(), null, [], health), TestContext.Current.CancellationToken));

        Assert.Empty(log.Recent(10, null));
        Assert.True(health.Health(Config()).Single().Systemic);
    }

    private static CallEntry Answered(string provider) => new() { Provider = provider, Outcome = CallEntry.Answered };

    private static CallEntry Failed(string provider, string failure) => new() { Provider = provider, Outcome = CallEntry.Failed, Failure = failure, Error = "down" };

    private static string Authorization(string id) => "Bearer key-for-" + id + "-12345";

    private sealed class Scripted(string provider, string model, Func<AiRequest, Task<AiAnswer>> ask) : IAiModel
    {
        public string Provider => provider;

        public string Model => model;

        public Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken) => ask(request);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
