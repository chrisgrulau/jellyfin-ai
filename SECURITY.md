# Security policy

## Reporting a vulnerability

Please use GitHub's **private vulnerability reporting** (Security → Report a vulnerability) rather than a public issue.
Reports are acknowledged as soon as possible.

## Secrets

API keys are entered by an administrator and stored on their server in a file readable only by the account Jellyfin
runs as (mode 0600), separate from the plugin configuration. The settings API only reports whether a key is set; keys
are never returned, logged, included in alerts or in configuration exports. Provider error bodies will be redacted before
they are logged, because some echo part of the key. Nothing secret belongs in this repository.

## What leaves the server

Only when an administrator has allowed a plugin to use AI, and only to the providers they enable: titles, years,
release names, candidate titles and short subtitle or transcript excerpts. Never file paths, user names, or anything
from home video and photo libraries.

An API key goes only to its own provider: Anthropic, OpenAI, Google (in the `x-goog-api-key` header, never in a URL), or
the OpenAI-compatible service at the address the administrator entered. That address must use HTTPS unless it is on the
server itself or the local network (the family's shared local-address check), so a key never crosses the internet in
plain text; addresses with a user name, password or query are refused. A compatible service used without a key gets no
`Authorization` header at all.

## Model output and prompts (rules for the model calls, which are being built)

Model output is untrusted data. It is validated against what was asked (for example, the answer must be one of the
candidates offered) and never used as a path, id or file name. Text that could come from an attacker (release names,
NFO text, subtitles, transcripts) is passed only in clearly delimited data fields, never as instructions, and answers
are constrained by JSON schemas.

## Other plugins

Other plugins will call this one through a versioned JSON-in/JSON-out entry point inside the server, identified by plugin
id, with size limits. There is no public HTTP endpoint for model calls; the HTTP endpoints are for the settings page and
require an administrator.

The spending entry point (`SpendingBridge`) exchanges only amounts, provider names and purposes with Shoal Subtitles:
never audio, text or keys. Requests are at most 4 KB, every field is checked by type, amounts must be 0 to 100,000 in
a supported currency, only Subtitles is accepted (and only while **Allow Subtitles to use this budget** is ticked),
and a reservation can only be settled or released by the plugin that made it.
