using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Secrets;
using Jellyfin.Plugin.Common.Storage;

namespace Jellyfin.Plugin.Ai.Calls;

/// <summary>
/// What a call is recorded with besides the call itself.
/// </summary>
/// <param name="Caller">Who asked (<c>ingest</c>, <c>subtitles</c>, <c>test</c>).</param>
/// <param name="Keep">Whether the settings ask for calls to be logged.</param>
/// <param name="Limits">The spending limits (for the display currency and extra percentage), if known.</param>
/// <param name="Rates">The latest exchange rates, if any.</param>
/// <param name="Secrets">The API keys in use, removed from any error message.</param>
internal sealed record CallContext(string Caller, bool Keep, SpendLimits? Limits, ExchangeRates? Rates, IReadOnlyList<string?> Secrets);

/// <summary>
/// A log of AI call attempts, for the settings page: one JSON object per line in the plugin's data folder
/// (<see cref="FileName"/>), readable only by the server's account. Each call is appended; the file is trimmed to the
/// last <see cref="MaxEntries"/> calls, none older than <see cref="KeptFor"/>, and under <see cref="MaxFileBytes"/>, when
/// the server starts, once a day and whenever it grows past those bounds. Trimming writes a new file and renames it over
/// the old one, so a crash never leaves a half-written log.
/// <para>
/// Privacy: never the instructions, data or schema sent (only their size), and the answer only by its shape. Error
/// messages have API keys removed and are cut to <see cref="MaxText"/> characters.
/// </para>
/// </summary>
public sealed class CallLog
{
    /// <summary>The log's file name in the plugin's data folder.</summary>
    public const string FileName = "calls.jsonl";

    /// <summary>The most calls kept.</summary>
    public const int MaxEntries = 1000;

    /// <summary>The largest the file may grow before it is trimmed (to three quarters of this).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    /// <summary>The longest error message or answer summary kept, in characters.</summary>
    public const int MaxText = 300;

    /// <summary>How long calls are kept.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    // Calls appended past MaxEntries before the file is rewritten, so it isn't rewritten on every call
    private const int Slack = 100;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    private List<CallEntry>? _entries; // oldest first
    private long _fileBytes;
    private bool _unreadable;
    private DateTimeOffset _lastTrim;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallLog"/> class.
    /// </summary>
    /// <param name="path">The log file.</param>
    public CallLog(string path)
        : this(path, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CallLog"/> class.
    /// </summary>
    /// <param name="path">The log file.</param>
    /// <param name="clock">Clock.</param>
    internal CallLog(string path, TimeProvider? clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Asks a model and records the attempt: answered, refused by the spending limits, or failed.
    /// </summary>
    /// <param name="model">The model (usually metered).</param>
    /// <param name="request">The request.</param>
    /// <param name="context">Who asked, and what the entry needs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="AiException">It couldn't be answered (whatever the model threw is passed on unchanged).</exception>
    internal async Task<AiAnswer> AskAsync(IAiModel model, AiRequest request, CallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var time = _clock.GetUtcNow();
        var start = _clock.GetTimestamp();
        AiAnswer answer;
        try
        {
            answer = await model.AskAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Record(Build(time, _clock.GetElapsedTime(start), request, model, null, ex, context), context.Keep);
            throw;
        }

        Record(Build(time, _clock.GetElapsedTime(start), request, model, answer, null, context), context.Keep);
        return answer;
    }

    /// <summary>
    /// Records an attempt that failed before a model was asked (a bad request, no usable provider or key).
    /// </summary>
    /// <param name="context">Who asked.</param>
    /// <param name="purpose">What for, if known.</param>
    /// <param name="provider">The provider, if one was chosen.</param>
    /// <param name="sentBytes">The size of the request.</param>
    /// <param name="message">Why, in words safe to show.</param>
    /// <param name="failure">The failure name.</param>
    internal void RecordProblem(CallContext context, string? purpose, string? provider, long sentBytes, string message, string failure)
    {
        ArgumentNullException.ThrowIfNull(context);
        Record(
            new CallEntry
            {
                Time = _clock.GetUtcNow(),
                Caller = CallerName(context.Caller),
                Purpose = Clean(purpose, 64),
                Provider = NullIfEmpty(Clean(provider, 40)),
                Outcome = CallEntry.Failed,
                Failure = Clean(failure, 40),
                SentBytes = sentBytes,
                Error = Shorten(Redaction.Redact(message, context.Secrets)),
            },
            context.Keep);
    }

    /// <summary>
    /// Appends a call to the log (when <paramref name="keep"/>). Never throws for file-system problems: the call itself
    /// matters more than its record.
    /// </summary>
    /// <param name="entry">The call.</param>
    /// <param name="keep">Whether the settings ask for calls to be logged.</param>
    internal void Record(CallEntry entry, bool keep = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!keep)
        {
            return;
        }

        lock (_lock)
        {
            var entries = Load();
            entries.Add(entry);
            try
            {
                var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
                if (!File.Exists(_path))
                {
                    CreateOwnerOnly(_path);
                    _fileBytes = 0;
                }

                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    stream.Write(line);
                }

                _fileBytes += line.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept in memory until the next successful write or trim
            }

            if (entries.Count > MaxEntries + Slack || _fileBytes > MaxFileBytes || _clock.GetUtcNow() - _lastTrim >= TimeSpan.FromDays(1))
            {
                TrimLocked();
            }
        }
    }

    /// <summary>
    /// The call log for the settings page.
    /// </summary>
    /// <param name="limit">The most calls to return (1 to <see cref="MaxEntries"/>).</param>
    /// <param name="caller">Only this caller's calls, or <c>null</c> or empty for all.</param>
    /// <param name="enabled">Whether calls are being logged.</param>
    /// <returns>Recent calls, newest first, and the last error.</returns>
    public CallLogView View(int limit, string? caller, bool enabled) => new(enabled, Recent(limit, caller), LastError());

    /// <summary>
    /// Recent calls, newest first.
    /// </summary>
    /// <param name="limit">The most calls to return (1 to <see cref="MaxEntries"/>).</param>
    /// <param name="caller">Only this caller's calls, or <c>null</c> or empty for all.</param>
    /// <returns>The calls.</returns>
    internal IReadOnlyList<CallEntry> Recent(int limit, string? caller)
    {
        limit = Math.Clamp(limit, 1, MaxEntries);
        lock (_lock)
        {
            IEnumerable<CallEntry> newest = Enumerable.Reverse(Load());
            if (!string.IsNullOrWhiteSpace(caller))
            {
                newest = newest.Where(e => string.Equals(e.Caller, caller.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return newest.Take(limit).ToList();
        }
    }

    /// <summary>
    /// The most recent failure or refusal, if it is newer than the last answered call.
    /// </summary>
    /// <returns>The call, or <c>null</c>.</returns>
    internal CallEntry? LastError()
    {
        lock (_lock)
        {
            var entries = Load();
            return entries.Count > 0 && entries[^1].IsError ? entries[^1] : null;
        }
    }

    /// <summary>
    /// Empties the log.
    /// </summary>
    /// <exception cref="IOException">The file couldn't be removed.</exception>
    public void Clear()
    {
        lock (_lock)
        {
            File.Delete(_path);
            _entries = [];
            _fileBytes = 0;
            _unreadable = false;
        }
    }

    /// <summary>
    /// Trims the log to its bounds (at start-up; also done once a day as calls are recorded).
    /// </summary>
    public void Trim()
    {
        lock (_lock)
        {
            Load();
            TrimLocked();
        }
    }

    /// <summary>
    /// Builds the record of a call from what happened.
    /// </summary>
    /// <param name="time">When it started.</param>
    /// <param name="duration">How long it took.</param>
    /// <param name="request">The request (only its purpose and size are kept).</param>
    /// <param name="model">The model asked.</param>
    /// <param name="answer">The answer, if answered.</param>
    /// <param name="error">What was thrown, if not.</param>
    /// <param name="context">Who asked, limits, rates and keys.</param>
    /// <returns>The record.</returns>
    internal static CallEntry Build(DateTimeOffset time, TimeSpan duration, AiRequest request, IAiModel model, AiAnswer? answer, Exception? error, CallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(context);
        var cost = (model as MeteredModel)?.LastCost;
        Money? display = null;
        if (cost is { } c && context.Limits is { } limits)
        {
            display = CostConverter.ToUserCurrency(c, limits.Currency, context.Rates, DateOnly.FromDateTime(time.ToLocalTime().DateTime), limits.ExtraPercent);
        }

        var entry = new CallEntry
        {
            Time = time,
            Caller = CallerName(context.Caller),
            Purpose = Clean(request.Purpose, 64),
            Provider = NullIfEmpty(Clean(model.Provider, 40)),
            Model = NullIfEmpty(Clean(answer?.Model ?? (error as AiException)?.ChargedModel ?? model.Model, 100)),
            SentBytes = SentBytes(request),
            Cost = cost is { } x ? decimal.Round(x.Amount, 6) : null,
            CostCurrency = cost?.Currency,
            DisplayCost = display is { } d ? decimal.Round(d.Amount, 6) : null,
            DisplayCurrency = display?.Currency,
            DurationMs = (long)Math.Max(0, duration.TotalMilliseconds),
        };

        return (answer, error) switch
        {
            ({ } a, _) => entry with
            {
                Outcome = CallEntry.Answered,
                InputTokens = a.InputTokens,
                OutputTokens = a.OutputTokens,
                Summary = Shorten(Summarise(a.Json)),
            },
            (_, AiException { RefusedByLimits: true } r) => entry with
            {
                Outcome = CallEntry.Refused,
                Failure = "spending-limit",
                Error = Shorten(Redaction.Redact(r.Message, context.Secrets)),
            },
            (_, AiException f) => entry with
            {
                Failure = Bridge.AiBridge.Name(f.Failure),
                InputTokens = f.Charged ? f.InputTokens : null,
                OutputTokens = f.Charged ? f.OutputTokens : null,
                Error = Shorten(Redaction.Redact(f.Message, context.Secrets)),
            },
            (_, OperationCanceledException) => entry with { Failure = "cancelled", Error = "The call was cancelled." },

            // Not a provider error: its message isn't known to be safe, so only its type is kept
            (_, { } other) => entry with { Failure = "transient", Error = "The AI plugin failed (" + other.GetType().Name + ")." },
            _ => entry with { Failure = "transient" },
        };
    }

    /// <summary>
    /// Describes an answer by its shape only: field names, numbers and yes/no values; text and lists only by their size.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <returns>The summary, e.g. <c>pick=2, confidence=0.9, reason=text (48 chars)</c>.</returns>
    internal static string Summarise(JsonElement json)
        => json.ValueKind == JsonValueKind.Object
            ? string.Join(", ", json.EnumerateObject().Select(p => Clean(p.Name, 40) + "=" + Shape(p.Value)))
            : Shape(json);

    /// <summary>
    /// Cuts a text to <see cref="MaxText"/> characters.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text, shortened with an ellipsis if needed.</returns>
    internal static string Shorten(string? text)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length > MaxText ? text[..MaxText] + "…" : text;
    }

    private static string Shape(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetRawText().Length <= 20 ? value.GetRawText() : "number",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.String => string.Create(CultureInfo.InvariantCulture, $"text ({value.GetString()!.Length} chars)"),
        JsonValueKind.Array => string.Create(CultureInfo.InvariantCulture, $"list ({value.GetArrayLength()} items)"),
        JsonValueKind.Object => string.Create(CultureInfo.InvariantCulture, $"object ({value.EnumerateObject().Count()} fields)"),
        _ => "nothing",
    };

    private static long SentBytes(AiRequest request)
        => Encoding.UTF8.GetByteCount(request.Instructions) + Encoding.UTF8.GetByteCount(request.Data)
            + request.Schema.Sum(p => Encoding.UTF8.GetByteCount(p.Key) + Encoding.UTF8.GetByteCount(p.Value.GetRawText()));

    /// <summary>
    /// The caller as it is logged: one of the known callers, or <c>other</c>.
    /// </summary>
    /// <param name="caller">What the request said.</param>
    /// <returns>The name.</returns>
    internal static string CallerName(string? caller) => caller switch
    {
        "ingest" or "subtitles" or "test" => caller,
        _ => "other",
    };

    // Identifiers only (purpose, provider, model, field names): letters, digits and . _ - :, shortened
    private static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var kept = new string(text.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':').Take(max).ToArray());
        return kept;
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private List<CallEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new List<CallEntry>();
        try
        {
            if (File.Exists(_path))
            {
                _fileBytes = new FileInfo(_path).Length;
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        if (JsonSerializer.Deserialize<CallEntry>(line, Json) is { } e)
                        {
                            entries.Add(e);
                        }
                    }
                    catch (JsonException)
                    {
                        // A line cut short by a crash, or edited by hand: dropped at the next trim
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable for now: never overwritten while unread; new calls are still appended
            _unreadable = true;
        }

        _entries = entries;
        return entries;
    }

    private void TrimLocked()
    {
        var entries = _entries!;
        _lastTrim = _clock.GetUtcNow();
        var cutoff = _lastTrim - KeptFor;
        var kept = entries.Where(e => e.Time >= cutoff).ToList();
        if (kept.Count > MaxEntries)
        {
            kept.RemoveRange(0, kept.Count - MaxEntries);
        }

        var lines = kept.Select(e => JsonSerializer.Serialize(e, Json) + "\n").ToList();
        var bytes = lines.Sum(l => (long)Encoding.UTF8.GetByteCount(l));
        var drop = 0;
        while (bytes > MaxFileBytes * 3 / 4 && drop < lines.Count)
        {
            bytes -= Encoding.UTF8.GetByteCount(lines[drop]);
            drop++;
        }

        kept.RemoveRange(0, drop);
        lines.RemoveRange(0, drop);
        _entries = kept;
        if (_unreadable || (kept.Count == entries.Count && bytes == _fileBytes))
        {
            return;
        }

        try
        {
            // A new owner-only file, filled and flushed, then renamed over the old one
            var next = _path + ".new";
            CreateOwnerOnly(next);
            using (var stream = new FileStream(next, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                foreach (var line in lines)
                {
                    stream.Write(Encoding.UTF8.GetBytes(line));
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(next, _path, overwrite: true);
            _fileBytes = bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tried again at the next trim
        }
    }

    // An empty file only the server's account can read: common's atomic writer sets the permissions (mode 0600, or an
    // owner-only access list on Windows) when it creates the file, and truncating keeps them
    private static void CreateOwnerOnly(string path)
    {
        JsonFile.WriteAtomic(path, string.Empty, ownerOnly: true);
        using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
    }
}
