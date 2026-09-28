using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ai.Configuration;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// A curated family of models: the automatic choice ("current") is the newest model in it that the provider lists and
/// that has a published price.
/// </summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Id">The family id, as saved in the settings.</param>
/// <param name="Label">Its name for people.</param>
/// <param name="Pattern">Matches the family's model ids; the <c>v</c> group is the version (<c>6</c>, <c>3.8</c>) and a
/// <c>preview</c> group marks a preview model.</param>
/// <param name="Fallback">The model used when the provider's list can't be read (updated with plugin releases).</param>
/// <param name="IsDefault">Whether it is the provider's recommended family.</param>
public sealed record ModelFamily(string Provider, string Id, string Label, Regex Pattern, string Fallback, bool IsDefault);

/// <summary>
/// The curated model families per provider, and how the newest model in one is picked. Only exact aliases match (never
/// dated snapshots, fine-tunes or audio, image and realtime variants), so the choice moves only when the provider
/// publishes a new generation of that family.
/// </summary>
public static class ModelCatalog
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>Gets the curated families, the recommended one first for each provider.</summary>
    public static IReadOnlyList<ModelFamily> Families { get; } =
    [
        // OpenAI names its tiers after bodies in the sky, per generation: gpt-6-sol, gpt-6-astra, gpt-6-luna (2026)
        new(KnownProviders.OpenAi, "sol", "Balanced (recommended)", Rx(@"^gpt-(?<v>\d+(?:\.\d+)?)-sol$"), "gpt-6-sol", true),
        new(KnownProviders.OpenAi, "astra", "Most capable (costs more)", Rx(@"^gpt-(?<v>\d+(?:\.\d+)?)-astra$"), "gpt-6-astra", false),
        new(KnownProviders.OpenAi, "luna", "Fastest and cheapest", Rx(@"^gpt-(?<v>\d+(?:\.\d+)?)-luna$"), "gpt-6-luna", false),

        // Gemini: gemini-<version>-flash, -pro, -flash-lite, sometimes only as a preview
        new(KnownProviders.Google, "flash", "Flash: balanced (recommended)", Rx(@"^gemini-(?<v>\d+(?:\.\d+)?)-flash(?<preview>-preview(?:-[0-9-]+)?)?$"), "gemini-3.8-flash", true),
        new(KnownProviders.Google, "pro", "Pro: most capable (costs more)", Rx(@"^gemini-(?<v>\d+(?:\.\d+)?)-pro(?<preview>-preview(?:-[0-9-]+)?)?$"), "gemini-3.1-pro-preview", false),
        new(KnownProviders.Google, "flash-lite", "Flash-Lite: fastest and cheapest", Rx(@"^gemini-(?<v>\d+(?:\.\d+)?)-flash-lite(?<preview>-preview(?:-[0-9-]+)?)?$"), "gemini-3.5-flash-lite", false),
    ];

    /// <summary>
    /// A provider's families.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <returns>Its families, the recommended one first; empty for a provider without automatic choice.</returns>
    public static IReadOnlyList<ModelFamily> Of(string? provider) => [.. Families.Where(f => f.Provider == provider)];

    /// <summary>
    /// A provider's family by id.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="id">The family id; empty (or unknown) for the recommended one.</param>
    /// <returns>The family, or <c>null</c> if the provider has none.</returns>
    public static ModelFamily? Family(string? provider, string? id)
    {
        var mine = Of(provider);
        return mine.FirstOrDefault(f => string.Equals(f.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? mine.FirstOrDefault(f => f.IsDefault);
    }

    /// <summary>
    /// Picks the newest model of a family from what the provider lists: the highest version among stable models with a
    /// price; a preview only when the family has no priced stable model.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="listed">The model ids the provider lists.</param>
    /// <param name="priced">Whether a model has a published price (an unpriced model is never picked).</param>
    /// <returns>The model id, or <c>null</c> if none fits.</returns>
    public static string? Newest(ModelFamily family, IEnumerable<string> listed, Func<string, bool> priced)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(priced);
        var candidates = listed
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => (Id: id.Trim(), Match: family.Pattern.Match(id.Trim())))
            .Where(c => c.Match.Success && priced(c.Id) && Version.TryParse(Normalise(c.Match.Groups["v"].Value), out _))
            .Select(c => (c.Id, Version: Version.Parse(Normalise(c.Match.Groups["v"].Value)), Preview: c.Match.Groups["preview"].Success))
            .ToList();
        var pool = candidates.Any(c => !c.Preview) ? candidates.Where(c => !c.Preview) : candidates;
        return pool.OrderByDescending(c => c.Version).ThenBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id).FirstOrDefault();
    }

    // "6" → "6.0" so Version can read it
    private static string Normalise(string v) => v.Contains('.', StringComparison.Ordinal) ? v : v + ".0";

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, Timeout);
}
