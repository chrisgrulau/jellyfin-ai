using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Ai.Configuration;

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML in the plugin configurations folder. API keys are not part of them.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>The default overall monthly limit, in the chosen currency.</summary>
    public const decimal DefaultOverallMonthly = 5m;

    // ---- Basic ----

    /// <summary>Gets or sets a value indicating whether the plugin answers requests at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether Ingest may ask for help identifying releases (it sends release names,
    /// parsed titles, years and candidate titles; never paths). Off until allowed.
    /// </summary>
    public bool AllowIngest { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Subtitles may ask for help judging matches (it sends subtitle and
    /// transcript excerpts with titles; never paths). Off until allowed.
    /// </summary>
    public bool AllowSubtitles { get; set; }

    /// <summary>Gets or sets the providers, in order of preference.</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Jellyfin deserializes plugin configuration from JSON, which cannot populate a get-only collection.")]
    public Collection<ProviderSettings> Providers { get; set; } = [];

    /// <summary>Gets or sets the currency limits and costs are set and shown in (ISO 4217).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Gets or sets the overall monthly limit for all paid AI providers together, in <see cref="Currency"/>. 0 means no
    /// paid usage at all.
    /// </summary>
    public decimal OverallMonthlyBudget { get; set; } = DefaultOverallMonthly;

    /// <summary>
    /// Gets or sets a value indicating whether there is no overall limit (an explicit choice; the settings page warns).
    /// Each provider's own limit, and limits set with the providers, still apply.
    /// </summary>
    public bool NoOverallLimit { get; set; }

    // ---- Advanced ----

    /// <summary>Gets or sets a percentage added to provider charges for taxes (such as GST) or card fees; 0 to 100.</summary>
    public decimal ExtraChargesPercent { get; set; }
}
