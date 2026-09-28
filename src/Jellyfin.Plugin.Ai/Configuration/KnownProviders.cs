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

    /// <summary>OpenAI (GPT), through the official OpenAI SDK.</summary>
    public const string OpenAi = "openai";

    /// <summary>Google (Gemini), through the Gemini API.</summary>
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
    /// which provider answers is <see cref="PluginConfiguration.DefaultProvider"/>, then
    /// <see cref="PluginConfiguration.FallbackProviders"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Anthropic, OpenAi, Google, OpenAiCompatible];

    /// <summary>
    /// Whether a provider can be used in this version: every known AI provider since 0.6 (a provider that isn't ready yet
    /// would be left out of the spending checks, with whatever was saved for it kept).
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns><c>true</c> if calls to it are made.</returns>
    public static bool IsAvailable(string? id) => IsKnown(id);

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
    /// A provider's name for people.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns>E.g. <c>Anthropic</c>, <c>OpenAI-compatible service</c>.</returns>
    public static string NameOf(string? id) => id switch
    {
        Anthropic => "Anthropic",
        OpenAi => "OpenAI",
        Google => "Google Gemini",
        OpenAiCompatible => "OpenAI-compatible service",
        Deepgram => "Deepgram speech-to-text",
        OpenAiSpeech => "OpenAI speech-to-text",
        null or "" => "Unknown provider",
        _ => id,
    };

    /// <summary>
    /// The default settings for a provider: Anthropic is allowed (it still needs a key), the others are off.
    /// </summary>
    /// <param name="id">Provider id.</param>
    /// <returns>The settings.</returns>
    public static ProviderSettings Default(string id) => new() { Id = id, Enabled = string.Equals(id, Anthropic, StringComparison.Ordinal) };
}
