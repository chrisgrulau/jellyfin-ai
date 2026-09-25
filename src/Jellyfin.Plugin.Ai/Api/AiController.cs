using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="AiController"/> class.
    /// </summary>
    /// <param name="keys">The key store.</param>
    public AiController(ApiKeyStore keys)
    {
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
