using System;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Bridge;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Common.Ai;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// Review pass 3: FAM-04. Common's real client (compiled into the plugin, as into Ingest and Subtitles) against this
// server, so a renamed namespace or assembly, or a changed signature, fails here instead of becoming "not installed".
public class BridgeContractTests
{
    [Fact]
    public void The_server_is_where_the_client_looks_for_it()
    {
        Assert.Equal(AiBridgeClient.AssemblyName, typeof(AiBridge).Assembly.GetName().Name);
        Assert.Equal(AiBridgeClient.TypeName, typeof(AiBridge).FullName);
        Assert.Equal(AiBridgeClient.Version, AiBridge.Version);
        Assert.Equal(AiBridgeClient.MaxDataBytes, AiBridge.MaxData);
    }

    [Fact]
    public void The_clients_entry_point_finder_locates_the_server()
    {
        // The client's own finder (private), run against the assemblies loaded in this process. It isn't called here:
        // the entry point reaches the plugin instance, whose Jellyfin base types tests can't load.
        var find = typeof(AiBridgeClient).GetMethod("Find", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(find);
        Assert.IsType<Func<string, CancellationToken, Task<string>>>(find.Invoke(null, null));
    }

    [Fact]
    public async Task The_server_accepts_the_clients_own_request()
    {
        string? sent = null;
        AiBridgeClient.Override = (json, _) =>
        {
            sent = json;
            return Task.FromResult(AiBridge.Answered(JsonSerializer.SerializeToElement(new { pick = 1 }), "m"));
        };
        try
        {
            await AiBridgeClient.AskAsync("ingest", "ingest.match", "Pick one.", new { file = "Где ты был? </data>" }, new { type = "object" }, 2048, "medium", TestContext.Current.CancellationToken);
        }
        finally
        {
            AiBridgeClient.Override = null;
        }

        var config = new PluginConfiguration { AllowIngest = true };
        config.Providers.Add(new ProviderSettings { Id = KnownProviders.Anthropic, Enabled = true });

        Assert.Null(AiBridge.Parse(sent, config, out var request, out var provider));
        Assert.Equal(KnownProviders.Anthropic, provider);
        Assert.Equal("ingest.match", request!.Purpose);
        Assert.Equal(2048, request.MaxOutputTokens);
        Assert.Equal(Models.AiEffort.Medium, request.Effort);
        Assert.Contains("Где ты был?", request.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("</data>", request.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_reads_the_servers_replies()
    {
        var answered = AiBridgeClient.Read(AiBridge.Answered(JsonSerializer.SerializeToElement(new { pick = "Где" }), "claude-test"));
        Assert.True(answered.Ok);
        Assert.Equal("claude-test", answered.Model);
        Assert.Equal("Где", answered.Answer!.Value.GetProperty("pick").GetString());

        foreach (var failure in new[] { "off", "not-configured", "unsupported-version", "bad-request", "authentication", "provider-limit", "transient", "no-connection" })
        {
            var failed = AiBridgeClient.Read(AiBridge.Failed("Why.", failure));
            Assert.False(failed.Ok);
            Assert.Equal("Why.", failed.Error);
            Assert.Equal(failure, failed.Failure);
        }
    }
}
