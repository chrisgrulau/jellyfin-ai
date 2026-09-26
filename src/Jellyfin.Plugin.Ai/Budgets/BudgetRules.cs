using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Ai.Configuration;

namespace Jellyfin.Plugin.Ai.Budgets;

/// <summary>
/// How serious a budget message is.
/// </summary>
public enum BudgetSeverity
{
    /// <summary>Allowed, but worth knowing.</summary>
    Warning = 0,

    /// <summary>Not allowed; the setting is ignored until fixed.</summary>
    Error,
}

/// <summary>
/// A message about the spending limits, shown on the settings page.
/// </summary>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">What it says.</param>
public sealed record BudgetMessage(BudgetSeverity Severity, string Message);

/// <summary>
/// The spending-limit rules. There is an overall monthly limit for all paid AI providers together (0 = no paid usage; no
/// limit only by explicit choice), and each provider may also have its own limit, as an amount or as a percentage of the
/// overall limit, mixed freely across providers. A provider may spend up to the lower of its own limit and what is left
/// of the overall one.
/// </summary>
public static class BudgetRules
{
    /// <summary>
    /// Brings settings within safe bounds and fills in anything missing: every known provider appears once, unknown
    /// ones are dropped, amounts are not negative and percentages are 0 to 100.
    /// </summary>
    /// <param name="config">The settings (changed in place).</param>
    public static void Normalise(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Currency = Common.Costs.CurrencyCode.IsSupported(config.Currency) ? Common.Costs.CurrencyCode.Normalise(config.Currency)! : "USD";
        config.OverallMonthlyBudget = Math.Max(0, config.OverallMonthlyBudget);
        config.ExtraChargesPercent = Math.Clamp(config.ExtraChargesPercent, 0m, Common.Costs.CostConverter.MaxExtraPercent);

        var byId = new Dictionary<string, ProviderSettings>(StringComparer.Ordinal);
        foreach (var p in config.Providers.Where(p => p is not null && KnownProviders.IsKnown(p.Id)))
        {
            byId.TryAdd(p.Id, p);
        }

        config.Providers.Clear();
        foreach (var id in KnownProviders.All)
        {
            var p = byId.TryGetValue(id, out var existing) ? existing : KnownProviders.Default(id);
            p.Model = (p.Model ?? string.Empty).Trim();
            p.BaseUrl = (p.BaseUrl ?? string.Empty).Trim();
            p.BudgetValue = p.BudgetMode == ProviderBudgetMode.PercentOfOverall ? Math.Clamp(p.BudgetValue, 0m, 100m) : Math.Max(0, p.BudgetValue);
            config.Providers.Add(p);
        }
    }

    /// <summary>
    /// A provider's own monthly limit in the chosen currency.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="overall">The overall monthly limit, or <c>null</c> for none.</param>
    /// <returns>The limit, or <c>null</c> when it has none of its own (or a percentage with no overall limit).</returns>
    public static decimal? ProviderLimit(ProviderSettings provider, decimal? overall)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.BudgetMode switch
        {
            ProviderBudgetMode.Amount => Math.Max(0, provider.BudgetValue),
            ProviderBudgetMode.PercentOfOverall when overall is { } o => o * Math.Clamp(provider.BudgetValue, 0m, 100m) / 100m,
            _ => null,
        };
    }

    /// <summary>
    /// Checks the limits and says what's risky or not allowed.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <returns>The messages; empty when all is well.</returns>
    public static IReadOnlyList<BudgetMessage> Check(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var messages = new List<BudgetMessage>();
        decimal? overall = config.NoOverallLimit ? null : Math.Max(0, config.OverallMonthlyBudget);
        var paid = config.Providers.Where(p => p is { Enabled: true } && !IsLocal(p)).ToList();
        string Money(decimal amount) => config.Currency + " " + amount.ToString("0.00", CultureInfo.InvariantCulture);

        foreach (var p in paid.Where(p => p.BudgetMode == ProviderBudgetMode.PercentOfOverall && overall is null))
        {
            messages.Add(new BudgetMessage(BudgetSeverity.Error, $"{Name(p)}: a percentage needs an overall limit. Set one, or give {Name(p)} an amount."));
        }

        var unlimited = paid.Where(p => overall is null && ProviderLimit(p, overall) is null).ToList();
        if (unlimited.Count > 0)
        {
            messages.Add(new BudgetMessage(BudgetSeverity.Warning, $"No spending limit for {string.Join(", ", unlimited.Select(Name))}: only limits set with the provider apply."));
        }

        if (overall is { } o && o > 0)
        {
            var own = paid.Select(p => ProviderLimit(p, overall)).ToList();
            if (paid.Count > 1 && own.All(l => l is null))
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Warning, "Only the overall limit is set: one provider running over could use it all and stop the others. Consider a limit for each provider."));
            }

            var sum = own.OfType<decimal>().Sum();
            if (sum > o)
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Warning, $"The providers' own limits add up to {Money(sum)}, more than the overall {Money(o)}: the overall limit will stop spending first."));
            }
        }

        return messages;
    }

    // Local servers (an OpenAI-compatible address on this machine or network) cost nothing. A local relay to a paid service
    // (LiteLLM, an OpenRouter proxy) is also treated as free; the settings page says so
    private static bool IsLocal(ProviderSettings p)
        => string.Equals(p.Id, KnownProviders.OpenAiCompatible, StringComparison.Ordinal) && Common.NetworkAddress.IsLocal(p.BaseUrl);

    private static string Name(ProviderSettings p) => p.Id switch
    {
        KnownProviders.Anthropic => "Anthropic",
        KnownProviders.OpenAi => "OpenAI",
        KnownProviders.Google => "Google",
        _ => "OpenAI-compatible service",
    };
}
