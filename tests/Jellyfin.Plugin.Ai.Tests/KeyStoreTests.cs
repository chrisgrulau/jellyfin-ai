using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

public sealed class KeyStoreTests : IDisposable
{
    private const string Key = "sk-test-0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-keys-" + Guid.NewGuid().ToString("N"));

    private string KeyPath => Path.Combine(_dir, "keys.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Keys_can_be_set_replaced_and_cleared_and_status_never_reveals_them()
    {
        var store = new ApiKeyStore(KeyPath);
        Assert.All(store.Status().Values, Assert.False);

        store.Set(KnownProviders.Anthropic, Key);
        store.Set(KnownProviders.Anthropic, Key + "x");

        Assert.True(store.Status()[KnownProviders.Anthropic]);
        Assert.False(store.Status()[KnownProviders.OpenAi]);
        Assert.Equal(Key + "x", new ApiKeyStore(KeyPath).Get(KnownProviders.Anthropic));

        store.Clear(KnownProviders.Anthropic);
        Assert.False(store.Status()[KnownProviders.Anthropic]);
    }

    [Fact]
    public void The_key_file_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        new ApiKeyStore(KeyPath).Set(KnownProviders.OpenAi, Key);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has a space in it")]
    [InlineData("line\nbreak-0123456789")]
    public void Malformed_keys_are_refused(string key)
        => Assert.Throws<ArgumentException>(() => new ApiKeyStore(KeyPath).Set(KnownProviders.Anthropic, key));

    [Fact]
    public void Unknown_providers_are_refused()
        => Assert.Throws<ArgumentException>(() => new ApiKeyStore(KeyPath).Set("../../etc", Key));

    [Fact]
    public void A_damaged_key_file_means_no_keys()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(KeyPath, "{ nope");

        Assert.All(new ApiKeyStore(KeyPath).Status().Values, Assert.False);
    }

    [Fact]
    public void The_settings_page_lists_exactly_the_known_providers_and_currencies()
    {
        using var stream = typeof(ApiKeyStore).Assembly.GetManifestResourceStream("Jellyfin.Plugin.Ai.Configuration.configPage.html")!;
        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();

        var providers = Regex.Matches(Regex.Match(page, @"var providers = \[(?<l>.*?)\];", RegexOptions.Singleline).Groups["l"].Value, @"\['([a-z-]+)'").Select(m => m.Groups[1].Value);
        var currencies = Regex.Matches(Regex.Match(page, @"var currencies = \[(?<l>[^\]]*)\]").Groups["l"].Value, "'([A-Z]{3})'").Select(m => m.Groups[1].Value);

        Assert.Equal(KnownProviders.All, providers);
        Assert.Equal(Common.Costs.CurrencyCode.Supported, currencies);
    }
}
