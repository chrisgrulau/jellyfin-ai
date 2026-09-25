# Contributing

Issues and pull requests are welcome.

- Target `main`; keep pull requests focused.
- Match `.editorconfig`; the build treats warnings as errors with all analysers enabled.
- Add unit tests for budget, validation and parsing logic; they are pure functions and easy to test.
- Never commit API keys, prompts or responses containing real library data, library paths or credentials.

```bash
git submodule update --init --recursive
dotnet build -c Debug
dotnet test --solution Jellyfin.Plugin.Ai.sln
```

## Versioning

A release tag `vX.Y.Z[-suffix]` publishes version `X.Y.Z.0`, which must match `build.yaml` (CI checks this). Tags with
a suffix are published as pre-releases.
