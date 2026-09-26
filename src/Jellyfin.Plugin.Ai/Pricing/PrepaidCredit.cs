using System;
using System.Globalization;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Common.Costs;

namespace Jellyfin.Plugin.Ai.Pricing;

/// <summary>
/// What is left of a prepaid credit.
/// </summary>
/// <param name="Provider">The provider.</param>
/// <param name="Currency">The credit's currency.</param>
/// <param name="Credit">The credit bought.</param>
/// <param name="Since">The date it was bought.</param>
/// <param name="Spent">What this plugin has spent with the provider since, or <c>null</c> if it can't be added up.</param>
/// <param name="Remaining">What's left, or <c>null</c> if it can't be worked out.</param>
public sealed record CreditLeft(string Provider, string Currency, decimal Credit, string Since, decimal? Spent, decimal? Remaining);

/// <summary>
/// Counts down a prepaid credit (Anthropic has no balance API): the credit entered on the settings page, less what this
/// plugin has spent with that provider since the date it was bought. Spending by anything else using the same key isn't
/// seen, so it is an estimate from this server's point of view.
/// </summary>
public static class PrepaidCredit
{
    /// <summary>
    /// What's left of a provider's credit.
    /// </summary>
    /// <param name="provider">The provider's settings.</param>
    /// <param name="ledger">The spend ledger.</param>
    /// <param name="rates">The latest exchange rates, if any.</param>
    /// <returns>The credit left, or <c>null</c> if no credit is tracked.</returns>
    internal static CreditLeft? Of(ProviderSettings provider, SpendLedger ledger, ExchangeRates? rates)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(ledger);
        if (provider.PrepaidCredit <= 0
            || CurrencyCode.Normalise(provider.PrepaidCreditCurrency) is not { } currency
            || !DateOnly.TryParseExact(provider.PrepaidCreditDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        // From the start of that day, in the server's time
        var since = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(date.ToDateTime(TimeOnly.MinValue)));
        var spent = ledger.SpentSince(provider.Id, since, currency, rates)?.Amount;
        return new CreditLeft(provider.Id, currency, provider.PrepaidCredit, provider.PrepaidCreditDate, spent is { } s ? decimal.Round(s, 4) : null, spent is { } t ? decimal.Round(provider.PrepaidCredit - t, 4) : null);
    }
}
