using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// What building a model needs besides the settings.
/// </summary>
/// <param name="Keys">The key store.</param>
/// <param name="Spending">Prices, ledger and rates.</param>
/// <param name="Resolver">Resolves "the current model" of a family, or <c>null</c> to use each family's built-in fallback.</param>
/// <param name="HttpFactory">Makes the HTTP clients Gemini calls go through (the model disposes them), or <c>null</c>.</param>
/// <param name="TestHttp">One HTTP client for every provider SDK and call (tests; never disposed by the model).</param>
internal sealed record ModelServices(ApiKeyStore Keys, AiSpending Spending, ModelResolver? Resolver = null, Func<HttpClient>? HttpFactory = null, HttpClient? TestHttp = null);

/// <summary>
/// Builds the model a provider setting names, metered against the spending limits (unless it costs nothing), or says in
/// plain language why it can't be used.
/// </summary>
public static class AiModels
{
    /// <summary>
    /// Builds the model for a provider.
    /// </summary>
    /// <param name="config">Plugin settings.</param>
    /// <param name="provider">Provider id.</param>
    /// <param name="model">Model override (empty for the setting, then the provider's current model).</param>
    /// <param name="services">Keys, spending, the model resolver and HTTP clients.</param>
    /// <param name="unsaved">The provider's settings as shown on the settings page, not yet saved (the Test button), or
    /// <c>null</c> for the saved ones.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model, or why it can't be used.</returns>
    internal static async Task<(IAiModel? Model, string? Problem)> CreateAsync(PluginConfiguration config, string provider, string? model, ModelServices services, ProviderSettings? unsaved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(services);
        var settings = (unsaved is not null && string.Equals(unsaved.Id, provider, StringComparison.Ordinal) ? unsaved : null)
            ?? config.Providers.FirstOrDefault(p => p is not null && string.Equals(p.Id, provider, StringComparison.Ordinal))
            ?? KnownProviders.Default(provider ?? string.Empty);
        var name = !string.IsNullOrWhiteSpace(model) ? model.Trim() : settings.Model?.Trim();
        var key = KnownProviders.IsKnown(provider) ? services.Keys.Get(provider!) : null;
        var paidOff = !config.NoOverallLimit && config.OverallMonthlyBudget <= 0;
        var who = KnownProviders.NameOf(provider);
        IAiModel Metered(IAiModel inner, ModelPrice? custom = null) => new MeteredModel(inner, services.Spending, AiSpending.LimitsOf(config), custom);

        switch (provider)
        {
            case KnownProviders.Anthropic:
                if (key is null)
                {
                    return (null, "Add an Anthropic API key first.");
                }

                if (paidOff)
                {
                    return (null, "The overall monthly limit is 0, so paid providers aren't used. Raise it to use Claude.");
                }

                return (Metered(services.TestHttp is null ? new ClaudeModel(key, name) : new ClaudeModel(key, name, services.TestHttp)), null);

            case KnownProviders.OpenAi:
                if (key is null)
                {
                    return (null, "Add an OpenAI API key first.");
                }

                if (paidOff)
                {
                    return (null, "The overall monthly limit is 0, so paid providers aren't used. Raise it to use OpenAI.");
                }

                name = string.IsNullOrWhiteSpace(name)
                    ? await CurrentAsync(settings, services, ct => OpenAiChatModel.ListAsync(key, null, services.TestHttp, ct), cancellationToken).ConfigureAwait(false)
                    : name;
                return (Metered(new OpenAiChatModel(KnownProviders.OpenAi, key, name, null, services.TestHttp)), null);

            case KnownProviders.Google:
                if (key is null)
                {
                    return (null, "Add a Gemini API key first (from Google AI Studio).");
                }

                if (paidOff)
                {
                    return (null, "The overall monthly limit is 0, so paid providers aren't used. Raise it to use Gemini.");
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    var (listHttp, ownsList) = Http(services);
                    try
                    {
                        name = await CurrentAsync(settings, services, ct => GeminiModel.ListAsync(key, listHttp, ct), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (ownsList)
                        {
                            listHttp.Dispose();
                        }
                    }
                }

                var (http, owns) = Http(services);
                return (Metered(new GeminiModel(key, name, http, owns)), null);

            case KnownProviders.OpenAiCompatible:
                if (ProviderRules.AddressProblem(settings.BaseUrl, out var endpoint) is { } address)
                {
                    return (null, who + ": " + address);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    return (null, who + ": name the model to use (the service has no automatic choice).");
                }

                var chat = new OpenAiChatModel(KnownProviders.OpenAiCompatible, key, name, endpoint, services.TestHttp);
                if (ProviderRules.IsUnmetered(settings))
                {
                    return (new FreeModel(chat), null);
                }

                if (ProviderRules.CustomPrice(settings) is not { } price)
                {
                    return (null, who + ": its prices aren't known, so it isn't used. Enter them under Advanced, or tick \"This service is free\".");
                }

                if (paidOff)
                {
                    return (null, "The overall monthly limit is 0, so paid providers aren't used. Raise it to use the OpenAI-compatible service.");
                }

                return (Metered(chat, price), null);

            default:
                return (null, "Unknown provider.");
        }
    }

    /// <summary>
    /// Whether a provider's model has a published price.
    /// </summary>
    /// <param name="spending">The prices.</param>
    /// <param name="provider">The provider id.</param>
    /// <param name="model">The model id.</param>
    /// <returns><c>true</c> if both its input and output prices are known.</returns>
    internal static bool Priced(AiSpending spending, string provider, string model)
    {
        ArgumentNullException.ThrowIfNull(spending);
        return spending.Prices?.PriceOf(provider, model, PriceTable.InputMillionTokens) is not null
            && spending.Prices.PriceOf(provider, model, PriceTable.OutputMillionTokens) is not null;
    }

    // The current model of the provider's chosen family: the resolver's, or the family's built-in fallback
    private static async Task<string> CurrentAsync(ProviderSettings settings, ModelServices services, Func<CancellationToken, Task<System.Collections.Generic.IReadOnlyList<string>>> list, CancellationToken cancellationToken)
    {
        var family = ModelCatalog.Family(settings.Id, settings.Family)!;
        if (services.Resolver is null)
        {
            return family.Fallback;
        }

        var resolved = await services.Resolver.ResolveAsync(family, list, id => Priced(services.Spending, settings.Id, id), cancellationToken).ConfigureAwait(false);
        return resolved.Model;
    }

    private static (HttpClient Http, bool Owns) Http(ModelServices services)
    {
        if (services.TestHttp is { } test)
        {
            return (test, false);
        }

        // Owned by the model that uses it, which disposes it
#pragma warning disable CA2000
        var http = services.HttpFactory?.Invoke() ?? new HttpClient();
#pragma warning restore CA2000
        http.Timeout = TimeSpan.FromMinutes(5);
        return (http, true);
    }
}
