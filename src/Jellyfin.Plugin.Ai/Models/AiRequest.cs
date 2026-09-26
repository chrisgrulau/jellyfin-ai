using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// How hard the model should think: more effort is slower and costs more (thinking is billed as output).
/// </summary>
public enum AiEffort
{
    /// <summary>Quick decisions between a few options.</summary>
    Low = 0,

    /// <summary>Harder judgement.</summary>
    Medium,

    /// <summary>The hardest cases.</summary>
    High,
}

/// <summary>
/// One question for a model. The instructions are fixed text written by the calling plugin; everything that came from
/// outside (release names, file names, subtitle text) goes in <see cref="Data"/>, which the model is told to treat as data
/// only. The answer must match <see cref="Schema"/>, and callers check it against the options they offered.
/// </summary>
public sealed record AiRequest
{
    /// <summary>Gets what the request is for (<c>ingest.match</c>, <c>subtitles.match</c> …), for budgets and the record.</summary>
    public required string Purpose { get; init; }

    /// <summary>Gets the instructions (fixed text from the calling plugin).</summary>
    public required string Instructions { get; init; }

    /// <summary>Gets the data to decide on, as JSON (untrusted content lives here only).</summary>
    public required string Data { get; init; }

    /// <summary>Gets the JSON schema the answer must match.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Schema { get; init; }

    /// <summary>Gets the most output tokens (thinking included) the answer may use.</summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>Gets the effort.</summary>
    public AiEffort Effort { get; init; } = AiEffort.Low;
}

/// <summary>
/// A model's answer.
/// </summary>
/// <param name="Json">The answer, matching the request's schema.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model that answered.</param>
/// <param name="InputTokens">Input tokens billed.</param>
/// <param name="OutputTokens">Output tokens billed (thinking included).</param>
public sealed record AiAnswer(JsonElement Json, string Provider, string Model, long InputTokens, long OutputTokens);

/// <summary>
/// A request couldn't be answered. The message is safe to show and log.
/// </summary>
public sealed class AiException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AiException"/> class.</summary>
    public AiException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AiException"/> class.</summary>
    /// <param name="message">A safe message.</param>
    public AiException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AiException"/> class.</summary>
    /// <param name="message">A safe message.</param>
    /// <param name="innerException">The cause.</param>
    public AiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets what kind of failure it was (decides retries and alerts).</summary>
    internal Common.Resilience.FailureClass Failure { get; init; } = Common.Resilience.FailureClass.Transient;
}

/// <summary>
/// A text model.
/// </summary>
public interface IAiModel
{
    /// <summary>Gets the provider id.</summary>
    string Provider { get; }

    /// <summary>Gets the model id used.</summary>
    string Model { get; }

    /// <summary>
    /// Asks the model.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="AiException">It couldn't be answered.</exception>
    System.Threading.Tasks.Task<AiAnswer> AskAsync(AiRequest request, System.Threading.CancellationToken cancellationToken);
}
