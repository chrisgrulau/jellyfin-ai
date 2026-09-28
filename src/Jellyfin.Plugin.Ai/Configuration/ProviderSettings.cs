namespace Jellyfin.Plugin.Ai.Configuration;

/// <summary>
/// How much one provider may spend.
/// </summary>
public enum ProviderBudgetMode
{
    /// <summary>No limit of its own; only the overall limit applies.</summary>
    OverallOnly = 0,

    /// <summary>A fixed monthly amount, in the chosen currency.</summary>
    Amount,

    /// <summary>A share of the overall monthly limit (only when there is one).</summary>
    PercentOfOverall,
}

/// <summary>
/// One AI provider's settings. Its API key is not here: keys are kept in a separate owner-only file and are never sent
/// back to the settings page.
/// </summary>
public class ProviderSettings
{
    /// <summary>Gets or sets the provider id (<c>anthropic</c>, <c>openai</c>, <c>google</c>, <c>openai-compatible</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the provider may be used.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the model. Empty means the current model of the chosen <see cref="Family"/>, resolved automatically
    /// (for Anthropic, Claude Opus 5.5); naming a model pins it, and a pinned model may cost more and can be retired by
    /// the provider. An OpenAI-compatible service has no automatic choice: its model must be named.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the model family the automatic choice picks the newest model from (OpenAI and Google; see
    /// <see cref="Models.ModelCatalog"/>). Empty means the provider's recommended family.
    /// </summary>
    public string Family { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an OpenAI-compatible service at a remote address doesn't charge (a free
    /// service, or one paid for another way). Its calls are then not metered, like a local service's.
    /// </summary>
    public bool Free { get; set; }

    /// <summary>
    /// Gets or sets what an OpenAI-compatible service charges per million input tokens, in <see cref="PriceCurrency"/>
    /// (0 when not set). Used to estimate and record its calls when the service doesn't say what a call cost.
    /// </summary>
    public decimal InputPrice { get; set; }

    /// <summary>Gets or sets what an OpenAI-compatible service charges per million output tokens (0 when not set).</summary>
    public decimal OutputPrice { get; set; }

    /// <summary>Gets or sets the currency of <see cref="InputPrice"/> and <see cref="OutputPrice"/>.</summary>
    public string PriceCurrency { get; set; } = "USD";

    /// <summary>Gets or sets the service address, for an OpenAI-compatible service (a local server, OpenRouter …).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Stored as entered in the XML plugin configuration; parsed and checked where it is used.")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets how this provider's spending is limited.</summary>
    public ProviderBudgetMode BudgetMode { get; set; }

    /// <summary>Gets or sets the limit: an amount per month, or a percentage of the overall limit.</summary>
    public decimal BudgetValue { get; set; }

    /// <summary>
    /// Gets or sets a prepaid credit to count down (Anthropic has no balance API): the amount bought, or 0 to not track
    /// one. What this plugin spends with the provider since <see cref="PrepaidCreditDate"/> is taken off it.
    /// </summary>
    public decimal PrepaidCredit { get; set; }

    /// <summary>Gets or sets the currency the credit was bought in (US dollars for Anthropic); set beside the credit on the settings page.</summary>
    public string PrepaidCreditCurrency { get; set; } = "USD";

    /// <summary>Gets or sets the date the credit was bought or last topped up (<c>yyyy-MM-dd</c>).</summary>
    public string PrepaidCreditDate { get; set; } = string.Empty;
}
