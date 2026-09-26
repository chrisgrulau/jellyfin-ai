using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Ai.Bridge;

/// <summary>
/// The entry point the other plugins of the family use, in the same server process: they find this type by name and
/// call <see cref="AskAsync"/> with JSON, so no C# types are shared between plugins. There is no HTTP endpoint for it.
/// <para>
/// Request (version 1): <c>{"version":1,"caller":"ingest","purpose":"ingest.match","instructions":"…","data":{…},
/// "schema":{…},"maxOutputTokens":2048,"effort":"low"}</c>. Reply: <c>{"version":1,"ok":true,"answer":{…},
/// "model":"…"}</c> or <c>{"version":1,"ok":false,"error":"…","failure":"not-allowed|authentication|provider-limit|
/// transient|bad-request|no-connection"}</c>.
/// </para>
/// <para>
/// Checks: the version, sizes, that the caller is allowed on the settings page, and that the purpose belongs to the
/// caller. Spending is metered as for any call. The answer is only guaranteed to match the schema: callers must still
/// check it against what they offered.
/// </para>
/// </summary>
public static class AiBridge
{
    /// <summary>The contract version.</summary>
    public const int Version = 1;

    /// <summary>The largest instructions accepted, in characters.</summary>
    public const int MaxInstructions = 16 * 1024;

    /// <summary>The largest data accepted, in characters.</summary>
    public const int MaxData = 64 * 1024;

    /// <summary>The largest schema accepted, in characters.</summary>
    public const int MaxSchema = 16 * 1024;

    /// <summary>The largest output allowance accepted.</summary>
    public const int MaxOutputTokens = 16000;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static ApiKeyStore? _keys;
    private static AiSpending? _spending;

    /// <summary>
    /// Answers a request from another plugin.
    /// </summary>
    /// <param name="requestJson">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply, as JSON. Never throws for a bad request or a failed call.</returns>
    public static async Task<string> AskAsync(string requestJson, CancellationToken cancellationToken)
    {
        if (_keys is null || _spending is null || AiPlugin.Instance?.Configuration is not { } config)
        {
            return Reply(false, "The AI plugin isn't ready yet.", "transient");
        }

        if (Parse(requestJson, config, out var request, out var provider) is { } problem)
        {
            return Reply(false, problem, "not-allowed");
        }

        var (model, why) = AiModels.Create(config, provider!, null, _keys, _spending);
        if (model is null)
        {
            return Reply(false, why ?? "No AI provider can be used.", "not-allowed");
        }

        try
        {
            var answer = await model.AskAsync(request!, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { version = Version, ok = true, answer = answer.Json, model = answer.Model }, Options);
        }
        catch (AiException ex)
        {
            return Reply(false, ex.Message, Name(ex.Failure));
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Connects the entry point to the plugin's services (at start-up).
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    internal static void Attach(ApiKeyStore keys, AiSpending spending)
    {
        _keys = keys;
        _spending = spending;
    }

    /// <summary>
    /// Checks and reads a request.
    /// </summary>
    /// <param name="json">The request.</param>
    /// <param name="config">The settings.</param>
    /// <param name="request">The request, when valid.</param>
    /// <param name="provider">The provider to use.</param>
    /// <returns>Why it can't be answered, or <c>null</c> when it can.</returns>
    internal static string? Parse(string? json, PluginConfiguration config, out AiRequest? request, out string? provider)
    {
        ArgumentNullException.ThrowIfNull(config);
        request = null;
        provider = null;
        if (string.IsNullOrEmpty(json) || json.Length > MaxInstructions + MaxData + MaxSchema + 4096)
        {
            return "The request is empty or too large.";
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return "The request isn't valid JSON.";
        }

        if (root is null || (int?)root["version"] != Version)
        {
            return "Unsupported request version (this plugin speaks version " + Version + ").";
        }

        var caller = (string?)root["caller"];
        var purpose = (string?)root["purpose"];
        var instructions = (string?)root["instructions"];
        var data = root["data"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : root["data"]?.ToJsonString();
        if (root["schema"] is not JsonObject schema)
        {
            return "The request has no answer schema.";
        }

        if (!config.Enabled)
        {
            return "The AI plugin is switched off.";
        }

        var allowed = caller switch { "ingest" => config.AllowIngest, "subtitles" => config.AllowSubtitles, _ => false };
        if (!allowed)
        {
            return "The AI plugin isn't allowed to help " + caller + " (see its settings page).";
        }

        if (purpose is null || !purpose.StartsWith(caller + ".", StringComparison.Ordinal) || purpose.Length > 64)
        {
            return "The request's purpose must start with \"" + caller + ".\".";
        }

        var schemaText = schema.ToJsonString();
        if (string.IsNullOrWhiteSpace(instructions) || instructions.Length > MaxInstructions || data is null || data.Length > MaxData || schemaText.Length > MaxSchema)
        {
            return "The request's instructions, data or schema are missing or too large.";
        }

        provider = config.Providers.FirstOrDefault(p => p is { Enabled: true } && p.Id == KnownProviders.Anthropic)?.Id;
        if (provider is null)
        {
            return "No AI provider is switched on.";
        }

        using var schemaDoc = JsonDocument.Parse(schemaText);
        request = new AiRequest
        {
            Purpose = purpose,
            Instructions = instructions,
            Data = data,
            Schema = schemaDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            MaxOutputTokens = Math.Clamp((int?)root["maxOutputTokens"] ?? 2048, 256, MaxOutputTokens),
            Effort = ((string?)root["effort"]) switch { "medium" => AiEffort.Medium, "high" => AiEffort.High, _ => AiEffort.Low },
        };
        return null;
    }

    private static string Name(FailureClass failure) => failure switch
    {
        FailureClass.Authentication => "authentication",
        FailureClass.ProviderLimit => "provider-limit",
        FailureClass.BadRequest => "bad-request",
        FailureClass.NoConnection => "no-connection",
        _ => "transient",
    };

    private static string Reply(bool ok, string error, string failure)
        => JsonSerializer.Serialize(new { version = Version, ok, error, failure }, Options);
}

/// <summary>
/// Connects <see cref="AiBridge"/> to the plugin's services when the server starts.
/// </summary>
internal sealed class AiBridgeHost : Microsoft.Extensions.Hosting.IHostedService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiBridgeHost"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    public AiBridgeHost(ApiKeyStore keys, AiSpending spending) => AiBridge.Attach(keys, spending);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
