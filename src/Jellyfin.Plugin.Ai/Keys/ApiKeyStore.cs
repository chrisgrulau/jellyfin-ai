using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Ai.Configuration;

namespace Jellyfin.Plugin.Ai.Keys;

/// <summary>
/// Keeps provider API keys in a file only the server's own account can read (mode 0600 on Linux and macOS), separate from
/// the plugin configuration so they are never sent back to the settings page, included in a configuration export, or
/// logged. The page can only ask whether a key is set, replace it or clear it.
/// </summary>
public sealed class ApiKeyStore
{
    /// <summary>The longest key accepted.</summary>
    public const int MaxKeyLength = 512;

    private readonly string _path;
    private readonly Lock _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyStore"/> class.
    /// </summary>
    /// <param name="path">Absolute path of the key file.</param>
    public ApiKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>
    /// Whether a key looks usable: 8 to <see cref="MaxKeyLength"/> printable ASCII characters, no spaces.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> if acceptable.</returns>
    public static bool IsWellFormed(string? key)
        => key is { Length: >= 8 and <= MaxKeyLength } && key.All(c => c is > ' ' and < '\u007f');

    /// <summary>
    /// Which providers have a key.
    /// </summary>
    /// <returns>Provider id → whether a key is set, for every known provider.</returns>
    public IReadOnlyDictionary<string, bool> Status()
    {
        lock (_lock)
        {
            var keys = Load();
            return KnownProviders.All.ToDictionary(p => p, keys.ContainsKey, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Stores (or replaces) a provider's key.
    /// </summary>
    /// <param name="provider">A known provider id.</param>
    /// <param name="key">The key.</param>
    /// <exception cref="ArgumentException">Unknown provider or malformed key.</exception>
    public void Set(string provider, string key)
    {
        if (!KnownProviders.IsKnown(provider))
        {
            throw new ArgumentException("Unknown provider.", nameof(provider));
        }

        var trimmed = key?.Trim();
        if (!IsWellFormed(trimmed))
        {
            throw new ArgumentException("That doesn't look like an API key.", nameof(key));
        }

        lock (_lock)
        {
            var keys = Load();
            keys[provider] = trimmed!;
            Save(keys);
        }
    }

    /// <summary>
    /// Removes a provider's key.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    public void Clear(string provider)
    {
        lock (_lock)
        {
            var keys = Load();
            if (keys.Remove(provider))
            {
                Save(keys);
            }
        }
    }

    /// <summary>
    /// Gets a provider's key, for making a call. Never pass it to a log, an alert or a response.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <returns>The key, or <c>null</c>.</returns>
    internal string? Get(string provider)
    {
        lock (_lock)
        {
            return Load().TryGetValue(provider, out var key) ? key : null;
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) is { } keys)
            {
                return new Dictionary<string, string>(keys.Where(k => KnownProviders.IsKnown(k.Key) && IsWellFormed(k.Value)), StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // A damaged key file means the keys have to be entered again; nothing else depends on it
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, string> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";

        // Create the file owner-only before anything is written to it
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, keys);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
