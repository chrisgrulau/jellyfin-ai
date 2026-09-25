# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow Jellyfin's plugin versioning.

## [Unreleased]

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
