using System;
using System.Linq;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Pricing;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// Builds the model a provider setting names, metered against the spending limits, or says in plain language why it
/// can't be used.
/// </summary>
public static class AiModels
{
    /// <summary>
    /// Builds the model for a provider.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="provider">Provider id.</param>
    /// <param name="model">Model override (empty for the setting, then the provider's default).</param>
    /// <param name="keys">The key store.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <returns>The model, or why it can't be used.</returns>
    public static (IAiModel? Model, string? Problem) Create(PluginConfiguration config, string provider, string? model, ApiKeyStore keys, AiSpending spending)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(spending);
        var settings = config.Providers.FirstOrDefault(p => string.Equals(p.Id, provider, StringComparison.Ordinal));
        var name = !string.IsNullOrWhiteSpace(model) ? model.Trim() : settings?.Model;
        switch (provider)
        {
            case KnownProviders.Anthropic:
                if (keys.Get(provider) is not { } key)
                {
                    return (null, "Add an Anthropic API key first.");
                }

                if (!config.NoOverallLimit && config.OverallMonthlyBudget <= 0)
                {
                    return (null, "The overall monthly limit is 0, so paid providers aren't used. Raise it to use Claude.");
                }

                return (new MeteredModel(new ClaudeModel(key, name), spending, AiSpending.LimitsOf(config)), null);
            case KnownProviders.OpenAi or KnownProviders.Google or KnownProviders.OpenAiCompatible:
                return (null, "This provider isn't available in this version yet; Anthropic (Claude) is.");
            default:
                return (null, "Unknown provider.");
        }
    }
}
