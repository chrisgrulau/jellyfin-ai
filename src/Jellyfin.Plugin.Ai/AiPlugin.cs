using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Ai.Budgets;
using Jellyfin.Plugin.Ai.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Ai;

/// <summary>
/// The AI plugin: gives the other plugins in the family optional, budget-controlled access to AI models. It does nothing
/// on its own; Ingest and Subtitles use it when it is installed and they are allowed to.
/// </summary>
public class AiPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public AiPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Shoal AI";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5f260705-cb7c-4fee-8324-b40202261739");

    /// <inheritdoc />
    public override string Description => "Gives other plugins optional, budget-controlled access to AI models.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static AiPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        // Keep values the plugin relies on within safe bounds, whatever the settings page (or an API client) sent
        if (configuration is PluginConfiguration c)
        {
            BudgetRules.Normalise(c);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
