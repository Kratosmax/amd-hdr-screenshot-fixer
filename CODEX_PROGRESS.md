# Codex Progress

## Current state

- Target release: `0.1.0`.
- Repository target: `https://github.com/Kratosmax/amd-hdr-screenshot-fixer`.
- WPF GUI, PNG correction, persistent defaults, drag/drop and integrated watcher are implemented.
- App icon, shared update core, external updater, release tool, Inno Setup and tag workflow are implemented locally.
- RSA public key is committed in `AmdHdrScreenshotFixer.Core/UpdateTrust.cs`; the private key remains outside Git. Uploading it to GitHub Secret `UPDATE_SIGNING_KEY` is pending explicit sensitive-data authorization.

## Required verification

```powershell
dotnet run --project .\AmdHdrScreenshotFixer.SmokeTests\AmdHdrScreenshotFixer.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -PrivateKeyPath <private.pem>
```

Verify both signed manifests with `AmdHdrScreenshotFixer.ReleaseTool`, run the Full and Lite candidates, inspect a real WPF screenshot at normal and minimum sizes, then check the online Release assets and hashes.

## Security and release boundaries

- Never commit PEM/PFX files, user screenshots, logs, build outputs or `%LOCALAPPDATA%` data.
- Do not publish a manifest without the repository signing key.
- Do not change `UpdateTrust.ProductId`, channels, public key, release URL prefix or package marker names without a migration plan.
- This first release has no earlier signed client, so an actual previous-version upgrade can only be exercised when preparing `0.2.0`.
