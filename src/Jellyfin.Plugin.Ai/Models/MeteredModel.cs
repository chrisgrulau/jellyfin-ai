using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// A paid model kept within the spending limits: each call's most it could cost (input plus the whole output
/// allowance) is reserved before it is made, and what it actually cost (from the reply's token counts) is recorded
/// afterwards, also when a billed reply turns out unusable; a call that was never answered is released, and one that
/// failed unexpectedly is recorded at the estimate (it may have been billed). Unknown prices or exchange rates, or going
/// over a limit, mean no call.
/// </summary>
public sealed class MeteredModel : IAiModel
{
    private readonly IAiModel _inner;
    private readonly AiSpending _spending;
    private readonly SpendLimits _limits;

    /// <summary>
    /// Initializes a new instance of the <see cref="MeteredModel"/> class.
    /// </summary>
    /// <param name="inner">The paid model.</param>
    /// <param name="spending">Prices, ledger and rates.</param>
    /// <param name="limits">The limits.</param>
    internal MeteredModel(IAiModel inner, AiSpending spending, SpendLimits limits)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _spending = spending ?? throw new ArgumentNullException(nameof(spending));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    /// <inheritdoc />
    public string Provider => _inner.Provider;

    /// <inheritdoc />
    public string Model => _inner.Model;

    /// <summary>Gets what the last call cost, as charged.</summary>
    internal Money? LastCost { get; private set; }

    /// <inheritdoc />
    public async Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_spending.Estimate(Provider, Model, request.Instructions.Length + request.Data.Length + 200, request.MaxOutputTokens) is not { } estimate)
        {
            throw new AiException("There's no published price for " + Model + ", so it isn't used.") { Failure = FailureClass.BadRequest };
        }

        // The shared metered call reserves, runs, then settles or releases (FAM-06). AiException is public, so it can't
        // derive from common's internal ProviderException: its billed failures are recognised here instead.
        var options = new MeteredCallOptions
        {
            // Answered and billed, but unusable (a refusal, cut off, unreadable): recorded at what it used (AI-04)
            IsCharged = static ex => ex is AiException { Charged: true },
            ChargedCost = ex => ex is AiException a ? CostOf(a.ChargedModel, a.InputTokens, a.OutputTokens) : null,

            // Refused or never answered: not charged. Anything else may have been billed, so it counts at the estimate
            IsUncharged = static ex => ex is AiException or OperationCanceledException,
            Refuse = static why => new AiException(why) { Failure = FailureClass.ProviderLimit },
            Recorded = cost => LastCost = cost,
        };

        return await MeteredCall.RunAsync(
            _spending.Ledger,
            _limits,
            _spending.Rates.Current,
            Provider,
            request.Purpose,
            estimate,
            ct => _inner.AskAsync(request, ct),
            answer => CostOf(answer.Model, answer.InputTokens, answer.OutputTokens),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    // A billed call's actual cost: the answering model's price, else the requested model's (else the estimate, in MeteredCall)
    private Money? CostOf(string? model, long inputTokens, long outputTokens)
        => (model is null ? null : _spending.Cost(Provider, model, inputTokens, outputTokens))
            ?? _spending.Cost(Provider, Model, inputTokens, outputTokens);
}
