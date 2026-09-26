# Shoal AI

<p align="center"><img src="assets/shoal-ai.png" alt="Shoal AI icon" width="480"></p>

Part of **Shoal**, a family of Jellyfin plugins that work together: [Shoal
Ingest](https://github.com/chrisgrulau/jellyfin-ingest) files new media into your libraries, [Shoal
Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles) finds, checks and synchronises subtitles, and [Shoal
AI](https://github.com/chrisgrulau/jellyfin-ai) gives both optional AI help. Each works on its own; installed together,
they help each other.

A [Jellyfin](https://jellyfin.org) plugin that gives the other plugins in the family
([Ingest](https://github.com/chrisgrulau/jellyfin-ingest), [Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles))
optional, budget-controlled access to AI models, for decisions they can't make on their own: which of two close
candidate titles a release is, or whether a subtitle really matches the dialogue.

> **Status:** early development. The settings page (providers, keys, spending limits) and Claude calls, kept within
> the spending limits, are in place; the entry point the other plugins use is next.

## How it fits together

- **Optional everywhere.** Ingest and Subtitles work fully without this plugin. When it is installed, and you allow
  them to, they ask it for a tiebreak; otherwise they leave hard cases for your review, as before.
- **Providers.** Anthropic (Claude) by default, using the current Sonnet model, updated automatically. OpenAI, Google
  (Gemini) and any OpenAI-compatible service (a local server such as Ollama, or OpenRouter …) can be used instead or as
  well. Naming a model pins it: it may cost more than the automatic choice, and the provider may retire it.
- **Spending limits** in your own currency: an overall monthly limit for all paid providers (5 a month by default; 0
  means no paid use; "no limit" is an explicit choice with a warning), plus, if you like, a limit per provider as an
  amount or a share of the overall limit. Providers' charges (usually US dollars) are converted with the European
  Central Bank's daily rates, with an optional percentage for taxes or card fees. Local services cost nothing.

## Privacy

Nothing is sent anywhere until you allow a plugin to use AI, and then only to the providers you enable. Requests carry
titles, years, release names and short text excerpts, never file paths, user names, or anything from home video and
photo libraries. See [SECURITY.md](SECURITY.md) and [docs/DESIGN.md](docs/DESIGN.md).

## Settings

**Dashboard → Plugins → AI.** Basic settings: what may use AI, providers and their keys, currency and the overall
limit. Advanced settings: a limit per provider, and a percentage for taxes or card fees.

API keys are kept in a file only Jellyfin can read, separate from the plugin settings. They are never shown again,
logged or included in exports; the settings page can only replace or clear them.

## Requirements

- Jellyfin 12.1
- An API key for a cloud provider, or a local OpenAI-compatible service

## Building

The shared source ([jellyfin-plugin-common](https://github.com/chrisgrulau/jellyfin-plugin-common)) is a git
submodule, so clone with `--recurse-submodules`, or fetch it in an existing clone; the build fails without
`external/common`:

```bash
git submodule update --init --recursive
dotnet build -c Release
```

Any .NET 10 SDK builds it (`global.json` sets the floor, so Linux distribution packages work), and package versions are locked in `packages.lock.json`. The Jellyfin
packages are pinned to the server version in `build.yaml`'s `targetAbi`; bump them together.

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
