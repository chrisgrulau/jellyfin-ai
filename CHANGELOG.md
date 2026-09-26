# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

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
