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

        config.Currency = Common.Costs.CurrencyCode.NormaliseOr(config.Currency, "USD");
        config.DefaultProvider = KnownProviders.IsKnown(config.DefaultProvider?.Trim()) ? config.DefaultProvider!.Trim() : KnownProviders.Anthropic;
        var fallbacks = (config.FallbackProviders ?? []).Select(f => f?.Trim()).Where(f => KnownProviders.IsKnown(f) && f != config.DefaultProvider).Distinct(StringComparer.Ordinal).ToList();
        config.FallbackProviders = new System.Collections.ObjectModel.Collection<string>(fallbacks!);
        config.OverallMonthlyBudget = Math.Max(0, config.OverallMonthlyBudget);
        config.ExtraChargesPercent = Math.Clamp(config.ExtraChargesPercent, 0m, Common.Costs.CostConverter.MaxExtraPercent);

        var byId = new Dictionary<string, ProviderSettings>(StringComparer.Ordinal);
        foreach (var p in config.Providers.Where(p => p is not null && (KnownProviders.IsKnown(p.Id) || KnownProviders.IsSpeech(p.Id))))
        {
            byId.TryAdd(p.Id, p);
        }

        config.Providers.Clear();
        foreach (var id in KnownProviders.All.Concat(KnownProviders.Speech))
        {
            var p = byId.TryGetValue(id, out var existing) ? existing : KnownProviders.Default(id);
            p.Model = (p.Model ?? string.Empty).Trim();
            p.BaseUrl = (p.BaseUrl ?? string.Empty).Trim();
            p.Family = Models.ModelCatalog.Family(p.Id, p.Family) is { } family && !family.IsDefault ? family.Id : string.Empty;
            p.InputPrice = Math.Max(0, p.InputPrice);
            p.OutputPrice = Math.Max(0, p.OutputPrice);
            p.PriceCurrency = Common.Costs.CurrencyCode.NormaliseOr(p.PriceCurrency, "USD");
            p.Free = p.Free && string.Equals(p.Id, KnownProviders.OpenAiCompatible, StringComparison.Ordinal);
            p.PrepaidCredit = Math.Max(0, p.PrepaidCredit);
            p.PrepaidCreditCurrency = Common.Costs.CurrencyCode.NormaliseOr(p.PrepaidCreditCurrency, "USD");
            p.PrepaidCreditDate = DateOnly.TryParseExact((p.PrepaidCreditDate ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : string.Empty;
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
    public static IReadOnlyList<BudgetMessage> Check(PluginConfiguration config) => Check(config, KnownProviders.IsAvailable);

    /// <summary>
    /// Checks the limits of the providers that can be used.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <param name="available">Whether a provider can be used. The others make no calls, and the page can't change their
    /// settings, so they're left out: a saved setting for one can't block saving (FAM-08).</param>
    /// <returns>The messages; empty when all is well.</returns>
    internal static IReadOnlyList<BudgetMessage> Check(PluginConfiguration config, Func<string, bool> available)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(available);

        var messages = new List<BudgetMessage>();
        decimal? overall = config.NoOverallLimit ? null : Math.Max(0, config.OverallMonthlyBudget);
        var on = config.Providers.Where(p => p is { Enabled: true } && available(p.Id)).ToList();
        var paid = on.Where(p => !ProviderRules.IsUnmetered(p)).ToList();
        string Money(decimal amount) => config.Currency + " " + amount.ToString("0.00", CultureInfo.InvariantCulture);

        // Speech-to-text providers are used by Subtitles, which this plugin can't see from here: their own limits count, but
        // they aren't warned about as unlimited or as sharing the overall limit
        var speech = config.Providers.Where(p => p is not null && KnownProviders.IsSpeech(p.Id)).ToList();
        foreach (var p in paid.Concat(speech).Where(p => p.BudgetMode == ProviderBudgetMode.PercentOfOverall && overall is null))
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

            var sum = own.Concat(speech.Select(p => ProviderLimit(p, overall))).OfType<decimal>().Sum();
            if (sum > o)
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Warning, $"The providers' own limits add up to {Money(sum)}, more than the overall {Money(o)}: the overall limit will stop spending first."));
            }
        }

        // An OpenAI-compatible service needs a usable address and a model; a paid one needs its prices to be metered
        foreach (var p in on.Where(p => p.Id == KnownProviders.OpenAiCompatible))
        {
            if (ProviderRules.AddressProblem(p.BaseUrl, out _) is { } address)
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Error, Name(p) + ": " + address));
            }

            if (string.IsNullOrWhiteSpace(p.Model))
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Warning, Name(p) + ": name the model to use (the service has no automatic choice)."));
            }

            if (!ProviderRules.IsUnmetered(p) && ProviderRules.CustomPrice(p) is null)
            {
                messages.Add(new BudgetMessage(BudgetSeverity.Warning, Name(p) + ": its prices aren't known, so it isn't used. Enter them under Advanced, or tick \"This service is free\"."));
            }
        }

        // Which provider answers
        if (on.Count > 0 && !on.Any(p => p.Id == config.DefaultProvider))
        {
            var order = ProviderRules.Order(config);
            var first = order.Count > 0 ? order[0] : null;
            messages.Add(new BudgetMessage(BudgetSeverity.Warning, $"{KnownProviders.NameOf(config.DefaultProvider)} is chosen to answer but is switched off{(first is null ? "." : $"; {KnownProviders.NameOf(first)} answers instead.")}"));
        }

        return messages;
    }

    private static string Name(ProviderSettings p) => KnownProviders.NameOf(p.Id);
}
