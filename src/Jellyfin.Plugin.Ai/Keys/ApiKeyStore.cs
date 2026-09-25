using System.Collections.Generic;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Common.Secrets;

namespace Jellyfin.Plugin.Ai.Keys;

/// <summary>
/// The AI providers' API keys, kept in an owner-only file separate from the plugin configuration (see the shared
/// <c>KeyFile</c> in jellyfin-plugin-common): the settings page can only ask whether a key is set, replace it or clear it.
/// </summary>
public sealed class ApiKeyStore
{
    private readonly KeyFile _file;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the key file.</param>
    public ApiKeyStore(string path)
    {
        _file = new KeyFile(path, KnownProviders.All);
    }

    /// <summary>
    /// Whether a key looks usable.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> if acceptable.</returns>
    public static bool IsWellFormed(string? key) => KeyFile.IsWellFormed(key);

    /// <summary>
    /// Which providers have a key.
    /// </summary>
    /// <returns>Provider id → whether a key is set.</returns>
    public IReadOnlyDictionary<string, bool> Status() => _file.Status();

    /// <summary>
    /// Stores (or replaces) a provider's key.
    /// </summary>
    /// <param name="provider">A known provider id.</param>
    /// <param name="key">The key.</param>
    public void Set(string provider, string key) => _file.Set(provider, key);

    /// <summary>
    /// Removes a provider's key.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    public void Clear(string provider) => _file.Clear(provider);

    /// <summary>
    /// Gets a provider's key, for making a call. Never pass it to a log, an alert or a response.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    internal string? Get(string provider) => _file.Get(provider);
}
