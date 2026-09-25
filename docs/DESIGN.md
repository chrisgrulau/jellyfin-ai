# Design notes

Working notes for the AI plugin. Descriptive of intent; updated as the implementation lands.

## Role

The AI plugin owns **text models** for the plugin family; the Subtitles plugin owns speech-to-text. Ingest and
Subtitles use this plugin only if it is installed and the administrator has allowed them to; without it they leave
hard cases for review. Plugins never share C# types: callers find this plugin by its id and use a JSON-in/JSON-out
entry point with BCL types only. Each request carries a purpose tag (`ingest.match`, `subtitles.match` …).

## Providers and models

| Provider | Notes |
|---|---|
| Anthropic (default) | Claude through the official C# SDK. Model left empty = the newest Sonnet, resolved automatically (refreshed daily, logged). |
| OpenAI | Curated family map for "current recommended". |
| Google | Gemini; curated family map. |
| OpenAI-compatible | Any service speaking the OpenAI API: a local server (Ollama …), OpenRouter, Groq. Local addresses cost nothing. |

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
  requests, and the actual cost settled afterwards. The ledger is persisted atomically.
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
- **Cross-plugin entry point**: in-process, identified by plugin id, versioned JSON contract, size limits; no public,
  unauthenticated HTTP endpoint.

## Failures

Shared failure classes and back-off from jellyfin-plugin-common: no connection (wait and probe, alert now), transient
(short jittered retries, alert if it persists), provider limit (trust the stated reset, bounded to 31 days; otherwise
hours to days), authentication (no retry; alert), bad request (fail that request only).
