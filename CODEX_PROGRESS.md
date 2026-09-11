# Codex Progress

## Current state

- Target release: `0.2.0`; do not overwrite the published `v0.1.0` assets.
- Repository target: `https://github.com/Kratosmax/amd-hdr-screenshot-fixer`.
- WPF GUI, PNG correction, persistent defaults, drag/drop and integrated watcher are implemented.
- App icon, shared update core, external updater, release tool, Inno Setup and tag workflow are released.
- RSA public key is committed in `AmdHdrScreenshotFixer.Core/UpdateTrust.cs`; the private key remains outside Git. GitHub Secret `UPDATE_SIGNING_KEY` was configured on 2026-09-11 with explicit authorization.
- Release `v0.2.0` is published at `https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/tag/v0.2.0`.
- Release commit `37833ca662ae0c787b237294bcc3f200f537cde2` is tagged `v0.2.0`.
- GitHub Actions run `34594779432` completed successfully in 2m52s.

## Current release

- The `v0.2.0` source adds three-pair-or-more offline cross-validation calibration, bounded 9³/17³ 3D LUT fitting, ΔE2000 scoring, result/reference preview and factory-model recovery.
- Saturation, black point and white point are part of the saved final adjustment layer used by preview, export and the watcher.
- A PinNote-style unified settings window now controls automatic update checks, GitHub prefix routes, an independent HTTP proxy and current-user startup registration. No named profile system is planned per user direction.
- Lite projects allow major runtime roll-forward. The Lite installer runtime check now inspects the actual standard x64 `Microsoft.WindowsDesktop.App\8.*` directory instead of relying on registry subkeys that can be absent.
- The signed local candidate and independently downloaded online `v0.2.0` assets passed the checks below.

## Local verification (2026-09-11)

- Release GUI and updater builds completed with zero warnings and zero errors.
- SmokeTests passed RGB/exposure/contrast/saturation/levels, trilinear 3D LUT, proxy normalization/order, config preservation, three-pair calibration and update ZIP path safety.
- A non-identity calibration regression uses three synthetic color-distortion pairs for fitting and a fourth unseen image for validation. Raw RGB byte MAE fell from `7.068` to `1.587` (77.5% lower); cross-validation measured mean DeltaE `0.673` and P95 DeltaE `2.188`, both within the configured thresholds.
- WPF screenshots were generated for 1120x720, 860x580, settings and calibration windows under ignored `temp/qa-vnext`.
- `scripts/Build-Release.ps1 -PrivateKeyPath <private.pem>` produced all four signed local package forms under ignored `temp/release/v0.2.0`; both Inno Setup variants compiled successfully.
- The packaged Lite GUI and updater runtime configs both contain `rollForward: Major`.
- The real Lite package opened the unified settings window and the real Full package opened the calibration window; screenshots are under ignored `temp/qa-v0.2.0` with no visible overlap or clipping.
- The local Lite portable preview ZIP is `temp/release/v0.2.0/AmdHdrScreenshotFixer-0.2.0-Lite-Portable.zip`, size 259,939 bytes, SHA-256 `4BAF1F7D838F8E8BCACD2B7ADB6B40A165D0597E7CD12DCA2DDE931D8BD6977B`.
- Both `update-lite.json` and `update-full.json` passed `AmdHdrScreenshotFixer.ReleaseTool verify` against their real candidate ZIPs.
- Commit, push, tag and Release publication were explicitly authorized and completed on 2026-09-11.

## Release verification

- The `v0.2.0` Release is public, non-draft and non-prerelease with all eight expected assets.
- All seven checksummed assets match online `SHA256SUMS.txt` and GitHub asset digests; `SHA256SUMS.txt` is the eighth asset.
- Online `update-full.json`, `update-lite.json` and compatibility `update.json` pass RSA signature, channel, hash and package-structure verification. `update.json` is byte-identical to `update-lite.json`.
- GitHub's latest API and `releases/latest/download/update.json` both resolve to `v0.2.0`.
- The original published `v0.1.0` Lite client discovered, downloaded, verified and handed off the online `v0.2.0` package to its external updater. The temporary install changed to version `0.2.0`, retained the Lite channel and restarted successfully.
- The online Lite package opened the unified settings window after that upgrade, and the independently extracted online Full package opened the calibration window. Screenshots are retained under ignored `temp/qa-v0.2.0`.

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
- Preserve the verified `v0.1.0 -> v0.2.0` update protocol unless a future release includes an explicit migration plan and regression coverage.
