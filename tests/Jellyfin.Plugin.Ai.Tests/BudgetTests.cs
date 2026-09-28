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

    private static ProviderSettings P(string id, ProviderBudgetMode mode = ProviderBudgetMode.OverallOnly, decimal value = 0, string url = "", string model = "")
        => new() { Id = id, Enabled = true, BudgetMode = mode, BudgetValue = value, BaseUrl = url, Model = model };

    [Fact]
    public void Defaults_are_a_small_overall_cap_and_only_anthropic_allowed()
    {
        var c = Config(overall: PluginConfiguration.DefaultOverallMonthly);

        Assert.Equal(KnownProviders.All.Concat(KnownProviders.Speech), c.Providers.Select(p => p.Id));
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
        Assert.Equal(KnownProviders.All.Concat(KnownProviders.Speech), c.Providers.Select(p => p.Id));
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
        var c = Config(10m, true, P(KnownProviders.OpenAiCompatible, url: url, model: "llama3.3"));

        Assert.DoesNotContain(EveryProvider(c), m => m.Message.Contains("OpenAI-compatible", System.StringComparison.Ordinal));
        Assert.True(ProviderRules.IsUnmetered(c.Providers.Single(p => p.Id == KnownProviders.OpenAiCompatible)));
    }

    [Fact]
    public void A_remote_openai_compatible_service_is_paid()
    {
        var c = Config(10m, true, P(KnownProviders.OpenAiCompatible, url: "https://openrouter.ai/api/v1"));

        Assert.Contains(EveryProvider(c), m => m.Message.Contains("OpenAI-compatible", System.StringComparison.Ordinal));
    }

    // Since 0.6 every AI provider can be used, so every one is checked
    [Fact]
    public void Every_ai_provider_is_available_and_checked()
    {
        var c = Config(10m, true, P(KnownProviders.Anthropic, ProviderBudgetMode.Amount, 3m), P(KnownProviders.OpenAi, ProviderBudgetMode.PercentOfOverall, 50m));

        Assert.All(KnownProviders.All, id => Assert.True(KnownProviders.IsAvailable(id)));
        Assert.Contains(BudgetRules.Check(c), m => m.Severity == BudgetSeverity.Error && m.Message.StartsWith("OpenAI:", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://openrouter.ai/api/v1", "https://")]
    [InlineData("ftp://localhost/v1", "must start with")]
    [InlineData("https://user:pw@example.com/v1", "user name")]
    [InlineData("", "Enter the service's address")]
    public void A_compatible_service_needs_a_usable_address(string url, string reason)
    {
        var c = Config(10m, false, P(KnownProviders.OpenAiCompatible, url: url, model: "m"));

        Assert.Contains(BudgetRules.Check(c), m => m.Severity == BudgetSeverity.Error && m.Message.Contains(reason, System.StringComparison.Ordinal));
    }

    [Fact]
    public void A_remote_compatible_service_needs_prices_or_to_be_marked_free()
    {
        var unpriced = Config(10m, false, P(KnownProviders.OpenAiCompatible, url: "https://api.groq.com/openai/v1", model: "m"));
        Assert.Contains(BudgetRules.Check(unpriced), m => m.Message.Contains("prices aren't known", System.StringComparison.Ordinal));

        var priced = P(KnownProviders.OpenAiCompatible, url: "https://api.groq.com/openai/v1", model: "m");
        priced.InputPrice = 0.5m;
        priced.OutputPrice = 1m;
        priced.PriceCurrency = "usd";
        var c = Config(10m, false, priced);
        Assert.DoesNotContain(BudgetRules.Check(c), m => m.Message.Contains("prices", System.StringComparison.Ordinal));
        Assert.Equal("USD", c.Providers.Single(p => p.Id == KnownProviders.OpenAiCompatible).PriceCurrency);
        Assert.False(ProviderRules.IsUnmetered(priced));

        var free = P(KnownProviders.OpenAiCompatible, url: "https://free.example/v1", model: "m");
        free.Free = true;
        Assert.True(ProviderRules.IsUnmetered(free));
        Assert.DoesNotContain(BudgetRules.Check(Config(10m, true, free)), m => m.Message.Contains("OpenAI-compatible", System.StringComparison.Ordinal));
    }

    [Fact]
    public void Only_a_compatible_service_can_be_marked_free()
    {
        var p = P(KnownProviders.OpenAi);
        p.Free = true;

        var c = Config(10m, false, p);

        Assert.False(c.Providers.Single(x => x.Id == KnownProviders.OpenAi).Free);
    }

    [Fact]
    public void The_default_provider_and_fallbacks_are_tidied()
    {
        var c = new PluginConfiguration { DefaultProvider = "evil" };
        foreach (var f in new[] { KnownProviders.Google, "evil", KnownProviders.Anthropic, KnownProviders.Google, KnownProviders.OpenAi })
        {
            c.FallbackProviders.Add(f);
        }

        BudgetRules.Normalise(c);

        Assert.Equal(KnownProviders.Anthropic, c.DefaultProvider);
        Assert.Equal([KnownProviders.Google, KnownProviders.OpenAi], c.FallbackProviders);
    }

    [Fact]
    public void A_switched_off_default_provider_is_warned_about()
    {
        var c = Config(10m, false, new ProviderSettings { Id = KnownProviders.Anthropic, Enabled = false }, P(KnownProviders.OpenAi, ProviderBudgetMode.Amount, 2m));
        c.DefaultProvider = KnownProviders.Anthropic;

        var m = Assert.Single(BudgetRules.Check(c));
        Assert.Contains("OpenAI answers instead", m.Message, System.StringComparison.Ordinal);
        Assert.Equal([KnownProviders.OpenAi], ProviderRules.Order(c));
    }

    [Fact]
    public void Requests_go_to_the_default_then_the_fallbacks_that_are_switched_on()
    {
        var c = Config(10m, false, P(KnownProviders.Anthropic), P(KnownProviders.OpenAi), new ProviderSettings { Id = KnownProviders.Google, Enabled = false }, P(KnownProviders.OpenAiCompatible, url: "http://localhost:11434/v1", model: "m"));
        c.DefaultProvider = KnownProviders.OpenAiCompatible;
        c.FallbackProviders.Add(KnownProviders.Google);
        c.FallbackProviders.Add(KnownProviders.Anthropic);

        Assert.Equal([KnownProviders.OpenAiCompatible, KnownProviders.Anthropic], ProviderRules.Order(c));
    }
}
