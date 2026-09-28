# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

### Added

- **OpenAI, Google Gemini and OpenAI-compatible providers.** Besides Anthropic (Claude), the plugin can now use:
  - **OpenAI** (GPT), through the official OpenAI SDK (`OpenAI` 2.14.0, MIT; shipped beside the plugin with
    `System.ClientModel.dll` and `System.Memory.Data.dll`).
  - **Google Gemini**, through the Gemini API with the family's shared provider HTTP code (Google's .NET SDK would bring
    several more assemblies for one request type). The key travels in the `x-goog-api-key` header.
  - **Any OpenAI-compatible service** (Ollama or another local server, OpenRouter, Groq …): an address, an optional key
    and a model name. On this machine or the local network it costs nothing and isn't metered; a remote one is metered
    at the prices entered under Advanced (or not at all when marked free), and a cost the service reports itself
    (OpenRouter's `usage.cost`) is recorded instead. Plain `http://` is only accepted for local addresses. A service
    without JSON-schema support is asked again in JSON mode.
- **The current model, automatically.** OpenAI and Gemini use the newest model of the kind you choose (OpenAI:
  balanced `sol`, most capable `astra`, cheapest `luna`; Gemini: Flash, Pro, Flash-Lite), from the provider's own model
  list, checked once a day (hourly while the list can't be read) and only among models with a published price. A change
  is written to the server log. A pinned model (under Advanced) still wins.
- **Which provider answers:** *Answer requests with* (Anthropic by default) and up to three fallbacks. The next one is
  tried when a provider isn't set up, can't be reached, refuses the key, is out of credit or over a spending limit, or
  is failing for now; a rejected request, or one that was answered and charged but couldn't be used, isn't sent again.
  The entry point's contract is unchanged (version 1).
- **Provider health and a banner.** Every provider's recent record (calls and failures per hour for a day, last
  success, last refused key or used-up credit) is kept in `health.json`, whether or not the call log is. A refused key,
  used-up credit or repeated failures (5 in a day, or half of at least 4 calls) show a banner at the top of the settings
  page with that provider's guidance (where to fix the key or credit, its status page, or whether a local service is
  running); one-off failures stay quiet. Three successes in a row, a new key or a day's quiet clear it. `GET Ai/Health`.
- **Prices** for OpenAI's GPT-6, GPT-5.6, GPT-5.5, GPT-5.4 and GPT-5 models and Gemini 3.8 Flash, 3.1 Pro (preview),
  3.5 and 3.1 Flash-Lite and 2.5 models (price table version 2026-09-28; Gemini 3.8 Flash's price is introductory until
  the end of 2026).
- **Test** shows the model's reply and what the test cost (or that it was free), and tests what the page shows, saved
  or not. **List available models** (under Advanced) fills the model field's suggestions. `GET Ai/Families`,
  `GET Ai/Models/{provider}`.

### Changed

- The settings page lists every provider with the everyday settings first (the kind of model, or a compatible
  service's address and model) and the rest under **Advanced** (a pinned model, prepaid credit, a compatible service's
  prices). "Coming later" is gone.
- Failures read the same for every provider: no connection, a temporary problem (tried again later), a used-up quota or
  credit (with when it resets, if the provider said), a refused key, or a rejected request. Gemini's short per-minute
  rate limits are waited out and tried again (twice at most) instead of being read as a used-up allowance.
- The call log marks calls to local or free services as free, and a used-up quota reads as such rather than as a rate
  limit.

### Fixed

- A metered model now disposes the provider client it wraps.

## [0.5.3-alpha] - 2026-09-27

### Changed

- **Buttons show progress while they work.** Save, Save key, Clear, Test, Refresh, Clear log and Show more are
  disabled while they run, with a small spinner and a label such as "Saving…" or "Testing…" (a still "…" when reduced
  motion is set), and the outcome is shown next to them afterwards: a success for a few seconds, a problem until the
  next try. Screen readers hear both.

## [0.5.2-alpha] - 2026-09-27

### Fixed

- The settings page always honours the `hidden` attribute, even on elements whose own style sets how they are laid
  out (a safeguard; nothing on the page is known to have been affected).

## [0.5.1-alpha] - 2026-09-27

### Changed

- **Settings pages use the full width; buttons are centred.** The settings sections, provider rows, limits and the
  recent-calls list now use the whole page area instead of stopping at about half a wide screen (help text keeps a
  comfortable reading width). *Save* and *Show more* are centred at a sensible width instead of stretching across the
  page; buttons inside rows (*Save key*, *Test*, *Refresh*) stay compact.

## [0.5.0-alpha] - 2026-09-27

### Added

- **One budget page:** the currency, the overall monthly limit and a limit per provider for every paid service in the
  family are set here, including Shoal Subtitles' paid speech-to-text (Deepgram and OpenAI). Subtitles reserves and
  records those calls on this plugin's ledger through a new in-process spending entry point
  (`Bridge.SpendingBridge.HandleAsync`, contract version 1 in common's `SpendingBridgeClient`: reserve, settle,
  release, carry, summary), and reports what it had already spent this month once, so the month counts it. Its keys
  stay in Subtitles; only amounts are exchanged.
  - **Allow Subtitles to use this budget for paid speech-to-text** (`AllowSubtitlesSpending`), on by default. Off, or
    without this plugin, Subtitles uses its own currency and limit as before.
  - The Spending section lists Deepgram and OpenAI speech-to-text as limit rows while Subtitles is installed (or once
    they have spending or a limit); this month's total includes them. `GET Ai/Spending` adds `SpeechProviders`.
  - A reservation made through the entry point that is left open for an hour is settled at its estimate.

### Changed

- **UI:** `GET Ai/Calls` returns a page of calls (15 by default) with a `Next` cursor to pass as `before` for the next
  page; calls recorded while the list is open don't shift the pages, and the caller filter applies across them. Each
  call comes presented for the settings page: a headline ("Ingest asked which film or show this is — answered"), an
  outcome icon and label, compact cost and duration, and its technical details (model, tokens, bytes sent, failure
  class, answer shape, cost in the provider's currency) as a list of terms.
- **UI:** a less cluttered settings page. Settings are grouped into sections that fold away (what may use AI,
  providers and keys, spending and recent calls open; the model and prepaid credit, limits per provider, and taxes and
  fees folded), with the longer explanations behind small "?" toggles. Providers that are coming later share one line.
  Spending shows this month against the limit with a bar, and each provider's limit row shows its spending (a list
  ready to grow with more providers). A per-provider value is only asked for when its limit needs one.
- **UI:** recent calls are listed 15 at a time with **Show more**: one line each with an outcome icon, a headline, the
  time relative to now (exact time on hover, no seconds), cost and duration; the model, tokens, bytes sent, failure
  class, answer shape and costs open under **▸**. Works on narrow screens.

## [0.4.0-alpha] - 2026-09-27

### Added

- **FEAT-04:** a log of AI calls on the settings page: time, plugin, purpose, model, outcome (answered, refused by the
  spending limits, or failed and why), tokens, cost in the provider's currency and yours, and duration, filterable by
  plugin, with the last error shown while nothing has succeeded since. Never what was sent (only its size) or the
  answer's text; error messages have keys removed and are cut to 300 characters. Kept in `calls.jsonl` in the plugin's
  data folder, readable only by Jellyfin, for the last 1,000 calls or 30 days. **Keep a log of AI calls** (on by
  default) and a two-click **Clear log**; `GET`/`DELETE Ai/Calls`.

## [0.3.0-alpha] - 2026-09-26

### Changed

- **FAM-06:** paid calls run through common's shared metered call, so AI and Subtitles reserve, settle and release in
  the same way. A call that fails unexpectedly (not a provider error, not a cancellation) is now recorded at its
  estimate, since it may have been billed; before, its reservation was left open and counted at the estimate anyway,
  until the month ended.
- **FAM-06:** spending (the ledger, exchange rates and prices) is kept by common's shared spending store. Exchange
  rates for another plugin's request are refreshed about once a day as before, but while they are missing or stale a
  failed refresh is retried at most every 30 minutes rather than on every request. The Test button still refreshes
  them directly.
- **FAM-06:** a currency setting that isn't supported is read as USD everywhere; before, the spending limits accepted
  any three letters. The settings page takes its currency list from the server (`Ai/Spending` now returns it), keeping
  its own copy only as a fallback.

## [0.2.0-alpha] - 2026-09-26

### Fixed

- **FAM-02:** text in any script is passed to the model as it is, not as `\uXXXX` escapes. The data limit (64 KB) is
  measured in UTF-8 bytes, and a request that's too large is refused as a bad request, with its size.
- **AI-02:** exchange rates are refreshed (at most once a day) when another plugin asks. Before, only the Test button
  refreshed them, so in any currency other than USD, AI help stopped about a week after the last Test.
- **AI-03:** a request field of the wrong type is answered as a bad request instead of throwing. String data is always
  JSON-encoded, so it can't close the model's data block. Anything unexpected is answered as transient, never thrown.
- **FAM-03 (server side):** refusals say what the caller should do:
  - `off`: switched off, or the plugin isn't allowed on the settings page;
  - `not-configured`: no usable provider or key;
  - `unsupported-version`;
  - `bad-request`.

  Before, all of these were `not-allowed`.
- **AI-04:** a call that Claude answered and billed, but whose answer couldn't be used (a refusal, an answer cut off at
  the output limit, invalid JSON), is recorded in the spending ledger at the tokens it actually used. Before, its
  reservation was released, so the ledger under-counted. "Cut off" is recognised by the reply's stop reason, not by
  the wording of the message.
- **FAM-04:** the entry point's version and data limit now come from common's `AiBridgeClient` (the shared code is
  updated to its current version), so the two sides can't drift. A contract test runs the real client against this
  plugin: its finder locates the entry point, the entry point accepts the client's own request, and the client reads
  the entry point's replies. DESIGN now says callers find the plugin by assembly and type name, not by its id.
- **FAM-08:** OpenAI, Google and OpenAI-compatible providers are shown as "coming later", with no fields to fill in,
  since this version can't use them. Anything saved for them earlier is kept as it was, and it no longer produces
  spending warnings or blocks saving. The docs no longer call the provider list an order of preference (it's a fixed
  order). The currency a prepaid credit was bought in can now be set on the page; before, it could only stay USD.
- **FAM-07 (settings page):** saving or clearing a key shows the server's own reason when it fails (before, every
  failure, including a server error, said "That doesn't look like an API key."). Status messages, test results and
  spending warnings are announced to screen readers, labels are linked to their inputs, and the "set" marker and
  warnings no longer rely on colours that are hard to read in the light theme.
- **AI-05, DOC-03:** the docs now match the default model (Claude Opus 5.5) and the entry point that exists. The
  README lists what each plugin sends.

## [0.1.0-alpha] - 2026-09-26

### Added

- **Entry point for the other plugins** (`AiBridge.AskAsync`). Ingest and Subtitles call it in the same server
  process with versioned JSON, so the plugins never share C# types, and there is no HTTP endpoint for it.
  - Each request is checked: the version, its size, that the calling plugin is allowed on this plugin's settings page,
    and that its purpose belongs to it.
  - Spending is metered like any other call.
  - Replies say what failed (not allowed, authentication, provider limit, transient, bad request, no connection), so
    callers can fall back to review.
- **Claude calls.** The plugin can now ask Claude (Anthropic), through the official Anthropic SDK. It uses Claude Opus
  5.5 unless you name another model.
  - Answers are constrained to a JSON schema.
  - Instructions and data are kept apart, and the data is marked as untrusted, so text in file names can't steer it.
  - Low effort is the default, to keep decisions cheap.
- **Spending.** Every call reserves its most possible cost (input plus the whole output allowance) against the monthly
  limits before it is made, and records the actual cost from the reply's token counts; a failed call isn't counted.
  - Prices ship with the plugin, dated 2026-09-26, for Claude Opus 5.5, Opus 5, Sonnet 5 and Haiku 4.5.
  - Unknown prices or exchange rates mean no call.
  - The settings page shows this month's spending.
- **Prepaid credit.** Anthropic has no balance API, so each paid provider can count down a prepaid credit instead. You
  enter the amount and the date it was bought or topped up. The settings page shows about what's left: the credit less
  what this plugin has spent with that provider since. Use of the same key elsewhere isn't seen.
- **Test** on the settings page sends one tiny request (a fraction of a cent) and shows the model, the time taken, the
  tokens used and the cost.

### Changed

- Shared code updated (COM-03, COM-04, COM-05). Whether an OpenAI-compatible service is local (and so free, outside
  every budget) now uses the one shared definition. That definition also counts link-local addresses (169.254.x.x) and
  bracketed IPv6. The settings page now says that a local address counts as free, and that a local relay to a paid
  service needs its own limit.

## 0.0.1 - scaffolding (not released)

### Added

- Repository scaffolding: README, design notes, security and contribution policies, CI with locked restores, pinned
  SDK, release job (SHA256SUMS, build-provenance attestation, draft then publish), Dependabot (NuGet, Actions, SDK and
  the common submodule), CODEOWNERS.
- Plugin skeleton targeting Jellyfin 12.1 / net10.0, compiling in jellyfin-plugin-common.
- Settings page: what may use AI (Ingest, Subtitles; off until allowed), providers (Anthropic by default; OpenAI,
  Google, OpenAI-compatible) with automatic or pinned models, currency, overall monthly limit (0 = no paid use; no limit
  only by explicit choice), and under Advanced settings a limit per provider (amount or share of the overall limit) and
  a percentage for taxes or card fees. Limits are checked on the server before saving, with the agreed warnings.
- API keys stored in an owner-only file, separate from the configuration; the settings API can set, replace and clear
  them and report whether one is set, never return them.
- `global.json` accepts any .NET 10 SDK (10.0.100 and later), so the SDKs shipped by Linux distributions (10.0.1xx)
  build it; CI uses the newest .NET 10 SDK, and Dependabot no longer raises the minimum. Package versions stay locked.
- The plugin family is now called **Shoal**: this plugin shows as "Shoal AI". Settings, data and the plugin id are
  unchanged.
