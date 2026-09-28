namespace Jellyfin.Plugin.Ai.Pricing;

/// <summary>
/// A model's price per million tokens, entered on the settings page for an OpenAI-compatible service (the shipped price
/// table covers only the providers' own models).
/// </summary>
/// <param name="InputPerMillion">Price per million input tokens.</param>
/// <param name="OutputPerMillion">Price per million output tokens (thinking included).</param>
/// <param name="Currency">The currency (ISO 4217).</param>
internal sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, string Currency);
