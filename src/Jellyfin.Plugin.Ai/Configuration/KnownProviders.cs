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

    /// <summary>Deepgram speech-to-text, used by Shoal Subtitles (its key stays there); only its spending limit is set here.</summary>
    public const string Deepgram = Common.Costs.SpendingBridgeClient.Deepgram;

    /// <summary>OpenAI speech-to-text, used by Shoal Subtitles (its key stays there); only its spending limit is set here.</summary>
    public const string OpenAiSpeech = Common.Costs.SpendingBridgeClient.OpenAiSpeech;

    /// <summary>
    /// Gets the speech-to-text providers whose spending this plugin's budget keeps for Shoal Subtitles (see the spending
    /// entry point): they have a limit here, but no key, model or test.
    /// </summary>
    public static IReadOnlyList<string> Speech { get; } = [Deepgram, OpenAiSpeech];

    /// <summary>
    /// Gets every known provider id, in the fixed order the settings keep and show them. The order isn't a preference:
    /// each request uses the one provider that can answer it.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Anthropic, OpenAi, Google, OpenAiCompatible];

    /// <summary>
    /// Whether a provider can be used in this version. The others can't be set up yet (the settings page shows them as
    /// coming later) and are left out of the spending checks; whatever was saved for them is kept.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns><c>true</c> if calls to it are made.</returns>
    public static bool IsAvailable(string? id) => string.Equals(id, Anthropic, StringComparison.Ordinal);

    /// <summary>
    /// Whether an id is a known provider.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns><c>true</c> if known.</returns>
    public static bool IsKnown(string? id) => id is not null && All.Contains(id, StringComparer.Ordinal);

    /// <summary>
    /// Whether an id is a speech-to-text provider this plugin's budget keeps (see <see cref="Speech"/>).
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns><c>true</c> if it is.</returns>
    public static bool IsSpeech(string? id) => id is not null && Speech.Contains(id, StringComparer.Ordinal);

    /// <summary>
    /// The default settings for a provider: Anthropic is allowed (it still needs a key), the others are off.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns>The settings.</returns>
    public static ProviderSettings Default(string id) => new() { Id = id, Enabled = string.Equals(id, Anthropic, StringComparison.Ordinal) };
}
