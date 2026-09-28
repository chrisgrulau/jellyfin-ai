using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Budgets;
using Jellyfin.Plugin.Ai.Calls;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Keys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Ai.Api;

/// <summary>
/// Settings-page endpoints, for administrators only: API key status and changes (keys are write-only: never returned),
/// and a check of the spending limits.
/// </summary>
[ApiController]
[Route("Ai")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class AiController : ControllerBase
{
    private readonly ApiKeyStore _keys;
    private readonly Pricing.AiSpending _spending;
    private readonly IHttpClientFactory _http;
    private readonly CallLog _log;
    private readonly Models.ModelResolver _resolver;
    private readonly Health.ProviderHealthLog _health;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiController"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="http">HTTP client factory (for exchange rates and Gemini calls).</param>
    /// <param name="log">The call log.</param>
    /// <param name="resolver">Resolves "the current model" of a family.</param>
    /// <param name="health">Each provider's recent record.</param>
    public AiController(ApiKeyStore keys, Pricing.AiSpending spending, IHttpClientFactory http, CallLog log, Models.ModelResolver resolver, Health.ProviderHealthLog health)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    /// <summary>
    /// Which providers have a key (the keys themselves are never returned).
    /// </summary>
    /// <returns>Provider id → whether a key is set.</returns>
    [HttpGet("Keys")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyDictionary<string, bool>> KeyStatus() => Ok(_keys.Status());

    /// <summary>
    /// Stores or replaces a provider's key.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <param name="request">The key.</param>
    /// <returns>No content.</returns>
    [HttpPut("Keys/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SetKey([FromRoute] string provider, [FromBody, Required] SetKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!KnownProviders.IsKnown(provider))
        {
            return BadRequest("Unknown provider.");
        }

        if (!ApiKeyStore.IsWellFormed(request.Key?.Trim()))
        {
            return BadRequest("That doesn't look like an API key.");
        }

        _keys.Set(provider, request.Key!);

        // A new key: an earlier refusal says nothing about it
        _health.Forget(provider);
        return NoContent();
    }

    /// <summary>
    /// Removes a provider's key.
    /// </summary>
    /// <param name="provider">Provider id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Keys/{provider}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearKey([FromRoute] string provider)
    {
        _keys.Clear(provider);
        _health.Forget(provider);
        return NoContent();
    }

    /// <summary>
    /// Checks that a provider answers, with one tiny request (a fraction of a cent, counted like any other call), and shows
    /// its reply and what it cost.
    /// </summary>
    /// <param name="request">The provider and model to test, as on the settings page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it worked, and a message to show.</returns>
    [HttpPost("Test")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<TestResult>> Test([FromBody, Required] TestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        using (var http = _http.CreateClient())
        {
            await _spending.Rates.RefreshAsync(http, cancellationToken).ConfigureAwait(false);
        }

        // What the page shows (an address or prices not saved yet), tidied the way saving would
        ProviderSettings? unsaved = null;
        if (request.Settings is { } shown && shown.Id == request.Provider)
        {
            var tidy = new PluginConfiguration();
            tidy.Providers.Add(shown);
            BudgetRules.Normalise(tidy);
            unsaved = tidy.Providers.First(p => p.Id == shown.Id);
        }

        var (model, problem) = await Models.AiModels.CreateAsync(config, request.Provider ?? string.Empty, request.Model, Services(), unsaved, cancellationToken).ConfigureAwait(false);
        var context = Bridge.AiBridge.Context("test", config, _keys, _spending, request.Provider, _health);
        if (model is null)
        {
            _log.RecordProblem(context, TestPurpose, request.Provider, 0, problem ?? "Can't be used.", "not-configured");
            return new TestResult(false, problem ?? "Can't be used.");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer = await _log.AskAsync(
                model,
                new Models.AiRequest
                {
                    Purpose = TestPurpose,
                    Instructions = "This is a connection test. Answer with ok set to true and a short greeting of at most five words.",
                    Data = "{}",
                    Schema = TestSchema,
                    MaxOutputTokens = 1024,
                },
                context,
                cancellationToken).ConfigureAwait(false);
            var cost = model is Models.FreeModel ? "free (not metered)" : (model as Models.MeteredModel)?.LastCost is { } c ? CostText(c, config) : "cost unknown";
            var reply = Reply(answer.Json);
            return new TestResult(
                true,
                string.Create(CultureInfo.InvariantCulture, $"Connected: {answer.Model} answered in {clock.Elapsed.TotalSeconds:0.0} s ({answer.InputTokens} + {answer.OutputTokens} tokens, {cost}). It said: {reply}"))
            {
                Model = answer.Model,
                Reply = reply,
                Cost = cost,
            };
        }
        catch (Models.AiException ex)
        {
            return new TestResult(false, ex.Message);
        }
        finally
        {
            (model as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// How the providers have been doing: systemic problems (shown as a banner, with what to do) and each provider's
    /// recent record. Transient failures stay out of the banner.
    /// </summary>
    /// <returns>The health.</returns>
    [HttpGet("Health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HealthView> ProviderHealth()
    {
        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var all = _health.Health(config);

        // A switched-off provider's old trouble isn't worth a banner
        var on = ProviderRules.Order(config);
        return new HealthView([.. all.Where(h => h.Systemic && on.Contains(h.Provider))], all);
    }

    /// <summary>
    /// A provider's model families and current automatic choice, and the models it offers (for the settings page). Reads
    /// the provider's model list, with the key saved for it.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="family">The family to resolve (empty for the saved one, then the recommended one).</param>
    /// <param name="address">An OpenAI-compatible service's address as typed on the page (empty for the saved one).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The models.</returns>
    [HttpGet("Models/{provider}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ModelsView>> ProviderModels([FromRoute] string provider, [FromQuery] string? family, [FromQuery] string? address, CancellationToken cancellationToken)
    {
        if (!KnownProviders.IsKnown(provider))
        {
            return BadRequest("Unknown provider.");
        }

        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var settings = config.Providers.FirstOrDefault(p => p?.Id == provider) ?? KnownProviders.Default(provider);
        var families = Models.ModelCatalog.Of(provider).Select(f => new FamilyView(f.Id, f.Label, f.IsDefault)).ToList();
        var key = _keys.Get(provider);
        IReadOnlyList<string> listed = [];
        string? problem = null;
        using var http = _http.CreateClient();
        try
        {
            switch (provider)
            {
                case KnownProviders.OpenAi when key is not null:
                    listed = await Models.OpenAiChatModel.ListAsync(key, null, null, cancellationToken).ConfigureAwait(false);
                    break;
                case KnownProviders.Google when key is not null:
                    listed = await Models.GeminiModel.ListAsync(key, http, cancellationToken).ConfigureAwait(false);
                    break;
                case KnownProviders.OpenAiCompatible:
                    problem = ProviderRules.AddressProblem(string.IsNullOrWhiteSpace(address) ? settings.BaseUrl : address, out var endpoint);
                    if (endpoint is not null)
                    {
                        listed = await Models.OpenAiChatModel.ListAsync(key, endpoint, null, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case KnownProviders.Anthropic:
                    problem = "Claude's recommended model is " + Models.ClaudeModel.DefaultModel + ", updated with plugin releases.";
                    break;
                default:
                    problem = "Add an API key first.";
                    break;
            }
        }
        catch (Models.AiException ex)
        {
            problem = ex.Message;
        }

        string? current = null;
        if (Models.ModelCatalog.Family(provider, string.IsNullOrWhiteSpace(family) ? settings.Family : family) is { } chosen)
        {
            current = listed.Count > 0
                ? (await _resolver.ResolveAsync(chosen, _ => Task.FromResult(listed), id => Models.AiModels.Priced(_spending, provider, id), cancellationToken).ConfigureAwait(false)).Model
                : _resolver.Current().FirstOrDefault(r => r.Provider == provider && r.Family == chosen.Id)?.Model ?? chosen.Fallback;
        }
        else if (provider == KnownProviders.Anthropic)
        {
            current = Models.ClaudeModel.DefaultModel;
        }

        var priced = provider is KnownProviders.OpenAi or KnownProviders.Google;
        return new ModelsView(
            current,
            families,
            [.. listed.Where(id => !priced || Models.AiModels.Priced(_spending, provider, id)).Order(StringComparer.Ordinal).Take(500)],
            problem);
    }

    /// <summary>
    /// The curated model families of each provider and the automatic choices made so far (no provider is asked).
    /// </summary>
    /// <returns>The families and choices.</returns>
    [HttpGet("Families")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FamiliesView> Families()
        => new FamiliesView(
            Models.ModelCatalog.Families.GroupBy(f => f.Provider).ToDictionary(g => g.Key, g => (IReadOnlyList<FamilyView>)[.. g.Select(f => new FamilyView(f.Id, f.Label, f.IsDefault))], StringComparer.Ordinal),
            _resolver.Current(),
            Models.ClaudeModel.DefaultModel);

    private Models.ModelServices Services() => new(_keys, _spending, _resolver, () => _http.CreateClient());

    // What a test cost, in the settings' currency when it can be converted
    private string CostText(Common.Costs.Money cost, PluginConfiguration config)
    {
        var limits = Pricing.AiSpending.LimitsOf(config);
        var shown = Common.Costs.CostConverter.ToUserCurrency(cost, limits.Currency, _spending.Rates.Current, DateOnly.FromDateTime(DateTime.Now), limits.ExtraPercent) ?? cost;
        return CallPresenter.Money(shown.Amount, shown.Currency);
    }

    // The test answer, short: the greeting if it gave one, else the JSON
    private static string Reply(JsonElement json)
    {
        var text = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("greeting", out var g) && g.ValueKind == JsonValueKind.String
            ? "\u201C" + g.GetString() + "\u201D"
            : json.GetRawText();
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    /// <summary>
    /// A page of recent AI calls, newest first, presented for the settings page, and the last error if it is newer than
    /// the last answered call. Never what was sent.
    /// </summary>
    /// <param name="limit">The most calls to return (1 to 1,000; 15 by default).</param>
    /// <param name="before">The previous page's <c>Next</c> cursor, for the next (older) page; omitted for the newest.</param>
    /// <param name="caller">Only this caller's calls (<c>ingest</c>, <c>subtitles</c>, <c>test</c>), or all.</param>
    /// <returns>The calls.</returns>
    [HttpGet("Calls")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<CallLogView> Calls([FromQuery] int? limit, [FromQuery] long? before, [FromQuery] string? caller)
    {
        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        return _log.View(limit ?? CallLog.PageSize, before, caller, config.KeepCallLog);
    }

    /// <summary>
    /// Empties the call log.
    /// </summary>
    /// <returns>No content.</returns>
    [HttpDelete("Calls")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public ActionResult ClearCalls()
    {
        try
        {
            _log.Clear();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return Problem("The call log couldn't be cleared: " + ex.GetType().Name + ".");
        }

        return NoContent();
    }

    /// <summary>
    /// This month's spending on paid providers (AI, and Subtitles' speech-to-text kept within this budget), in the user's
    /// currency.
    /// </summary>
    /// <returns>The spending.</returns>
    [HttpGet("Spending")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SpendingSummary> Spending()
    {
        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        var limits = Pricing.AiSpending.LimitsOf(config);
        var rates = _spending.Rates.Current;
        var month = _spending.Ledger.ThisMonth(limits, rates);
        return new SpendingSummary(
            limits.Currency,
            limits.Overall,
            month.Total,
            month.PerProvider.ToDictionary(p => p.Key, p => decimal.Round(p.Value, 4), StringComparer.OrdinalIgnoreCase),
            rates?.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            rates is not null && rates.IsFresh(DateOnly.FromDateTime(DateTime.Now)),
            _spending.Prices?.Version,
            Pricing.AiSpending.Currencies,
            SpeechShown(config, month.PerProvider, SubtitlesInstalled()));
    }

    /// <summary>
    /// The speech-to-text providers the Spending section lists: all of them while Shoal Subtitles is installed, otherwise
    /// those with spending this month or a limit of their own.
    /// </summary>
    /// <param name="config">The settings.</param>
    /// <param name="spent">This month's spending per provider.</param>
    /// <param name="subtitlesInstalled">Whether Shoal Subtitles is loaded.</param>
    /// <returns>The provider ids, in <see cref="KnownProviders.Speech"/> order.</returns>
    internal static IReadOnlyList<string> SpeechShown(PluginConfiguration config, IReadOnlyDictionary<string, decimal> spent, bool subtitlesInstalled)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(spent);
        return [.. KnownProviders.Speech.Where(id => subtitlesInstalled
            || spent.ContainsKey(id)
            || config.Providers.Any(p => p is not null && p.Id == id && p.BudgetMode != ProviderBudgetMode.OverallOnly))];
    }

    // Shoal Subtitles is loaded in this server (its speech-to-text can be kept within this budget)
    private static bool SubtitlesInstalled()
        => AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, "Jellyfin.Plugin.Subtitles", StringComparison.Ordinal));

    /// <summary>
    /// What's left of each prepaid credit being tracked.
    /// </summary>
    /// <returns>The credits.</returns>
    [HttpGet("Credit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<Pricing.CreditLeft>> Credit()
    {
        var config = AiPlugin.Instance?.Configuration ?? new PluginConfiguration();
        return config.Providers
            .Select(p => Pricing.PrepaidCredit.Of(p, _spending.Ledger, _spending.Rates.Current))
            .OfType<Pricing.CreditLeft>()
            .ToList();
    }

    private const string TestPurpose = "ai.test";

    private static readonly IReadOnlyDictionary<string, JsonElement> TestSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new { ok = new { type = "boolean" }, greeting = new { type = "string" } }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "ok", "greeting" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    /// <summary>
    /// Checks spending-limit settings before they are saved.
    /// </summary>
    /// <param name="settings">The settings as they would be saved.</param>
    /// <returns>Warnings and errors, if any.</returns>
    [HttpPost("Budget/Check")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<BudgetMessage>> CheckBudget([FromBody, Required] PluginConfiguration settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BudgetRules.Normalise(settings);
        return Ok(BudgetRules.Check(settings));
    }
}

/// <summary>
/// Body of <see cref="AiController.SetKey"/>.
/// </summary>
public sealed record SetKeyRequest
{
    /// <summary>Gets the API key.</summary>
    public string? Key { get; init; }
}

/// <summary>
/// Body of <see cref="AiController.Test"/>.
/// </summary>
public sealed record TestRequest
{
    /// <summary>Gets the provider id.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the model (empty for the setting or the provider's default).</summary>
    public string? Model { get; init; }

    /// <summary>Gets the provider's settings as shown on the page (not yet saved), if sent; otherwise the saved ones are used.</summary>
    public ProviderSettings? Settings { get; init; }
}

/// <summary>
/// The outcome of a test.
/// </summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Message">What to show.</param>
public sealed record TestResult(bool Ok, string Message)
{
    /// <summary>Gets the model that answered, when it worked.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the model's reply, short, when it worked.</summary>
    public string? Reply { get; init; }

    /// <summary>Gets what the test cost (<c>AUD 0.0012</c>, or free), when it worked.</summary>
    public string? Cost { get; init; }
}

/// <summary>
/// How the providers have been doing.
/// </summary>
/// <param name="Problems">Switched-on providers with a systemic problem (shown as a banner).</param>
/// <param name="Providers">Every provider's recent record.</param>
public sealed record HealthView(IReadOnlyList<Health.AiProviderHealth> Problems, IReadOnlyList<Health.AiProviderHealth> Providers);

/// <summary>
/// A model family, for the settings page.
/// </summary>
/// <param name="Id">The family id.</param>
/// <param name="Label">Its name for people.</param>
/// <param name="IsDefault">Whether it is the recommended one.</param>
public sealed record FamilyView(string Id, string Label, bool IsDefault);

/// <summary>
/// The curated model families and the automatic choices made so far.
/// </summary>
/// <param name="Families">Provider id → its families, the recommended one first.</param>
/// <param name="Current">The automatic choices made since the server started (provider, family, model, when checked).</param>
/// <param name="ClaudeDefault">Claude's recommended model (it has no automatic choice yet).</param>
public sealed record FamiliesView(IReadOnlyDictionary<string, IReadOnlyList<FamilyView>> Families, IReadOnlyList<Models.ResolvedModel> Current, string ClaudeDefault);

/// <summary>
/// A provider's models, for the settings page.
/// </summary>
/// <param name="Current">The model the automatic choice uses now (for the family asked about), if known.</param>
/// <param name="Families">The provider's model families (empty for providers without automatic choice).</param>
/// <param name="Available">The models the provider offers (for OpenAI and Google, only those with a published price).</param>
/// <param name="Problem">Why the list couldn't be read, if it couldn't.</param>
public sealed record ModelsView(string? Current, IReadOnlyList<FamilyView> Families, IReadOnlyList<string> Available, string? Problem);

/// <summary>
/// This month's spending on AI providers.
/// </summary>
/// <param name="Currency">The user's currency.</param>
/// <param name="Limit">The overall monthly limit, or <c>null</c> for no limit.</param>
/// <param name="Spent">Spent so far this month (open reservations included), or <c>null</c> if it can't be converted.</param>
/// <param name="PerProvider">Spent per provider.</param>
/// <param name="RatesDate">The date of the exchange rates in use, if any.</param>
/// <param name="RatesFresh">Whether those rates are recent enough to use.</param>
/// <param name="PricesVersion">The version of the published prices shipped with the plugin.</param>
/// <param name="Currencies">The currencies that can be chosen (so the settings page doesn't copy the list).</param>
/// <param name="SpeechProviders">The speech-to-text providers to list with a limit (Shoal Subtitles' Deepgram and OpenAI,
/// kept within this budget): all while Subtitles is installed, else those with spending this month or a limit.</param>
public sealed record SpendingSummary(string Currency, decimal? Limit, decimal? Spent, IReadOnlyDictionary<string, decimal> PerProvider, string? RatesDate, bool RatesFresh, string? PricesVersion, IReadOnlyList<string> Currencies, IReadOnlyList<string> SpeechProviders);
