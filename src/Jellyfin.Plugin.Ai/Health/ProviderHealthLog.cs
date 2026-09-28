using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Ai.Calls;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Ai.Health;

/// <summary>
/// How an AI provider has been doing lately (see <see cref="ProviderHealthLog.Classify"/>).
/// </summary>
/// <param name="Provider">The provider id.</param>
/// <param name="Name">Its name for people.</param>
/// <param name="Systemic">Whether its failures look systemic (worth a banner) rather than transitory.</param>
/// <param name="Problem">What to tell the administrator, when systemic: what's wrong and where to fix it.</param>
/// <param name="Calls">Calls made in the last 24 hours.</param>
/// <param name="Failures">Calls that failed in the last 24 hours.</param>
/// <param name="LastSuccess">When a call last succeeded, if known.</param>
/// <param name="LastFailure">When a call last failed, if known.</param>
/// <param name="LastError">The last failure's message (keys removed).</param>
public sealed record AiProviderHealth(string Provider, string Name, bool Systemic, string? Problem, int Calls, int Failures, DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? LastError);

/// <summary>
/// Keeps each AI provider's recent record (calls and failures per hour for a day, the last success and the last refused
/// key or used-up allowance), whether or not the call log is kept, so the settings page can tell a systemic problem (a
/// banner with what to do) from transitory failures (which stay quiet: they are retried, and the call log lists them).
/// Stored in <c>health.json</c> in the plugin's data folder; never throws for file-system problems.
/// </summary>
public sealed class ProviderHealthLog
{
    /// <summary>The file's name in the plugin's data folder.</summary>
    public const string FileName = "health.json";

    /// <summary>The window the counts cover.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>Failures in the window that make a problem systemic.</summary>
    public const int FailuresForSystemic = 5;

    /// <summary>The share of calls failing in the window that makes a problem systemic …</summary>
    public const double FailingShare = 0.5;

    /// <summary>… once at least this many calls were made.</summary>
    public const int MinimumCalls = 4;

    /// <summary>Successes in a row that clear a systemic problem.</summary>
    public const int SuccessesToClear = 3;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private Dictionary<string, ProviderRecord>? _records;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderHealthLog"/> class.
    /// </summary>
    /// <param name="path">The file.</param>
    public ProviderHealthLog(string path)
        : this(path, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderHealthLog"/> class.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="clock">Clock.</param>
    internal ProviderHealthLog(string path, TimeProvider? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Counts a call to a provider from its call-log entry. Refusals by this plugin's own spending limits, cancelled calls
    /// and calls that never reached a provider (not set up) say nothing about the provider and aren't counted.
    /// </summary>
    /// <param name="entry">The call.</param>
    internal void Record(CallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!KnownProviders.IsKnown(entry.Provider) || entry.Outcome == CallEntry.Refused || entry.Failure is "cancelled" or "not-configured" or "spending-limit")
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var failed = entry.IsError;
        lock (_lock)
        {
            var records = Load();
            if (!records.TryGetValue(entry.Provider!, out var r))
            {
                records[entry.Provider!] = r = new ProviderRecord();
            }

            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            var bucket = r.Hours.FirstOrDefault(h => h.Hour == hour);
            if (bucket is null)
            {
                r.Hours.Add(bucket = new HourCount { Hour = hour });
            }

            bucket.Calls++;
            r.Hours.RemoveAll(h => now - h.Hour > Window + TimeSpan.FromHours(1));
            if (failed)
            {
                bucket.Failed++;
                r.SuccessesInARow = 0;
                r.LastFailure = now;
                r.LastFailureClass = entry.Failure;
                r.LastError = entry.Error;
                if (entry.Failure is "authentication" or "provider-limit")
                {
                    r.Refused = now;
                    r.RefusedClass = entry.Failure;
                }
            }
            else
            {
                r.SuccessesInARow++;
                r.LastSuccess = now;
            }

            Save(records);
        }
    }

    /// <summary>
    /// How each provider that has been called is doing, those with a systemic problem first.
    /// </summary>
    /// <param name="config">The settings (for provider-specific guidance, such as a local service's address).</param>
    /// <returns>One per provider.</returns>
    public IReadOnlyList<AiProviderHealth> Health(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            return [.. Load().Select(p => Classify(p.Key, p.Value, config.Providers.FirstOrDefault(s => s?.Id == p.Key), now))
                .OrderByDescending(h => h.Systemic).ThenBy(h => KnownProviders.All.ToList().IndexOf(h.Provider))];
        }
    }

    /// <summary>
    /// Forgets a provider's record (its key was replaced, so an old refusal no longer says anything).
    /// </summary>
    /// <param name="provider">The provider id.</param>
    public void Forget(string provider)
    {
        lock (_lock)
        {
            var records = Load();
            if (records.Remove(provider))
            {
                Save(records);
            }
        }
    }

    /// <summary>
    /// Classifies a provider's record. Systemic: the key was refused, or the provider's allowance or credit is used up,
    /// within the last day and with no success since; or at least <see cref="FailuresForSystemic"/> calls failed in the
    /// last day; or at least half of the calls in that time failed (with at least <see cref="MinimumCalls"/> calls).
    /// Anything else is transitory. A problem clears after <see cref="SuccessesToClear"/> successes in a row.
    /// </summary>
    /// <param name="provider">The provider id.</param>
    /// <param name="r">Its record.</param>
    /// <param name="settings">Its settings, if any.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The health.</returns>
    internal static AiProviderHealth Classify(string provider, ProviderRecord r, ProviderSettings? settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(r);
        var recent = r.Hours.Where(h => now - h.Hour <= Window).ToList();
        var calls = recent.Sum(h => h.Calls);
        var failed = recent.Sum(h => h.Failed);
        var refused = r.Refused is { } at && now - at <= Window && (r.LastSuccess is null || r.LastSuccess < at) ? r.RefusedClass : null;
        var how = string.Create(CultureInfo.InvariantCulture, $"{failed} of {calls} call{(calls == 1 ? string.Empty : "s")} since yesterday");
        var systemic = r.SuccessesInARow < SuccessesToClear
            && (refused is not null || failed >= FailuresForSystemic || (calls >= MinimumCalls && failed >= calls * FailingShare));
        var name = KnownProviders.NameOf(provider);
        return new AiProviderHealth(provider, name, systemic, systemic ? Guidance(provider, refused, r.LastFailureClass, how, settings) : null, calls, failed, r.LastSuccess, r.LastFailure, r.LastError);
    }

    // What to tell the administrator, per provider: where the key, the credit and the status page are
    private static string Guidance(string provider, string? refused, string? last, string how, ProviderSettings? settings)
    {
        var local = settings is not null && ProviderRules.IsLocal(settings);
        var address = settings?.BaseUrl?.Trim();
        if (refused == "authentication")
        {
            return provider switch
            {
                KnownProviders.Anthropic => "Anthropic refused the API key. Create a new key at console.anthropic.com and save it under Providers.",
                KnownProviders.OpenAi => "OpenAI refused the API key (or it can't use this model). Check it at platform.openai.com/api-keys and save a new one under Providers.",
                KnownProviders.Google => "Gemini refused the API key. Check it in Google AI Studio (aistudio.google.com/apikey) and save a new one under Providers.",
                _ => $"The OpenAI-compatible service{(string.IsNullOrEmpty(address) ? string.Empty : " at " + address)} refused the key. Check the key with the service and save it under Providers.",
            };
        }

        if (refused == "provider-limit")
        {
            return provider switch
            {
                KnownProviders.Anthropic => "The Anthropic account is out of credit or over its spending limit. Top it up or raise the limit at console.anthropic.com (Billing).",
                KnownProviders.OpenAi => "The OpenAI account's quota or credit is used up. Add credit or raise the limit at platform.openai.com (Billing, Limits).",
                KnownProviders.Google => "The Gemini quota is used up, or billing isn't set up for the key's project. Check the plan and billing in Google AI Studio.",
                _ => "The OpenAI-compatible service says its quota or credit is used up. Check your account with the service.",
            };
        }

        return provider switch
        {
            KnownProviders.OpenAiCompatible when local => $"The local service{(string.IsNullOrEmpty(address) ? string.Empty : " at " + address)} keeps failing ({how}). Is it running, and does it have the model named under Providers?",
            KnownProviders.OpenAiCompatible => $"The OpenAI-compatible service keeps failing ({how}). Check its address, the model name and your account with the service.",
            _ when last == "bad-request" => $"{KnownProviders.NameOf(provider)} keeps rejecting requests ({how}). The model may have been retired: clear a pinned model under Providers to use the current one.",
            KnownProviders.Anthropic => $"Anthropic keeps failing ({how}). Check this server's internet connection and status.anthropic.com.",
            KnownProviders.OpenAi => $"OpenAI keeps failing ({how}). Check this server's internet connection and status.openai.com.",
            KnownProviders.Google => $"Gemini keeps failing ({how}). Check this server's internet connection and aistudio.google.com/status.",
            _ => $"{KnownProviders.NameOf(provider)} keeps failing ({how}).",
        };
    }

    private Dictionary<string, ProviderRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }

        var read = JsonFile.Read<Dictionary<string, ProviderRecord>>(_path, Json);
        _records = read is { IsLoaded: true, Value: { } loaded }
            ? new Dictionary<string, ProviderRecord>(loaded.Where(p => KnownProviders.IsKnown(p.Key) && p.Value is not null), StringComparer.Ordinal)
            : new Dictionary<string, ProviderRecord>(StringComparer.Ordinal);
        foreach (var r in _records.Values)
        {
            r.Hours ??= [];
        }

        return _records;
    }

    private void Save(Dictionary<string, ProviderRecord> records)
    {
        try
        {
            JsonFile.WriteAtomic(_path, records, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory; written with the next call
        }
    }

    /// <summary>One provider's recent record.</summary>
    internal sealed class ProviderRecord
    {
        /// <summary>Gets or sets the calls per hour (the last day or so).</summary>
        public List<HourCount> Hours { get; set; } = [];

        /// <summary>Gets or sets how many calls in a row have succeeded.</summary>
        public int SuccessesInARow { get; set; }

        /// <summary>Gets or sets when a call last succeeded.</summary>
        public DateTimeOffset? LastSuccess { get; set; }

        /// <summary>Gets or sets when a call last failed.</summary>
        public DateTimeOffset? LastFailure { get; set; }

        /// <summary>Gets or sets the last failure's class.</summary>
        public string? LastFailureClass { get; set; }

        /// <summary>Gets or sets the last failure's message (already redacted and shortened by the call log).</summary>
        public string? LastError { get; set; }

        /// <summary>Gets or sets when the key was last refused or the allowance found used up.</summary>
        public DateTimeOffset? Refused { get; set; }

        /// <summary>Gets or sets which of the two it was (<c>authentication</c> or <c>provider-limit</c>).</summary>
        public string? RefusedClass { get; set; }
    }

    /// <summary>Calls in one hour.</summary>
    internal sealed class HourCount
    {
        /// <summary>Gets or sets the hour (UTC).</summary>
        public DateTimeOffset Hour { get; set; }

        /// <summary>Gets or sets the calls made.</summary>
        public int Calls { get; set; }

        /// <summary>Gets or sets the calls that failed.</summary>
        public int Failed { get; set; }
    }
}
