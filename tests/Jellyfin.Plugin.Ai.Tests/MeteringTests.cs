using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

public sealed class MeteringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-metering-" + Guid.NewGuid().ToString("N"));

    public MeteringTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // 1 EUR = 1.10 USD = 1.65 AUD, so 1 USD = 1.5 AUD
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AiRequest Request(int maxOutput = 1000) => new()
    {
        Purpose = "test",
        Instructions = "Pick one.",
        Data = "{}",
        Schema = new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") },
        MaxOutputTokens = maxOutput,
    };

    private static SpendLimits Aud(decimal? overall) => new("AUD", overall, new Dictionary<string, decimal>(), 0m);

    [Fact]
    public void The_shipped_prices_load_and_cost_tokens()
    {
        using var spending = new AiSpending(_dir);

        // 10,000 input and 2,000 output tokens of Claude Opus 5.5: 0.04 + 0.04 USD
        Assert.Equal(Money.Of(0.08m, "USD"), spending.Cost("anthropic", ClaudeModel.DefaultModel, 10_000, 2_000));
        Assert.NotNull(spending.Cost("anthropic", "claude-sonnet-5", 1, 1));
        Assert.Null(spending.Cost("anthropic", "claude-unknown", 1, 1));
    }

    [Fact]
    public async Task A_call_is_recorded_at_its_actual_cost()
    {
        using var spending = new AiSpending(_dir);
        var fake = new Fake { Output = 500 };
        var metered = new MeteredModel(fake, spending, Aud(5m));

        await metered.AskAsync(Request(), TestContext.Current.CancellationToken);

        // 100 in + 500 out at 4/20 USD per million = 0.0104 USD = 0.0156 AUD
        Assert.Equal(Money.Of(0.0104m, "USD"), metered.LastCost);
        Assert.Equal(0.0156m, decimal.Round(spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total!.Value, 10));
    }

    [Fact]
    public async Task Calls_stop_at_the_limit_and_failures_are_not_counted()
    {
        using var spending = new AiSpending(_dir);

        // Each call reserves up to 1,000 output tokens (0.02 USD = 0.03 AUD) plus its input
        var metered = new MeteredModel(new Fake { Output = 1000 }, spending, Aud(0.05m));
        await metered.AskAsync(Request(), TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<AiException>(() => metered.AskAsync(Request(), TestContext.Current.CancellationToken));
        Assert.Contains("monthly limit", ex.Message, StringComparison.Ordinal);

        using var other = new AiSpending(Path.Combine(_dir, "sub"));
        var failing = new MeteredModel(new Fake { Fail = true }, spending, Aud(5m));
        await Assert.ThrowsAsync<AiException>(() => failing.AskAsync(Request(), TestContext.Current.CancellationToken));
        Assert.True(spending.Ledger.ThisMonth(Aud(5m), spending.Rates.Current).Total < 0.05m);
    }

    [Fact]
    public async Task An_unpriced_model_is_never_called()
    {
        using var spending = new AiSpending(_dir);
        var fake = new Fake { ModelName = "claude-unknown" };

        await Assert.ThrowsAsync<AiException>(() => new MeteredModel(fake, spending, Aud(5m)).AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public void Provider_limits_come_from_the_settings()
    {
        var config = new PluginConfiguration { Currency = "aud", OverallMonthlyBudget = 10m };
        config.Providers.Add(new ProviderSettings { Id = "anthropic", Enabled = true, BudgetMode = ProviderBudgetMode.PercentOfOverall, BudgetValue = 50 });

        var limits = AiSpending.LimitsOf(config);

        Assert.Equal("AUD", limits.Currency);
        Assert.Equal(10m, limits.Overall);
        Assert.Equal(5m, limits.PerProvider["anthropic"]);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unsupported_currency_setting_is_read_as_usd(string? currency)
    {
        // FAM-06: "the setting, or USD" only lets a supported code through (before, any three letters passed)
        var limits = AiSpending.LimitsOf(new PluginConfiguration { Currency = currency! });

        Assert.Equal("USD", limits.Currency);
    }

    [Fact]
    public void The_settings_page_is_offered_the_shared_currency_list()
    {
        Assert.Equal(CurrencyCode.Supported, AiSpending.Currencies);
        Assert.Contains("AUD", AiSpending.Currencies);
    }

    [Fact]
    public async Task A_prepaid_credit_counts_down_from_its_date()
    {
        using var spending = new AiSpending(_dir);
        await new MeteredModel(new Fake { Output = 500 }, spending, Aud(5m)).AskAsync(Request(), TestContext.Current.CancellationToken);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var provider = new ProviderSettings { Id = "anthropic", Enabled = true, PrepaidCredit = 10m, PrepaidCreditDate = today };

        var left = PrepaidCredit.Of(provider, spending.Ledger, null)!;

        Assert.Equal("USD", left.Currency);
        Assert.Equal(0.0104m, left.Spent);
        Assert.Equal(9.9896m, left.Remaining);
        Assert.Null(PrepaidCredit.Of(new ProviderSettings { Id = "anthropic" }, spending.Ledger, null));
        var later = PrepaidCredit.Of(new ProviderSettings { Id = "anthropic", PrepaidCredit = 5m, PrepaidCreditDate = DateTime.Now.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }, spending.Ledger, null)!;
        Assert.Equal(5m, later.Remaining);
    }

    [Fact]
    public void Credit_settings_are_tidied()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderSettings { Id = "anthropic", PrepaidCredit = -3m, PrepaidCreditCurrency = "xyz", PrepaidCreditDate = "yesterday" });

        Jellyfin.Plugin.Ai.Budgets.BudgetRules.Normalise(config);

        var p = config.Providers[0];
        Assert.Equal(0m, p.PrepaidCredit);
        Assert.Equal("USD", p.PrepaidCreditCurrency);
        Assert.Equal(string.Empty, p.PrepaidCreditDate);
    }

    private sealed class Fake : IAiModel
    {
        public string Provider => KnownProviders.Anthropic;

        public string ModelName { get; init; } = ClaudeModel.DefaultModel;

        public string Model => ModelName;

        public int Output { get; init; } = 100;

        public bool Fail { get; init; }

        public int Calls { get; private set; }

        public Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail
                ? throw new AiException("down")
                : Task.FromResult(new AiAnswer(JsonSerializer.SerializeToElement(new { ok = true }), Provider, Model, 100, Output));
        }
    }
}
