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
| OpenAI (coming later) | Curated family map for "current recommended". |
| Google (coming later) | Gemini; curated family map. |
| OpenAI-compatible (coming later) | Any service speaking the OpenAI API: a local server (Ollama …), OpenRouter, Groq. Local addresses cost nothing. |

Only Anthropic is available in this version. The settings page shows the others as "coming later" without any fields,
sends back whatever was saved for them unchanged, and the spending checks leave them out. The providers are kept in a
fixed order (`KnownProviders.All`); it isn't an order of preference.

A named model is pinned; the settings page notes that a pinned model may cost more than the automatic choice and can be
retired by the provider.

## Spending limits (COM-02)

- **One owner per budget.** When this plugin is installed it owns the ledger for AI providers used by several plugins;
  the others ask it rather than keep their own copy. Without it, each plugin owns its own.
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
- **Prices** come from the response where the provider gives them, else the provider's usage API, else a price table
  shipped in the plugin (validated; pinned by version). If prices can't be loaded, they are unknown and paid calls stop;
  never assume zero.

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
- **API:** `GET Ai/Calls?limit=&caller=` (administrators) returns the calls newest first (in the order they finished),
  whether logging is on, and the last error: the newest call, if it wasn't answered. `DELETE Ai/Calls` empties the log
  (the page asks for a second click).

## Failures

Shared failure classes and back-off from jellyfin-plugin-common: no connection (wait and probe, alert now), transient
(short jittered retries, alert if it persists), provider limit (trust the stated reset, bounded to 31 days; otherwise
hours to days), authentication (no retry; alert), bad request (fail that request only).
