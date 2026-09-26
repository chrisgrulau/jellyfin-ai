using System.IO;
using Jellyfin.Plugin.Ai.Keys;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Ai;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // API keys live in their own owner-only file in the plugin's data folder, never in the plugin configuration
        // (which the settings page reads back)
        // Spending on AI providers: published prices, the month's ledger and exchange rates
        serviceCollection.AddSingleton(sp => new Pricing.AiSpending(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(AiPlugin).Assembly.GetName().Name!)));

        serviceCollection.AddSingleton(sp => new ApiKeyStore(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(AiPlugin).Assembly.GetName().Name!, "keys.json")));

        // A log of AI calls for the settings page (never what was sent), owner-only, trimmed to 1,000 calls or 30 days
        serviceCollection.AddSingleton(sp => new Calls.CallLog(
            Path.Combine(sp.GetRequiredService<IApplicationPaths>().PluginsPath, typeof(AiPlugin).Assembly.GetName().Name!, Calls.CallLog.FileName)));

        // The entry point the other plugins use (in-process, JSON in and out)
        serviceCollection.AddHostedService<Bridge.AiBridgeHost>();
    }
}
