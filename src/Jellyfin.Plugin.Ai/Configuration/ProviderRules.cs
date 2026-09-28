using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ai.Pricing;

namespace Jellyfin.Plugin.Ai.Configuration;

/// <summary>
/// Rules about the providers that don't depend on a call: which one answers and in what order the others are tried,
/// which ones cost nothing, and whether an OpenAI-compatible service's address may be used.
/// </summary>
public static class ProviderRules
{
    /// <summary>
    /// The providers to try for a request, in order: the default one, then the fallbacks, each once and only if it is
    /// switched on.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <returns>Provider ids; empty when none is switched on.</returns>
    public static IReadOnlyList<string> Order(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var on = config.Providers.Where(p => p is { Enabled: true } && KnownProviders.IsAvailable(p.Id)).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in new[] { config.DefaultProvider }.Concat(config.FallbackProviders ?? []))
        {
            if (id is not null && on.Contains(id) && !order.Contains(id, StringComparer.Ordinal))
            {
                order.Add(id);
            }
        }

        // The default provider switched off, and no fallback on: the first provider that is on answers, so switching
        // the default off doesn't silently stop everything (the settings page warns)
        if (order.Count == 0 && on.Count > 0)
        {
            order.Add(KnownProviders.All.First(on.Contains));
        }

        return order;
    }

    /// <summary>
    /// Whether an OpenAI-compatible service is on this machine or the local network (common's shared check).
    /// </summary>
    /// <param name="p">The provider's settings.</param>
    /// <returns><c>true</c> for a local OpenAI-compatible service.</returns>
    public static bool IsLocal(ProviderSettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return string.Equals(p.Id, KnownProviders.OpenAiCompatible, StringComparison.Ordinal) && Common.NetworkAddress.IsLocal(p.BaseUrl);
    }

    /// <summary>
    /// Whether a provider's calls cost nothing and aren't metered: an OpenAI-compatible service that is local, or that
    /// the settings say is free. A local relay to a paid service (LiteLLM, an OpenRouter proxy) also counts as free; the
    /// settings page says so.
    /// </summary>
    /// <param name="p">The provider's settings.</param>
    /// <returns><c>true</c> if unmetered.</returns>
    public static bool IsUnmetered(ProviderSettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return string.Equals(p.Id, KnownProviders.OpenAiCompatible, StringComparison.Ordinal) && (p.Free || IsLocal(p));
    }

    /// <summary>
    /// The prices entered for an OpenAI-compatible service, if any.
    /// </summary>
    /// <param name="p">The provider's settings.</param>
    /// <returns>The prices, or <c>null</c>.</returns>
    internal static ModelPrice? CustomPrice(ProviderSettings p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return string.Equals(p.Id, KnownProviders.OpenAiCompatible, StringComparison.Ordinal) && (p.InputPrice > 0 || p.OutputPrice > 0)
            ? new ModelPrice(Math.Max(0, p.InputPrice), Math.Max(0, p.OutputPrice), Common.Costs.CurrencyCode.NormaliseOr(p.PriceCurrency, "USD"))
            : null;
    }

    /// <summary>
    /// Checks an OpenAI-compatible service's address. A key may only travel over plain HTTP to this machine or the local
    /// network (common's shared local-address check); anything else must use HTTPS.
    /// </summary>
    /// <param name="address">The address as entered.</param>
    /// <param name="endpoint">The address, when usable.</param>
    /// <returns>Why it can't be used, or <c>null</c>.</returns>
    public static string? AddressProblem(string? address, out Uri? endpoint)
    {
        endpoint = null;
        if (string.IsNullOrWhiteSpace(address))
        {
            return "Enter the service's address (for example http://localhost:11434/v1).";
        }

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "The service's address must start with http:// or https://.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return "The service's address can't include a user name, password or query; put the key in the key field.";
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !Common.NetworkAddress.IsLocal(uri))
        {
            return "Use an https:// address: plain http:// is only allowed for this machine or the local network.";
        }

        endpoint = uri;
        return null;
    }
}
