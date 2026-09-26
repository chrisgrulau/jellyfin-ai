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

    /// <summary>
    /// Initializes a new instance of the <see cref="AiController"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    /// <param name="spending">Prices, spend ledger and exchange rates.</param>
    /// <param name="http">HTTP client factory (for exchange rates).</param>
    public AiController(ApiKeyStore keys, Pricing.AiSpending spending, IHttpClientFactory http)
    {
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
        return NoContent();
    }

    /// <summary>
    /// Checks that a provider answers, with one tiny request (a fraction of a cent, counted like any other call).
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

        var (model, problem) = Models.AiModels.Create(config, request.Provider ?? string.Empty, request.Model, _keys, _spending);
        if (model is null)
        {
            return new TestResult(false, problem ?? "Can't be used.");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var answer = await model.AskAsync(
                new Models.AiRequest
                {
                    Purpose = "ai.test",
                    Instructions = "This is a connection test. Answer with ok set to true.",
                    Data = "{}",
                    Schema = TestSchema,
                    MaxOutputTokens = 1024,
                },
                cancellationToken).ConfigureAwait(false);
            var cost = (model as Models.MeteredModel)?.LastCost;
            return new TestResult(true, string.Create(CultureInfo.InvariantCulture, $"Connected: {answer.Model} answered in {clock.Elapsed.TotalSeconds:0.0} s ({answer.InputTokens} + {answer.OutputTokens} tokens{(cost is { } c ? ", " + c : string.Empty)})."));
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
    /// This month's spending on AI providers, in the user's currency.
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
            Pricing.AiSpending.Currencies);
    }

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

    private static readonly IReadOnlyDictionary<string, JsonElement> TestSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new { ok = new { type = "boolean" } }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "ok" }),
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
}

/// <summary>
/// The outcome of a test.
/// </summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Message">What to show.</param>
public sealed record TestResult(bool Ok, string Message);

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
public sealed record SpendingSummary(string Currency, decimal? Limit, decimal? Spent, IReadOnlyDictionary<string, decimal> PerProvider, string? RatesDate, bool RatesFresh, string? PricesVersion, IReadOnlyList<string> Currencies);
