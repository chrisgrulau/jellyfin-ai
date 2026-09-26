using System.Linq;
using Jellyfin.Plugin.Ai.Budgets;
using Jellyfin.Plugin.Ai.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

public class BudgetTests
{
    private static PluginConfiguration Config(decimal overall = 10m, bool noOverall = false, params ProviderSettings[] providers)
    {
        var c = new PluginConfiguration { OverallMonthlyBudget = overall, NoOverallLimit = noOverall, Currency = "AUD" };
        foreach (var p in providers)
        {
            c.Providers.Add(p);
        }

        BudgetRules.Normalise(c);
        return c;
    }

    // The rules themselves, as they'll apply once every provider can be used
    private static System.Collections.Generic.IReadOnlyList<BudgetMessage> EveryProvider(PluginConfiguration c) => BudgetRules.Check(c, _ => true);

    private static ProviderSettings P(string id, ProviderBudgetMode mode = ProviderBudgetMode.OverallOnly, decimal value = 0, string url = "")
        => new() { Id = id, Enabled = true, BudgetMode = mode, BudgetValue = value, BaseUrl = url };

    [Fact]
    public void Defaults_are_a_small_overall_cap_and_only_anthropic_allowed()
    {
        var c = Config(overall: PluginConfiguration.DefaultOverallMonthly);

        Assert.Equal(KnownProviders.All, c.Providers.Select(p => p.Id));
        Assert.Equal([KnownProviders.Anthropic], c.Providers.Where(p => p.Enabled).Select(p => p.Id));
        Assert.Empty(BudgetRules.Check(c));
        Assert.False(c.AllowIngest);
        Assert.False(c.AllowSubtitles);
    }

    [Fact]
    public void Normalising_drops_unknown_and_duplicate_providers_and_bad_values()
    {
        var c = new PluginConfiguration { Currency = "zzz", OverallMonthlyBudget = -3, ExtraChargesPercent = 500 };
        c.Providers.Add(new ProviderSettings { Id = "evil" });
        c.Providers.Add(new ProviderSettings { Id = KnownProviders.OpenAi, Enabled = true, BudgetMode = ProviderBudgetMode.PercentOfOverall, BudgetValue = 150 });
        c.Providers.Add(new ProviderSettings { Id = KnownProviders.OpenAi, Enabled = false });

        BudgetRules.Normalise(c);

        Assert.Equal("USD", c.Currency);
        Assert.Equal(0, c.OverallMonthlyBudget);
        Assert.Equal(100, c.ExtraChargesPercent);
        Assert.Equal(KnownProviders.All, c.Providers.Select(p => p.Id));
        var openAi = c.Providers.Single(p => p.Id == KnownProviders.OpenAi);
        Assert.True(openAi.Enabled);
        Assert.Equal(100, openAi.BudgetValue);
    }

    [Fact]
    public void Only_an_overall_limit_with_several_providers_warns_that_one_could_starve_the_others()
    {
        var c = Config(10m, false, P(KnownProviders.Anthropic), P(KnownProviders.OpenAi));

        var m = Assert.Single(EveryProvider(c));
        Assert.Equal(BudgetSeverity.Warning, m.Severity);
        Assert.Contains("one provider", m.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_limits_adding_up_to_more_than_the_overall_limit_warn()
    {
        var c = Config(10m, false, P(KnownProviders.Anthropic, ProviderBudgetMode.Amount, 8m), P(KnownProviders.OpenAi, ProviderBudgetMode.PercentOfOverall, 50m));

        var m = Assert.Single(EveryProvider(c));
        Assert.Contains("AUD 13.00", m.Message, System.StringComparison.Ordinal);
        Assert.Contains("AUD 10.00", m.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Amounts_and_percentages_mix_within_the_overall_limit()
    {
        var c = Config(10m, false, P(KnownProviders.Anthropic, ProviderBudgetMode.Amount, 6m), P(KnownProviders.OpenAi, ProviderBudgetMode.PercentOfOverall, 40m));

        Assert.Empty(EveryProvider(c));
        Assert.Equal(4m, BudgetRules.ProviderLimit(c.Providers.Single(p => p.Id == KnownProviders.OpenAi), 10m));
    }

    [Fact]
    public void A_percentage_without_an_overall_limit_is_an_error()
    {
        var c = Config(10m, true, P(KnownProviders.Anthropic, ProviderBudgetMode.PercentOfOverall, 50m));

        Assert.Contains(EveryProvider(c), m => m.Severity == BudgetSeverity.Error);
    }

    [Fact]
    public void No_limit_anywhere_warns()
    {
        var c = Config(10m, true, P(KnownProviders.Anthropic));

        var m = Assert.Single(EveryProvider(c));
        Assert.Contains("No spending limit for Anthropic", m.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_limit_still_applies_without_an_overall_limit()
    {
        var c = Config(10m, true, P(KnownProviders.Anthropic, ProviderBudgetMode.Amount, 3m));

        Assert.Empty(EveryProvider(c));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://192.168.1.20:8000/v1")]
    [InlineData("http://ai-box.local/v1")]
    public void Local_services_cost_nothing_and_need_no_limit(string url)
    {
        var c = Config(10m, true, P(KnownProviders.OpenAiCompatible, url: url));

        Assert.DoesNotContain(EveryProvider(c), m => m.Message.Contains("OpenAI-compatible", System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_remote_openai_compatible_service_is_paid()
    {
        var c = Config(10m, true, P(KnownProviders.OpenAiCompatible, url: "https://openrouter.ai/api/v1"));

        Assert.Contains(EveryProvider(c), m => m.Message.Contains("OpenAI-compatible", System.StringComparison.Ordinal));
    }

    // Review pass 3: FAM-08. Providers that can't be used yet make no calls and can't be changed on the page, so their
    // saved settings are kept but never warn or block saving
    [Fact]
    public void Providers_not_available_yet_are_kept_but_not_checked()
    {
        var c = Config(10m, true, P(KnownProviders.Anthropic, ProviderBudgetMode.Amount, 3m), P(KnownProviders.OpenAi, ProviderBudgetMode.PercentOfOverall, 50m));

        Assert.Contains(EveryProvider(c), m => m.Severity == BudgetSeverity.Error);
        Assert.Empty(BudgetRules.Check(c));
        var openAi = c.Providers.Single(p => p.Id == KnownProviders.OpenAi);
        Assert.True(openAi.Enabled);
        Assert.Equal(50m, openAi.BudgetValue);
        Assert.True(KnownProviders.IsAvailable(KnownProviders.Anthropic));
        Assert.DoesNotContain(KnownProviders.All, id => id != KnownProviders.Anthropic && KnownProviders.IsAvailable(id));
    }
}
