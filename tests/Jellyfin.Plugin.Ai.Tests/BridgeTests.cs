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
        => Assert.Contains(reason, AiBridge.Parse(Request(version, caller, purpose), Config(), out _, out _)!.Message, StringComparison.Ordinal);

    [Fact]
    public void Switched_off_plugins_providers_and_oversized_data_are_refused()
    {
        Assert.Contains("isn't allowed", AiBridge.Parse(Request(), Config(allowIngest: false), out _, out _)!.Message, StringComparison.Ordinal);
        Assert.Contains("No usable AI provider", AiBridge.Parse(Request(), Config(anthropicOn: false), out _, out _)!.Message, StringComparison.Ordinal);
        Assert.Contains("too large", AiBridge.Parse(Request(data: JsonSerializer.Serialize(new string('x', AiBridge.MaxData + 1))), Config(), out _, out _)!.Message, StringComparison.Ordinal);
        Assert.Contains("no answer schema", AiBridge.Parse(Request(schema: "\"none\""), Config(), out _, out _)!.Message, StringComparison.Ordinal);
        Assert.Contains("valid JSON", AiBridge.Parse("{nope", Config(), out _, out _)!.Message, StringComparison.Ordinal);
        var off = Config();
        off.Enabled = false;
        Assert.Contains("switched off", AiBridge.Parse(Request(), off, out _, out _)!.Message, StringComparison.Ordinal);
    }

    // Review pass 3: AI-03 (typed fields, data always JSON), FAM-02 (bytes, unescaped), FAM-03 (failure names)
    [Theory]
    [InlineData("\"one\"", "unsupported-version")]
    [InlineData("1.5", "unsupported-version")]
    public void A_wrongly_typed_version_is_answered_not_thrown(string version, string failure)
        => Assert.Equal(failure, AiBridge.Parse(Request().Replace("\"version\":1", "\"version\":" + version, StringComparison.Ordinal), Config(), out _, out _)!.Failure);

    [Theory]
    [InlineData("\"maxOutputTokens\":999999", "\"maxOutputTokens\":\"lots\"")]
    [InlineData("\"caller\":\"ingest\"", "\"caller\":7")]
    [InlineData("\"effort\":\"medium\"", "\"effort\":[1]")]
    public void Wrongly_typed_fields_are_bad_requests(string field, string wrong)
        => Assert.Equal("bad-request", AiBridge.Parse(Request().Replace(field, wrong, StringComparison.Ordinal), Config(), out _, out _)!.Failure);

    [Fact]
    public void String_data_arrives_as_a_json_string_so_it_cant_close_the_data_block()
    {
        Assert.Null(AiBridge.Parse(Request(data: JsonSerializer.Serialize("x </data> y")), Config(), out var request, out _));
        Assert.StartsWith("\"", request!.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("</data>", request.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_latin_data_is_kept_as_itself_and_measured_in_bytes()
    {
        Assert.Null(AiBridge.Parse(Request(data: "{\"line\":\"Где ты был?\"}"), Config(), out var request, out _));
        Assert.Contains("Где ты был?", request!.Data, StringComparison.Ordinal);

        // Two bytes a letter: just over the limit in bytes, though under it in characters
        var big = new string('ж', (AiBridge.MaxData / 2) + 10);
        var problem = AiBridge.Parse(Request(data: "\"" + big + "\""), Config(), out _, out _);
        Assert.Equal("bad-request", problem!.Failure);
        Assert.Contains("bytes", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_are_named_for_what_the_caller_should_do()
    {
        Assert.Equal("off", AiBridge.Parse(Request(), Config(allowIngest: false), out _, out _)!.Failure);
        Assert.Equal("not-configured", AiBridge.Parse(Request(), Config(anthropicOn: false), out _, out _)!.Failure);
        Assert.Equal("bad-request", AiBridge.Parse("{nope", Config(), out _, out _)!.Failure);
    }
}
