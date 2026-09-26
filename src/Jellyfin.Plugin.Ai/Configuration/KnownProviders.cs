using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Ai.Configuration;

/// <summary>
/// The providers the plugin knows how to call.
/// </summary>
public static class KnownProviders
{
    /// <summary>Anthropic (Claude). The default: Claude Opus 5.5 unless another model is named.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>OpenAI.</summary>
    public const string OpenAi = "openai";

    /// <summary>Google (Gemini).</summary>
    public const string Google = "google";

    /// <summary>Any service with an OpenAI-compatible API: a local server (Ollama …), OpenRouter, Groq ….</summary>
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>Gets every known provider id, in the default order of preference.</summary>
    public static IReadOnlyList<string> All { get; } = [Anthropic, OpenAi, Google, OpenAiCompatible];

    /// <summary>
    /// Whether an id is a known provider.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns><c>true</c> if known.</returns>
    public static bool IsKnown(string? id) => id is not null && All.Contains(id, StringComparer.Ordinal);

    /// <summary>
    /// The default settings for a provider: Anthropic is allowed (it still needs a key), the others are off.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns>The settings.</returns>
    public static ProviderSettings Default(string id) => new() { Id = id, Enabled = string.Equals(id, Anthropic, StringComparison.Ordinal) };
}
