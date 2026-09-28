using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ai.Models;

/// <summary>
/// What the automatic choice picked for a provider's model family.
/// </summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Family">The family id.</param>
/// <param name="Model">The model used.</param>
/// <param name="Checked">When the provider's list was last read (or tried).</param>
/// <param name="FromList">Whether it came from the provider's list (otherwise it is the family's built-in fallback).</param>
/// <param name="Problem">Why the list couldn't be read, if it couldn't.</param>
public sealed record ResolvedModel(string Provider, string Family, string Model, DateTimeOffset Checked, bool FromList, string? Problem);

/// <summary>
/// Resolves "the current model" of a family: the newest model in it that the provider lists and that has a published
/// price (<see cref="ModelCatalog.Newest"/>). The provider's list is read at most once a day per family (hourly while it
/// can't be read), and a change of model is written to the server log. When the list can't be read, the last choice is
/// kept, or the family's built-in fallback is used.
/// </summary>
public sealed partial class ModelResolver : IDisposable
{
    /// <summary>How long a choice stands before the provider's list is read again.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);

    /// <summary>How long before trying again when the list couldn't be read.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromHours(1);

    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ResolvedModel> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelResolver"/> class.
    /// </summary>
    /// <param name="logger">The server log.</param>
    public ModelResolver(ILogger<ModelResolver> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelResolver"/> class.
    /// </summary>
    /// <param name="logger">The server log.</param>
    /// <param name="clock">Clock.</param>
    internal ModelResolver(ILogger logger, TimeProvider? clock)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The choices made so far (for the settings page).
    /// </summary>
    /// <returns>One per provider and family asked about since the server started.</returns>
    public IReadOnlyList<ResolvedModel> Current()
    {
        lock (_cache)
        {
            return [.. _cache.Values.OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.Family, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// The current model of a family, reading the provider's list when the last choice is due for a refresh.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="list">Reads the provider's model list.</param>
    /// <param name="priced">Whether a model has a published price.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The choice.</returns>
    public async Task<ResolvedModel> ResolveAsync(ModelFamily family, Func<CancellationToken, Task<IReadOnlyList<string>>> list, Func<string, bool> priced, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(priced);
        var key = family.Provider + "/" + family.Id;
        if (Fresh(key) is { } fresh)
        {
            return fresh;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(key) is { } again)
            {
                return again;
            }

            ResolvedModel? before;
            lock (_cache)
            {
                _cache.TryGetValue(key, out before);
            }

            var now = _clock.GetUtcNow();
            ResolvedModel next;
            try
            {
                var listed = await list(cancellationToken).ConfigureAwait(false);
                var newest = ModelCatalog.Newest(family, listed, priced);
                next = newest is null
                    ? new ResolvedModel(family.Provider, family.Id, before?.Model ?? family.Fallback, now, false, "The provider lists no priced model in this family.")
                    : new ResolvedModel(family.Provider, family.Id, newest, now, true, null);
            }
            catch (Exception ex) when (ex is AiException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                next = new ResolvedModel(family.Provider, family.Id, before?.Model ?? family.Fallback, now, false, ex is AiException ? ex.Message : "The model list couldn't be read (" + ex.GetType().Name + ").");
                LogListFailed(_logger, family.Provider, next.Problem, next.Model, family.Label);
            }

            if (before is null || !string.Equals(before.Model, next.Model, StringComparison.Ordinal))
            {
                LogChosen(_logger, family.Provider, family.Label, next.Model, before?.Model ?? "none");
            }

            lock (_cache)
            {
                _cache[key] = next;
            }

            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shoal AI: the {Provider} model list couldn't be read ({Problem}); using {Model} for {Family}.")]
    private static partial void LogListFailed(ILogger logger, string provider, string? problem, string model, string family);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shoal AI: the current {Provider} model for {Family} is {Model} (was {Before}).")]
    private static partial void LogChosen(ILogger logger, string provider, string family, string model, string before);

    private ResolvedModel? Fresh(string key)
    {
        lock (_cache)
        {
            return _cache.TryGetValue(key, out var r) && _clock.GetUtcNow() - r.Checked < (r.Problem is null ? RefreshEvery : RetryEvery) ? r : null;
        }
    }
}
