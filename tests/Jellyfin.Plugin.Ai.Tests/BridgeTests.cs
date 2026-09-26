using System;
using System.Text.Json;
using Jellyfin.Plugin.Ai.Bridge;
using Jellyfin.Plugin.Ai.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

public class BridgeTests
{
    private static PluginConfiguration Config(bool allowIngest = true, bool anthropicOn = true)
    {
        var c = new PluginConfiguration { AllowIngest = allowIngest };
        c.Providers.Add(new ProviderSettings { Id = KnownProviders.Anthropic, Enabled = anthropicOn });
        return c;
    }

    private static string Request(int version = 1, string caller = "ingest", string purpose = "ingest.match", string? data = null, string schema = "{\"type\":\"object\"}")
        => "{\"version\":" + version + ",\"caller\":\"" + caller + "\",\"purpose\":\"" + purpose + "\",\"instructions\":\"Pick one.\",\"data\":"
            + (data ?? "{\"file\":\"x\"}") + ",\"schema\":" + schema + ",\"maxOutputTokens\":999999,\"effort\":\"medium\"}";

    [Fact]
    public void A_valid_request_is_read_with_its_limits_applied()
    {
        Assert.Null(AiBridge.Parse(Request(), Config(), out var request, out var provider));

        Assert.Equal(KnownProviders.Anthropic, provider);
        Assert.Equal("ingest.match", request!.Purpose);
        Assert.Equal("{\"file\":\"x\"}", request.Data);
        Assert.Equal(AiBridge.MaxOutputTokens, request.MaxOutputTokens);
        Assert.Equal(Jellyfin.Plugin.Ai.Models.AiEffort.Medium, request.Effort);
        Assert.Equal("object", request.Schema["type"].GetString());
    }

    [Theory]
    [InlineData(2, "ingest", "ingest.match", "version")]
    [InlineData(1, "subtitles", "subtitles.match", "isn't allowed")]
    [InlineData(1, "ingest", "subtitles.match", "must start with")]
    [InlineData(1, "someone", "someone.x", "isn't allowed")]
    public void Requests_from_the_wrong_version_caller_or_purpose_are_refused(int version, string caller, string purpose, string reason)
        => Assert.Contains(reason, AiBridge.Parse(Request(version, caller, purpose), Config(), out _, out _), StringComparison.Ordinal);

    [Fact]
    public void Switched_off_plugins_providers_and_oversized_data_are_refused()
    {
        Assert.Contains("isn't allowed", AiBridge.Parse(Request(), Config(allowIngest: false), out _, out _), StringComparison.Ordinal);
        Assert.Contains("No AI provider", AiBridge.Parse(Request(), Config(anthropicOn: false), out _, out _), StringComparison.Ordinal);
        Assert.Contains("too large", AiBridge.Parse(Request(data: JsonSerializer.Serialize(new string('x', AiBridge.MaxData + 1))), Config(), out _, out _), StringComparison.Ordinal);
        Assert.Contains("no answer schema", AiBridge.Parse(Request(schema: "\"none\""), Config(), out _, out _), StringComparison.Ordinal);
        Assert.Contains("valid JSON", AiBridge.Parse("{nope", Config(), out _, out _), StringComparison.Ordinal);
        var off = Config();
        off.Enabled = false;
        Assert.Contains("switched off", AiBridge.Parse(Request(), off, out _, out _), StringComparison.Ordinal);
    }
}
