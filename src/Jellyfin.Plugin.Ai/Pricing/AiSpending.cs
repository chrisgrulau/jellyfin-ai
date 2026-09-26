using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Ai.Budgets;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Common.Costs;

namespace Jellyfin.Plugin.Ai.Pricing;

/// <summary>
/// What AI calls cost and how much has been spent this month: the published prices shipped with the plugin, the spend
/// ledger and the latest exchange rates, kept in the plugin's data folder. This plugin owns the ledger for AI providers
/// shared by the plugin family.
/// </summary>
public sealed class AiSpending : IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiSpending"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    public AiSpending(string dataFolder)
        : this(dataFolder, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AiSpending"/> class.
    /// </summary>
    /// <param name="dataFolder">The plugin's data folder.</param>
    /// <param name="prices">Prices (for tests); by default the ones shipped with the plugin.</param>
    /// <param name="clock">Clock.</param>
    internal AiSpending(string dataFolder, PriceTable? prices, TimeProvider? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        Ledger = new SpendLedger(Path.Combine(dataFolder, "spend.json"), clock);
        Rates = new ExchangeRateStore(Path.Combine(dataFolder, "rates.json"), clock);
        Prices = prices ?? Shipped();
    }

    /// <summary>Gets the spend ledger.</summary>
    internal SpendLedger Ledger { get; }

    /// <summary>Gets the exchange rates.</summary>
    internal ExchangeRateStore Rates { get; }

    /// <summary>Gets the prices, or <c>null</c> if the shipped table couldn't be read (paid calls then wait).</summary>
    internal PriceTable? Prices { get; }

    /// <summary>
    /// The spending limits from the settings: the overall monthly limit and each provider's own.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <returns>The limits.</returns>
    internal static SpendLimits LimitsOf(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        decimal? overall = config.NoOverallLimit ? null : Math.Max(0, config.OverallMonthlyBudget);
        var per = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in config.Providers.Where(p => p is { Enabled: true }))
        {
            if (BudgetRules.ProviderLimit(p, overall) is { } limit)
            {
                per[p.Id] = limit;
            }
        }

        return new SpendLimits(CurrencyCode.Normalise(config.Currency) ?? "USD", overall, per, Math.Clamp(config.ExtraChargesPercent, 0m, CostConverter.MaxExtraPercent));
    }

    /// <summary>
    /// The most a call can cost: its input (estimated generously from its length) plus its whole output allowance.
    /// </summary>
    /// <param name="provider">Provider.</param>
    /// <param name="model">Model.</param>
    /// <param name="inputChars">Characters sent.</param>
    /// <param name="maxOutputTokens">Output allowance.</param>
    /// <returns>The estimate, or <c>null</c> if the price is unknown.</returns>
    internal Money? Estimate(string provider, string model, int inputChars, int maxOutputTokens)
        => Cost(provider, model, (inputChars / 3) + 200, maxOutputTokens);

    /// <summary>
    /// What a call cost from its token counts.
    /// </summary>
    /// <param name="provider">Provider.</param>
    /// <param name="model">Model.</param>
    /// <param name="inputTokens">Input tokens.</param>
    /// <param name="outputTokens">Output tokens.</param>
    /// <returns>The cost, or <c>null</c> if the price is unknown.</returns>
    internal Money? Cost(string provider, string model, long inputTokens, long outputTokens)
    {
        if (Prices?.PriceOf(provider, model, PriceTable.InputMillionTokens) is not { } input
            || Prices.PriceOf(provider, model, PriceTable.OutputMillionTokens) is not { } output
            || input.Currency != output.Currency)
        {
            return null;
        }

        return new Money(((input.Amount * inputTokens) + (output.Amount * outputTokens)) / 1_000_000m, input.Currency);
    }

    /// <inheritdoc />
    public void Dispose() => Rates.Dispose();

    private static PriceTable? Shipped()
    {
        using var stream = typeof(AiSpending).Assembly.GetManifestResourceStream(typeof(AiSpending).Namespace + ".prices.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return PriceTable.Parse(reader.ReadToEnd());
    }
}
