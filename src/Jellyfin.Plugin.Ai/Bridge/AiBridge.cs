using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text.Unicode;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Ai;
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
    // The contract is declared once, in common's client (FAM-04): these follow it, and a contract test runs the client
    // against this class, so a rename or a changed signature fails a test instead of becoming "not installed".

    /// <summary>The contract version (the client's <see cref="AiBridgeClient.Version"/>).</summary>
    public const int Version = AiBridgeClient.Version;

    /// <summary>The largest instructions accepted, in characters.</summary>
    public const int MaxInstructions = 16 * 1024;

    /// <summary>The largest data accepted, in UTF-8 bytes of its JSON (the client's <see cref="AiBridgeClient.MaxDataBytes"/>).</summary>
    public const int MaxData = AiBridgeClient.MaxDataBytes;

    /// <summary>The largest schema accepted, in characters.</summary>
    public const int MaxSchema = 16 * 1024;

    /// <summary>The largest output allowance accepted.</summary>
    public const int MaxOutputTokens = 16000;

    // Replies: letters in every script as themselves (FAM-02); < > & stay escaped
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    // Data passed on to the model: the same encoder as the callers' requests, so </data> can't appear inside it
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    private static ApiKeyStore? _keys;
    private static AiSpending? _spending;
    private static IHttpClientFactory? _http;

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
            return Failed("The AI plugin isn't ready yet.", "transient");
        }

        if (Parse(requestJson, config, out var request, out var provider) is { } problem)
        {
            return Failed(problem.Message, problem.Failure);
        }

        // Exchange rates are needed to price the call in the user's currency: the shared store refreshes them about once
        // a day, and at most every 30 minutes while they are missing or stale (AI-02, FAM-06). It never throws for network
        // problems: the last good rates are kept, and too old ones make the ledger refuse, with its own reason
        if (_http is not null)
        {
            using var http = _http.CreateClient();
            await _spending.Store.CurrentRatesAsync(http, cancellationToken).ConfigureAwait(false);
        }

        var (model, why) = AiModels.Create(config, provider!, null, _keys, _spending);
        if (model is null)
        {
            return Failed(why ?? "No AI provider can be used.", "not-configured");
        }

        try
        {
            var answer = await model.AskAsync(request!, cancellationToken).ConfigureAwait(false);
            return Answered(answer.Json, answer.Model);
        }
        catch (AiException ex)
        {
            return Failed(ex.Message, Name(ex.Failure));
        }
#pragma warning disable CA1031 // The entry point is documented never to throw: anything unexpected is a transient failure
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return Failed("The AI plugin failed (" + ex.GetType().Name + ").", "transient");
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
    /// <param name="http">HTTP clients (for exchange rates), if available.</param>
    internal static void Attach(ApiKeyStore keys, AiSpending spending, IHttpClientFactory? http = null)
    {
        _keys = keys;
        _spending = spending;
        _http = http;
    }

    /// <summary>
    /// Checks and reads a request.
    /// </summary>
    /// <param name="json">The request.</param>
    /// <param name="config">The settings.</param>
    /// <param name="request">The request, when valid.</param>
    /// <param name="provider">The provider to use.</param>
    /// <returns>Why it can't be answered, or <c>null</c> when it can.</returns>
    internal static Problem? Parse(string? json, PluginConfiguration config, out AiRequest? request, out string? provider)
    {
        ArgumentNullException.ThrowIfNull(config);
        request = null;
        provider = null;
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxInstructions + MaxData + MaxSchema + 4096)
        {
            return new("The request is empty or too large.", "bad-request");
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return new("The request isn't valid JSON.", "bad-request");
        }

        if (root is null)
        {
            return new("The request isn't a JSON object.", "bad-request");
        }

        // Every field is read by type: a wrong type is a bad request, never an exception
        if (!TryInt(root["version"], out var version) || version != Version)
        {
            return new("Unsupported request version (this plugin speaks version " + Version + "); update the Shoal plugins so they match.", "unsupported-version");
        }

        if (!TryString(root["caller"], out var caller) || !TryString(root["purpose"], out var purpose) || !TryString(root["instructions"], out var instructions)
            || !TryString(root["effort"], out var effort) || !TryInt(root["maxOutputTokens"], out var maxOutputTokens))
        {
            return new("A field has the wrong type.", "bad-request");
        }

        if (root["schema"] is not JsonObject schema)
        {
            return new("The request has no answer schema.", "bad-request");
        }

        // Data is always sent as JSON (a string arrives as a JSON string), escaped the same way whatever its type
        if (root["data"] is not { } dataNode)
        {
            return new("The request has no data.", "bad-request");
        }

        var data = dataNode.ToJsonString(Json);
        if (!config.Enabled)
        {
            return new("The AI plugin is switched off.", "off");
        }

        var allowed = caller switch { "ingest" => config.AllowIngest, "subtitles" => config.AllowSubtitles, _ => false };
        if (!allowed)
        {
            return new("The AI plugin isn't allowed to help " + caller + " (see its settings page).", "off");
        }

        if (purpose is null || !purpose.StartsWith(caller + ".", StringComparison.Ordinal) || purpose.Length > 64)
        {
            return new("The request's purpose must start with \"" + caller + ".\".", "bad-request");
        }

        var schemaText = schema.ToJsonString(Json);
        var dataBytes = Encoding.UTF8.GetByteCount(data);
        if (string.IsNullOrWhiteSpace(instructions) || instructions.Length > MaxInstructions || dataBytes > MaxData || schemaText.Length > MaxSchema)
        {
            return new(string.Create(CultureInfo.InvariantCulture, $"The request's instructions, data or schema are missing or too large (data {dataBytes:N0} of {MaxData:N0} bytes)."), "bad-request");
        }

        provider = config.Providers.FirstOrDefault(p => p is { Enabled: true } && KnownProviders.IsAvailable(p.Id))?.Id;
        if (provider is null)
        {
            return new("No usable AI provider is switched on (only Anthropic Claude is supported so far).", "not-configured");
        }

        using var schemaDoc = JsonDocument.Parse(schemaText);
        request = new AiRequest
        {
            Purpose = purpose,
            Instructions = instructions,
            Data = data,
            Schema = schemaDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
            MaxOutputTokens = Math.Clamp(maxOutputTokens ?? 2048, 256, MaxOutputTokens),
            Effort = effort switch { "medium" => AiEffort.Medium, "high" => AiEffort.High, _ => AiEffort.Low },
        };
        return null;
    }

    private static bool TryInt(JsonNode? node, out int? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i))
        {
            value = i;
            return true;
        }

        return false;
    }

    private static bool TryString(JsonNode? node, out string? value)
    {
        value = null;
        if (node is null)
        {
            return true;
        }

        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Why a request can't be answered.
    /// </summary>
    /// <param name="Message">In words safe to show.</param>
    /// <param name="Failure">The failure name: <c>off</c> (switched off or not allowed on the settings page),
    /// <c>not-configured</c> (no usable provider or key), <c>unsupported-version</c> or <c>bad-request</c>.</param>
    internal sealed record Problem(string Message, string Failure);

    private static string Name(FailureClass failure) => failure switch
    {
        FailureClass.Authentication => "authentication",
        FailureClass.ProviderLimit => "provider-limit",
        FailureClass.BadRequest => "bad-request",
        FailureClass.NoConnection => "no-connection",
        _ => "transient",
    };

    /// <summary>
    /// Writes an answer reply.
    /// </summary>
    /// <param name="answer">The answer.</param>
    /// <param name="model">The model that answered.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static string Answered(JsonElement answer, string model)
        => JsonSerializer.Serialize(new { version = Version, ok = true, answer, model }, Options);

    /// <summary>
    /// Writes a failure reply.
    /// </summary>
    /// <param name="error">Why, in words safe to show.</param>
    /// <param name="failure">The failure name.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static string Failed(string error, string failure)
        => JsonSerializer.Serialize(new { version = Version, ok = false, error, failure }, Options);
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
    /// <param name="http">HTTP clients.</param>
    public AiBridgeHost(ApiKeyStore keys, AiSpending spending, IHttpClientFactory http) => AiBridge.Attach(keys, spending, http);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
