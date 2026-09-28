using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// A model that costs nothing (an OpenAI-compatible service on this machine or the local network, or one the settings say
/// is free): its calls aren't metered or counted against the spending limits, and the call log marks them unmetered.
/// </summary>
public sealed class FreeModel : IAiModel, IDisposable
{
    private readonly IAiModel _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="FreeModel"/> class.
    /// </summary>
    /// <param name="inner">The model.</param>
    internal FreeModel(IAiModel inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public string Provider => _inner.Provider;

    /// <inheritdoc />
    public string Model => _inner.Model;

    /// <inheritdoc />
    public Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken) => _inner.AskAsync(request, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
