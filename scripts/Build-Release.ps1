[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$PrivateKeyPath = "",
    [string]$InnoSetupPath = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root
[xml]$props = Get-Content -Raw -LiteralPath (Join-Path $root "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must use three numeric parts." }

if ([string]::IsNullOrWhiteSpace($InnoSetupPath)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $InnoSetupPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not (Test-Path -LiteralPath $InnoSetupPath)) { throw "Inno Setup 6 was not found." }

$releaseRoot = Join-Path $root "temp\release\v$version"
$lite = Join-Path $releaseRoot "package-lite"
$full = Join-Path $releaseRoot "package-full"
$work = Join-Path $releaseRoot "work"
if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Path $lite, $full, $work -Force | Out-Null

dotnet build-server shutdown | Out-Null
$projects = @(
    "AmdHdrScreenshotFixer.Gui\AmdHdrScreenshotFixer.Gui.csproj",
    "AmdHdrScreenshotFixer.Updater\AmdHdrScreenshotFixer.Updater.csproj",
    "AmdHdrScreenshotFixer.ReleaseTool\AmdHdrScreenshotFixer.ReleaseTool.csproj",
    "AmdHdrScreenshotFixer.SmokeTests\AmdHdrScreenshotFixer.SmokeTests.csproj"
)
foreach ($project in $projects) {
    dotnet restore $project --runtime win-x64 --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
foreach ($project in $projects) {
    dotnet build $project --configuration $Configuration --no-restore --disable-build-servers --maxcpucount:1
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
dotnet run --project $projects[3] --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed." }

dotnet publish $projects[0] --configuration $Configuration --runtime win-x64 --self-contained false --no-restore --output $lite
if ($LASTEXITCODE -ne 0) { throw "Lite app publish failed." }
$liteUpdater = Join-Path $work "updater-lite"
dotnet publish $projects[1] --configuration $Configuration --runtime win-x64 --self-contained false --no-restore --output $liteUpdater
if ($LASTEXITCODE -ne 0) { throw "Lite updater publish failed." }
foreach ($name in @("AmdHdrScreenshotFixer.Updater.exe", "AmdHdrScreenshotFixer.Updater.dll",
        "AmdHdrScreenshotFixer.Updater.deps.json", "AmdHdrScreenshotFixer.Updater.runtimeconfig.json")) {
    Copy-Item -LiteralPath (Join-Path $liteUpdater $name) -Destination (Join-Path $lite $name) -Force
}

dotnet publish $projects[0] --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
    --output $full -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Full app publish failed." }
$fullUpdater = Join-Path $work "updater-full"
dotnet publish $projects[1] --configuration $Configuration --runtime win-x64 --self-contained true --no-restore `
    --output $fullUpdater -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Full updater publish failed." }
Copy-Item -LiteralPath (Join-Path $fullUpdater "AmdHdrScreenshotFixer.Updater.exe") `
    -Destination (Join-Path $full "AmdHdrScreenshotFixer.Updater.exe") -Force

$liteChannel = "portable-framework-dependent"
$fullChannel = "portable-self-contained"
foreach ($item in @(@{ Dir=$lite; Channel=$liteChannel }, @{ Dir=$full; Channel=$fullChannel })) {
    Get-ChildItem -LiteralPath $item.Dir -Filter "*.pdb" -Recurse | Remove-Item -Force
    $metadata = Join-Path $item.Dir "amd-hdr-screenshot-fixer-package.json"
    dotnet run --project $projects[2] --configuration $Configuration --no-build -- metadata `
        --version $version --channel $item.Channel --output $metadata
    if ($LASTEXITCODE -ne 0) { throw "Package metadata generation failed." }
    Copy-Item -LiteralPath $metadata -Destination (Join-Path $item.Dir "amd-hdr-screenshot-fixer-install.json")
}

$liteName = "AmdHdrScreenshotFixer-$version-Lite-Portable.zip"
$fullName = "AmdHdrScreenshotFixer-$version-Full-Portable.zip"
$liteZip = Join-Path $releaseRoot $liteName
$fullZip = Join-Path $releaseRoot $fullName
Compress-Archive -Path (Join-Path $lite "*") -DestinationPath $liteZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $full "*") -DestinationPath $fullZip -CompressionLevel Optimal

$iss = Join-Path $root "installer\AmdHdrScreenshotFixer.iss"
& $InnoSetupPath "/DAppVersion=$version" "/DSourceDir=$lite" "/DOutputDir=$releaseRoot" `
    "/DOutputBaseFilename=AmdHdrScreenshotFixer-$version-Lite-Setup" "/DRequireDesktopRuntime=1" $iss
if ($LASTEXITCODE -ne 0) { throw "Lite Setup build failed." }
& $InnoSetupPath "/DAppVersion=$version" "/DSourceDir=$full" "/DOutputDir=$releaseRoot" `
    "/DOutputBaseFilename=AmdHdrScreenshotFixer-$version-Full-Setup" $iss
if ($LASTEXITCODE -ne 0) { throw "Full Setup build failed." }

if (-not [string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $key = (Resolve-Path -LiteralPath $PrivateKeyPath).Path
    foreach ($item in @(
        @{ Zip=$liteZip; Name=$liteName; Channel=$liteChannel; Manifest="update-lite.json" },
        @{ Zip=$fullZip; Name=$fullName; Channel=$fullChannel; Manifest="update-full.json" })) {
        $url = "https://github.com/Kratosmax/amd-hdr-screenshot-fixer/releases/download/v$version/$($item.Name)"
        dotnet run --project $projects[2] --configuration $Configuration --no-build -- manifest `
            --version $version --channel $item.Channel --package $item.Zip --private-key $key `
            --download-url $url --release-notes RELEASE_NOTES_CURRENT.md `
            --output (Join-Path $releaseRoot $item.Manifest)
        if ($LASTEXITCODE -ne 0) { throw "Signed manifest generation failed." }
    }
    Copy-Item -LiteralPath (Join-Path $releaseRoot "update-lite.json") `
        -Destination (Join-Path $releaseRoot "update.json")
}

$assetNames = @(
    "AmdHdrScreenshotFixer-$version-Full-Setup.exe", "AmdHdrScreenshotFixer-$version-Lite-Setup.exe",
    $fullName, $liteName, "update.json", "update-lite.json", "update-full.json")
$hashLines = foreach ($name in $assetNames) {
    $path = Join-Path $releaseRoot $name
    if (Test-Path -LiteralPath $path) { "{0}  {1}" -f (Get-FileHash $path -Algorithm SHA256).Hash, $name }
}
$hashLines | Out-File (Join-Path $releaseRoot "SHA256SUMS.txt") -Encoding ascii
Write-Host "Release assets: $releaseRoot"
