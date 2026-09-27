using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common;
using Jellyfin.Plugin.Common.Costs;

namespace Jellyfin.Plugin.Ai.Bridge;

/// <summary>
/// The spending entry point: the family's other plugins keep their paid calls within the one budget set on this plugin's
/// page (currency, overall monthly limit, a limit per provider), on this plugin's ledger. They find this type by name and
/// call <see cref="HandleAsync"/> with JSON, so no C# types are shared between plugins. There is no HTTP endpoint for it.
/// <para>
/// The contract (version 1) is declared once, in common's <see cref="SpendingBridgeClient"/>: <c>reserve</c> (purpose,
/// provider, estimate), <c>settle</c> (reservation, actual cost), <c>release</c> (reservation), <c>carry</c> (the
/// caller's own spending this month, replacing what it reported before) and <c>summary</c>. The caller prices its calls
/// with its own prices; this plugin converts them with its exchange rates.
/// </para>
/// <para>
/// Checks: the version; the caller (only <c>subtitles</c>, with <see cref="PluginConfiguration.AllowSubtitlesSpending"/>
/// on; <c>not-allowed</c> otherwise, and the caller then uses its own budget); the provider (a speech-to-text provider,
/// <see cref="KnownProviders.Speech"/>); amounts (0 to <see cref="SpendingBridgeClient.MaxAmount"/>, a supported
/// currency); and that only the caller that made a reservation settles or releases it. Reservations left open longer
/// than <see cref="SpendingBridgeClient.ReservationLifetime"/> are settled at their estimate. It doesn't depend on
/// <see cref="PluginConfiguration.Enabled"/>, which is about answering AI requests: the budget is kept either way.
/// </para>
/// </summary>
public static class SpendingBridge
{
    /// <summary>The contract version (the client's <see cref="SpendingBridgeClient.Version"/>).</summary>
    public const int Version = SpendingBridgeClient.Version;

    /// <summary>The largest request accepted, in UTF-8 bytes.</summary>
    public const int MaxRequest = 4096;

    private static AiSpending? _spending;
    private static IHttpClientFactory? _http;

    /// <summary>
    /// Answers a spending request from another plugin.
    /// </summary>
    /// <param name="requestJson">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply, as JSON. Never throws for a bad request.</returns>
    public static async Task<string> HandleAsync(string requestJson, CancellationToken cancellationToken)
    {
        if (_spending is not { } spending || AiPlugin.Instance?.Configuration is not { } config)
        {
            return Failed("The AI plugin isn't ready yet.", "transient");
        }

        var http = _http;
        return await AnswerAsync(
            requestJson,
            config,
            spending,
            http is null ? null : async ct =>
            {
                using var client = http.CreateClient();
                await spending.Store.CurrentRatesAsync(client, ct).ConfigureAwait(false);
            },
            null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects the entry point to the plugin's services (at start-up).
    /// </summary>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <param name="http">HTTP clients (for exchange rates), if available.</param>
    internal static void Attach(AiSpending spending, IHttpClientFactory? http)
    {
        _spending = spending;
        _http = http;
    }

    /// <summary>
    /// Answers a request with the given settings and services (the entry point's work, testable without the plugin).
    /// </summary>
    /// <param name="requestJson">The request.</param>
    /// <param name="config">The settings.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <param name="refreshRates">Refreshes the exchange rates when due, before a reservation, if available.</param>
    /// <param name="clock">Clock (the month of a carry); the system's by default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static async Task<string> AnswerAsync(string? requestJson, PluginConfiguration config, AiSpending spending, Func<CancellationToken, Task>? refreshRates, TimeProvider? clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(spending);
        if (string.IsNullOrEmpty(requestJson) || Encoding.UTF8.GetByteCount(requestJson) > MaxRequest)
        {
            return Failed("The request is empty or too large.", "bad-request");
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(requestJson) as JsonObject;
        }
        catch (JsonException)
        {
            return Failed("The request isn't valid JSON.", "bad-request");
        }

        if (root is null)
        {
            return Failed("The request isn't a JSON object.", "bad-request");
        }

        if (Int(root["version"]) != Version)
        {
            return Failed("Unsupported request version (this plugin speaks version " + Version + "); update the Shoal plugins so they match.", "unsupported-version");
        }

        var caller = Text(root["caller"]);
        if (caller != "subtitles" || !config.AllowSubtitlesSpending)
        {
            return Failed("Shoal AI isn't keeping " + (caller == "subtitles" ? "Subtitles'" : "this plugin's") + " spending (see its settings page), so its own spending limit applies.", "not-allowed");
        }

        var ledger = spending.Ledger;
        ledger.ExpireOpen(SpendingBridgeClient.ReservationLifetime);
        var limits = AiSpending.LimitsOf(config);
        switch (Text(root["op"]))
        {
            case "reserve":
            {
                var purpose = Text(root["purpose"]);
                if (!IsPurpose(purpose))
                {
                    return Failed("The purpose must be a short name such as subtitles.sync.", "bad-request");
                }

                if (Provider(root["provider"]) is not { } provider)
                {
                    return Failed("Unknown speech-to-text provider (Shoal AI keeps " + string.Join(" and ", KnownProviders.Speech) + ").", "bad-request");
                }

                if (Amount(root["estimate"]) is not { } estimate)
                {
                    return Failed("The estimate must be an amount from 0 to " + SpendingBridgeClient.MaxAmount.ToString(CultureInfo.InvariantCulture) + " in a supported currency.", "bad-request");
                }

                if (refreshRates is not null)
                {
                    await refreshRates(cancellationToken).ConfigureAwait(false);
                }

                var decision = ledger.TryReserve(provider, purpose!, estimate, limits, spending.Rates.Current, caller);
                return decision.ReservationId is { } id
                    ? Json(new { version = Version, ok = true, reservationId = id.ToString("D") })
                    : Failed(decision.Refusal ?? "Not allowed by the spending limits.", "provider-limit");
            }

            case "settle":
                if (Id(root["reservationId"]) is not { } settled || Amount(root["actual"]) is not { } actual)
                {
                    return Failed("A reservation and an actual amount are needed.", "bad-request");
                }

                return ledger.Settle(settled, actual, caller) ? Done() : Failed("Unknown or expired reservation (an expired one stays at its estimate).", "bad-request");

            case "release":
                if (Id(root["reservationId"]) is not { } released)
                {
                    return Failed("A reservation is needed.", "bad-request");
                }

                return ledger.Release(released, caller) ? Done() : Failed("Unknown, settled or expired reservation.", "bad-request");

            case "carry":
            {
                if (Provider(root["provider"]) is not { } provider || Amount(root["amount"]) is not { } amount)
                {
                    return Failed("A known speech-to-text provider and an amount are needed.", "bad-request");
                }

                if (Text(root["month"]) != SpendingBridgeClient.MonthOf((clock ?? TimeProvider.System).GetLocalNow()))
                {
                    return Failed("That month is over; only this month's spending is carried.", "bad-request");
                }

                ledger.RecordCarried(caller, provider, caller + ".carried", amount);
                return Done();
            }

            case "summary":
            {
                var rates = spending.Rates.Current;
                var month = ledger.ThisMonth(limits, rates);
                return Json(new
                {
                    version = Version,
                    ok = true,
                    currency = limits.Currency,
                    limit = limits.Overall,
                    spent = month.Total is { } t ? decimal.Round(t, 4) : (decimal?)null,
                    perProvider = month.PerProvider.ToDictionary(p => p.Key, p => decimal.Round(p.Value, 4), StringComparer.OrdinalIgnoreCase),
                    providerLimits = limits.PerProvider.ToDictionary(p => p.Key, p => decimal.Round(p.Value, 4), StringComparer.OrdinalIgnoreCase),
                    ratesDate = rates?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ratesFresh = rates is not null && rates.IsFresh(DateOnly.FromDateTime((clock ?? TimeProvider.System).GetLocalNow().DateTime)),
                });
            }

            default:
                return Failed("Unknown operation (reserve, settle, release, carry or summary).", "bad-request");
        }
    }

    /// <summary>
    /// Writes a failure reply.
    /// </summary>
    /// <param name="error">Why, in words safe to show.</param>
    /// <param name="failure">The failure name.</param>
    /// <returns>The reply, as JSON.</returns>
    internal static string Failed(string error, string failure) => Json(new { version = Version, ok = false, error, failure });

    private static string Done() => Json(new { version = Version, ok = true });

    private static string Json(object value) => JsonSerializer.Serialize(value, BridgeJson.Options);

    private static string? Text(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static int? Int(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i : null;

    private static string? Provider(JsonNode? node) => Text(node) is { } p && KnownProviders.IsSpeech(p) ? p : null;

    private static Guid? Id(JsonNode? node) => Text(node) is { } s && Guid.TryParse(s, out var g) ? g : null;

    // Money as the contract writes it: {"amount": number, "currency": "USD"}
    private static Money? Amount(JsonNode? node)
    {
        if (node is not JsonObject o || o["amount"] is not JsonValue a || a.GetValueKind() != JsonValueKind.Number
            || !a.TryGetValue<decimal>(out var amount) || amount < 0 || amount > SpendingBridgeClient.MaxAmount
            || !CurrencyCode.IsSupported(Text(o["currency"])))
        {
            return null;
        }

        return new Money(amount, CurrencyCode.Normalise(Text(o["currency"]))!);
    }

    // A purpose is an identifier such as subtitles.sync or ingest.episode (Subtitles meters Ingest's transcripts too)
    private static bool IsPurpose(string? purpose)
        => purpose is { Length: > 2 and <= 64 } && purpose.Contains('.', StringComparison.Ordinal) && char.IsAsciiLetter(purpose[0])
            && purpose.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
