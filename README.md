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

> **Status:** alpha. Claude calls within the spending limits, a prepaid-credit countdown and the entry point the other
> plugins use are in place. OpenAI, Google and OpenAI-compatible providers are coming later: the settings page lists
> them, but they can't be set up or used yet.

## Installing

**From the Shoal plugin repository (recommended):** in **Dashboard → Plugins → Repositories**, add
`https://raw.githubusercontent.com/chrisgrulau/jellyfin-shoal/main/manifest.json`, install **Shoal AI** from the
catalogue and restart Jellyfin. Updates install automatically unless you switch that off for the plugin under **My
Plugins**.

**By hand:** download `jellyfin-plugin-ai.zip` from the [releases](../../releases), check it against `SHA256SUMS` (and,
if you like, `gh attestation verify jellyfin-plugin-ai.zip --repo chrisgrulau/jellyfin-ai`), and put **all three DLLs**
it contains (`Jellyfin.Plugin.Ai.dll`, `Anthropic.dll`, `Microsoft.Extensions.AI.Abstractions.dll`) in
`<jellyfin data>/plugins/AI_<version>/`, then restart Jellyfin.

Then open **Dashboard → Plugins → Shoal AI**: add a Claude API key, press **Test**, and allow Ingest and Subtitles to use
it. Nothing is sent anywhere until you do.

Uninstalling leaves the plugin's data folder (`keys.json`, `spend.json`, `rates.json`); delete it by hand if you like.

## How it fits together

- **Optional everywhere.** Ingest and Subtitles work fully without this plugin. When it is installed, and you allow
  them to, they ask it for a tiebreak; otherwise they leave hard cases for your review, as before.
- **Providers.** Anthropic (Claude), using Claude Opus 5.5 unless you name another model (USD 4 / 20 per million input
  / output tokens; a tie-break costs a fraction of a cent, a transcript comparison a few cents). OpenAI, Google (Gemini)
  and OpenAI-compatible services (a local server such as Ollama, or OpenRouter …) are coming later: the settings page
  shows them, but they can't be set up or used yet. Naming a model pins it: it may cost more, and the provider may retire it.
- **Spending limits** in your own currency: an overall monthly limit for all paid providers (5 a month by default; 0
  means no paid use; "no limit" is an explicit choice with a warning), plus, if you like, a limit per provider as an
  amount or a share of the overall limit. Providers' charges (usually US dollars) are converted with the European
  Central Bank's daily rates, with an optional percentage for taxes or card fees. Local services cost nothing.

## Privacy

Nothing is sent anywhere until you allow a plugin to use AI, and then only to the providers you enable. Never file
paths, user names, or anything from home video and photo libraries. What each plugin sends:

| From | When | What |
|---|---|---|
| Ingest | A close match | The file name; candidate titles, years and kinds |
| Ingest | An episode named by title, or with no usable name | Also the season's episode titles, years and short synopses; for no usable name, up to 4,000 characters of transcribed dialogue |
| Subtitles | The wording differs from what is said, or an audit | The subtitle language, a few minutes of heard phrases and the subtitle lines around them |
 See [SECURITY.md](SECURITY.md) and [docs/DESIGN.md](docs/DESIGN.md).

## Settings

**Dashboard → Plugins → AI.** Basic settings: what may use AI, providers and their keys, a prepaid credit to count
down (the amount, the currency it was bought in, usually US dollars, and the date), currency and the overall limit.
Advanced settings: a limit per provider, and a percentage for taxes or card fees. Providers are listed in a fixed order,
not an order of preference; for now Anthropic (Claude) is the only one that can be used.

API keys are kept in a file only Jellyfin can read, separate from the plugin settings. They are never shown again,
logged or included in exports; the settings page can only replace or clear them.

## Requirements

- Jellyfin 12.1
- An Anthropic API key (other providers are coming later)

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
