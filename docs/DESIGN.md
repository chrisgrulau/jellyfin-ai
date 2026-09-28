# Design notes

Working notes for the AI plugin. Descriptive of intent; updated as the implementation lands.

## Role

The AI plugin owns **text models** for the plugin family; the Subtitles plugin owns speech-to-text. Ingest and
Subtitles use this plugin only if it is installed and the administrator has allowed them to; without it they leave
hard cases for review. Plugins never share C# types: callers find this plugin's entry point by assembly and type name
(`Jellyfin.Plugin.Ai`, `Jellyfin.Plugin.Ai.Bridge.AiBridge`, declared once in common's `AiBridgeClient` and checked by
a contract test) and use a JSON-in/JSON-out entry point with BCL types only. Each request carries a purpose tag (`ingest.match`, `subtitles.match` …).

## Providers and models

| Provider | Notes |
|---|---|
| Anthropic (default) | Claude through the official C# SDK (`Anthropic`, pinned and locked; shipped beside the plugin with `Microsoft.Extensions.AI.Abstractions`). Model left empty = Claude Opus 5.5 (`claude-opus-5-5`, US$4 / US$20 per million tokens), updated with plugin releases. Answers use structured output (a JSON schema) at low effort by default; thinking is billed as output. |
| OpenAI | `Models.OpenAiChatModel` through the official OpenAI SDK (`OpenAI` 2.14.0, MIT; shipped with `System.ClientModel` and `System.Memory.Data`). Chat Completions with a JSON-schema response format (strict when the schema is closed: every object lists all its properties as required and forbids others), `max_completion_tokens` and `reasoning_effort` (the SDK marks the property evaluation-only, OPENAI001; a model that rejects it is asked once more without). The SDK retries twice itself. |
| Google Gemini | `Models.GeminiModel`: `generateContent` over HTTPS with common's `ProviderHttp` (Google's `Google.GenAI` SDK would add `Google.Apis.Auth`, its dependencies and a second `Microsoft.Extensions.AI.Abstractions` for one request type). The key goes in `x-goog-api-key`, never the URL. `responseMimeType: application/json` with `responseJsonSchema`; `thinkingConfig.thinkingLevel` (`low`, or `high` for medium and high effort) for Gemini 3 and later. Output tokens = candidates + thoughts. Transient failures with a short wait are tried again twice here. |
| OpenAI-compatible | The same `OpenAiChatModel` pointed at the service's address: an optional key (without one, no `Authorization` header is sent), a model that must be named, no reasoning effort. A service that rejects `json_schema` is asked once more in JSON mode with the schema in the instructions, and an answer wrapped in a Markdown code fence is unwrapped. |

**Which provider answers** (`ProviderRules.Order`): `PluginConfiguration.DefaultProvider` (Anthropic by default), then
`FallbackProviders` (up to three on the page), each once and only if switched on; if the default is off and no fallback
is on, the first provider switched on answers (the page warns). The bridge (`AiBridge.AnswerAsync`) tries them in turn:
a provider that can't be built (no key, no address, no prices) or whose call fails without being charged, other than a
rejected request (`FallsBack`), passes the request on; every attempt is in the call log. A charged failure (a refusal,
cut off, unreadable) isn't sent elsewhere, so it isn't paid for twice. The reply is the first answer, or the last
failure. The bridge contract is unchanged (version 1).

**The current model** (`ModelCatalog`, `ModelResolver`). OpenAI and Gemini have curated families: OpenAI `sol`
(balanced, recommended), `astra` (most capable), `luna` (cheapest), matching `gpt-<version>-<tier>`; Gemini `flash`
(recommended), `pro`, `flash-lite`, matching `gemini-<version>-<tier>[-preview…]`. Only exact aliases match (no dated
snapshots or audio/realtime variants). The automatic choice is the highest version among the provider's listed models
that have a published price, preferring stable models (a preview only when the family has no stable one). The list is
read at most once a day per family, hourly while it can't be read; a change is logged (`Shoal AI: the current openai
model for … is gpt-6-sol (was …)`). Without a list, the last choice or the family's built-in fallback
(`gpt-6-sol`, `gemini-3.8-flash` …, each priced in the shipped table) is used. A pinned model (`ProviderSettings.Model`)
always wins. Claude keeps its fixed default for now.

**Unmetered services.** An OpenAI-compatible service whose address is local (common's `NetworkAddress.IsLocal`: loopback,
private, link-local and unique-local addresses, `localhost`, `.local`), or that is marked **free**, is wrapped in
`FreeModel`: no reservation, no ledger entry, no limit, and the call log marks it free. Its limit row is ignored.

**Addresses.** A compatible service's address must be `http(s)` with no user name, password, query or fragment, and
plain `http://` only for a local address (`ProviderRules.AddressProblem`), so a key never crosses the internet in plain
text. Checked when saving (an error) and before each call.

## Provider health

`Health.ProviderHealthLog` (`health.json`) keeps, per provider, calls and failures per hour for a day, successes in a
row, the last success and failure, and when the key was last refused or the allowance found used up. It is fed from
each call-log entry (also when the call log is off); refusals by this plugin's own limits, cancelled calls and calls
that never reached a provider aren't counted. A problem is **systemic** when the key was refused or the allowance used
up within the day with no success since, or 5 calls failed in the day, or half of at least 4; three successes in a row
clear it, and so does saving or clearing the key. `GET Ai/Health` returns the systemic problems of providers that are
switched on (a banner at the top of the settings page) and every provider's record. The banner's text is per provider:
where to replace the key or top up (console.anthropic.com, platform.openai.com, Google AI Studio), the provider's status
page, whether a pinned model may have been retired, or whether a local service is running at its address. Transient
failures stay out of it.

## Spending limits (COM-02)

- **One budget page.** When this plugin is installed it owns the family's budget: the currency, the overall limit and a
  limit per provider for every paid service, Claude and Shoal Subtitles' paid speech-to-text (Deepgram and OpenAI,
  named `deepgram` and `openai-speech` here, apart from OpenAI's text models). Subtitles meters those calls on this
  plugin's ledger through the spending entry point (below) and hides its own spending settings; its keys stay with it.
  Without this plugin, or with **Allow Subtitles to use this budget for paid speech-to-text** off, Subtitles uses its
  own ledger and settings, as before.
- **Overall limit** for all paid providers together, per month, in the user's currency: 5 by default; 0 means no paid
  use; "no limit" is an explicit choice with a warning.
- **Per-provider limits** (optional), each either an amount or a percentage of the overall limit, mixed freely across
  providers; percentages need an overall limit. A provider stops at whichever limit it reaches first.
- **Warnings** (checked on the server before saving): only an overall limit with several paid providers ("one provider
  running over could use it all"); per-provider limits adding up to more than the overall limit; no limit anywhere.
- **Money** is `decimal`, kept in the currency it was charged in and converted with the ECB's daily rates for display
  and checks (see jellyfin-plugin-common's *Currencies* notes). Unknown or stale rates mean an unknown cost: paid calls
  in other currencies stop rather than count as free. An optional percentage covers taxes or card fees.
- **Reserve, then settle.** The estimated cost of each call is reserved before it is made, atomically across concurrent
  requests, and the actual cost settled afterwards (common's `MeteredCall`). A reply that was billed but can't be used
  (a refusal, cut off, invalid JSON) is settled at the tokens it used; a provider error or cancellation releases the
  reservation; any other failure is settled at the estimate, since it may have been billed. The ledger, rates and
  prices live in common's `SpendingStore`, which also refreshes the rates when due and gives the settings page its
  currency list (in the `Ai/Spending` reply). The ledger is persisted atomically.
- **Prices** come from the response where the provider gives them (OpenRouter's `usage.cost`), else the tokens used ×
  a price table shipped in the plugin (validated; pinned by version, 2026-09-28: Claude, OpenAI's GPT-6 to GPT-5 and
  Gemini 3.8 to 2.5), or for a remote OpenAI-compatible service the prices entered on the settings page (the table
  doesn't know its models). A reply without token counts is settled at its estimate. If prices can't be loaded, they
  are unknown and paid calls stop; never assume zero. The dated snapshot a provider names in its reply
  (`gpt-6-sol-2026-07-01`) is priced as the model asked for.

## Spending entry point

`Jellyfin.Plugin.Ai.Bridge.SpendingBridge.HandleAsync(string, CancellationToken)`, found by the callers through
common's `SpendingBridgeClient` (which declares the contract, version 1; see common's *Spending entry point* notes). It
forwards to the shared `AiSpending` attached at start-up by `AiBridgeHost`, like `AiBridge`.

- **Operations:** `reserve` (purpose, provider, estimate as `{amount, currency}`) returns a `reservationId`, or
  `provider-limit` with the ledger's reason; `settle` (reservation, actual) and `release` (reservation); `carry`
  (Subtitles' own spending this month with one provider in one currency, replacing what it reported before, so the month
  counts it once); `summary` (currency, overall limit, this month's spending overall and per provider, each provider's
  limit, the rates' date and freshness), which the Subtitles page shows instead of its own settings.
- **Prices:** the caller prices its calls with its own price table (this plugin has no speech prices); the estimate and
  the actual cost are converted here with this plugin's exchange rates, refreshed when due before each reservation, and
  checked against the overall limit and the provider's own limit. The call log isn't used for speech: the ledger is
  enough.
- **Checks:** the version (`unsupported-version`); the caller, only `subtitles`, while
  `PluginConfiguration.AllowSubtitlesSpending` is on (on by default, since only amounts are exchanged: `not-allowed`
  otherwise, and Subtitles then uses its own budget); a speech-to-text provider (`KnownProviders.Speech`); a purpose
  that is a short identifier (`subtitles.sync`, or `ingest.episode` when Subtitles transcribes for Ingest); amounts 0
  to 100,000 in a supported currency; a carry for the current month only. The plugin's **Enabled** switch doesn't
  apply: it is about answering AI requests, and the budget is kept either way.
- **Owned reservations:** each reservation records its caller, who alone can settle or release it. One left open for an
  hour is settled at its estimate (the caller stopped mid-call); a late settle or release is refused (`bad-request`)
  and changes nothing.
- **Settings page:** the speech providers are listed as limit rows (spending this month, amount or share of the overall
  limit) while Subtitles is installed, or once they have spending or a limit (`SpendingSummary.SpeechProviders`). They
  have no key, model or test here. They are kept in `Providers` after the AI providers; `BudgetRules` checks that a
  percentage has an overall limit and adds their limits up with the others, but doesn't warn that they are unlimited.

## Safety (AI-01)

- **Model output is untrusted data**: validated against the options offered (the answer must be one of the candidate
  indexes); never used as a path, id or file name.
- **Prompt injection**: release names, NFO text, subtitles and transcripts are attacker-controllable, so they go only in
  delimited data fields, never in instructions; outputs are constrained with JSON schemas.
- **Send as little as possible**: titles, years, episode codes, short excerpts. Never absolute paths or Jellyfin user
  names. Home videos and photos are excluded. What is sent, and to whom, is listed on the settings page and in
  SECURITY.md. Each calling plugin is opt-in.
- **Keys**: an owner-only file separate from the configuration; write-only through the settings API; never in URLs,
  logs, alerts or exports; provider error bodies redacted before logging.
- **Cross-plugin entry point**: in-process, versioned JSON contract, size limits; no public, unauthenticated HTTP
  endpoint. Implemented as `Jellyfin.Plugin.Ai.Bridge.AiBridge.AskAsync(string, CancellationToken)`, found by the
  callers (through jellyfin-plugin-common) by assembly and type name. Version 1 request: `version`, `caller` (`ingest` or
  `subtitles`), `purpose` (must start with the caller), `instructions`, `data`, `schema`, `maxOutputTokens` (256–16000),
  `effort` (`low`/`medium`/`high`); reply: `ok` with `answer` and `model`, or `error` with a `failure` class.

## Call log (FEAT-04)

Every attempt through the entry point or the **Test** button is recorded by `Calls.CallLog`, unless **Keep a log of AI
calls** is off. Requests refused because a plugin is switched off or not allowed aren't: that is the administrator's
choice and nothing was sent.

- **Kept:** time (UTC), caller (`ingest`, `subtitles`, `test`, anything else as `other`), purpose, provider, model,
  outcome (`answered`, `refused` by the spending limits, or `failed` with its failure class: the bridge's names plus
  `spending-limit` and `cancelled`), tokens billed (also for a billed failure), the cost recorded on the ledger in the
  provider's currency and converted to the settings' currency (with the extra percentage) when rates are current,
  duration, the size in UTF-8 bytes of the instructions, data and schema, and either the answer's shape (field names,
  numbers and booleans; strings and lists by length only) or the error message.
- **Never kept:** the instructions, data or schema, the answer's text, or keys. Error messages go through common's
  `Redaction` with the keys in use and are cut to 300 characters; an unexpected exception keeps only its type name.
  Purposes, providers, models and field names keep identifier characters only.
- **Storage:** JSON Lines (`calls.jsonl`) in the plugin's data folder. Each call is appended; the file is created
  owner-only through common's `JsonFile.WriteAtomic(ownerOnly: true)` and then truncated, which keeps its permissions.
  It is trimmed to the last 1,000 calls, none older than 30 days, and three quarters of 1 MB at start-up, once a day,
  after 100 calls past the limit, or past 1 MB: a new owner-only file is written, flushed and renamed over the old one.
  A damaged line (a crash mid-append) is skipped when read and dropped at the next trim. A file that can't be read is
  never overwritten. Recording never fails a call.
- **API:** `GET Ai/Calls?limit=&before=&caller=` (administrators) returns a page of calls (15 by default) newest first
  (in the order they finished), whether logging is on, the last error (the newest call, if it wasn't answered), and
  `Next`, the cursor to pass as `before` for the next page. The cursor is each entry's place in the log, numbered in
  memory as it is read or recorded (never saved), so calls recorded while the list is open don't shift the pages; after
  a restart the page simply starts again from the newest.
- **Presentation:** `Calls.CallPresenter` words each call on the server, so it can be tested: a headline from the
  caller, purpose and outcome ("Subtitles checked wording — refused: monthly limit reached"; unknown purposes read as
  "asked for help"), an outcome icon and label (✅ / ⛔ / ⚠, "Failed (charged)" for a billed failure), compact cost (in
  the settings' currency when converted) and duration, and the technical details as terms and values. The page adds
  only the time relative to now (the viewer's clock and time zone), with the exact local time on hover. `DELETE Ai/Calls` empties the log
  (the page asks for a second click).

## Failures

Shared failure classes and back-off from jellyfin-plugin-common: no connection (wait and probe, alert now), transient
(short jittered retries, alert if it persists), provider limit (trust the stated reset, bounded to 31 days; otherwise
hours to days), authentication (no retry; alert), bad request (fail that request only).

Every provider's HTTP failures go through common's `HttpFailure.Classify` and `RetryAfter`, then
`Models.ProviderErrors` words them alike ("OpenAI's quota or credit is used up (it says to try again in about 2
hours)"); the wait is kept on `AiException.RetryAfter`. Provider-specific readings on top: Gemini's bad key is a 400
`API_KEY_INVALID` (authentication), `FAILED_PRECONDITION` (billing not set up, region) is a provider limit, and a 429
with a `retryDelay` of a minute or less that doesn't mention a daily allowance or billing is a short rate limit
(transient), not a used-up quota. Key-shaped text is redacted from every message before it is kept.
