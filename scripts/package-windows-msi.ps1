$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
. (Join-Path $repoRoot "packaging/windows/common/package-vars.ps1")

$projectPath = Join-Path $repoRoot "LavaLauncher.Desktop/LavaLauncher.Desktop.csproj"
$wixProjectPath = Join-Path $repoRoot "packaging/windows/LavaLauncher.WindowsInstaller.wixproj"
$iconToolProject = Join-Path $repoRoot "packaging/windows/Tools/LavaLauncher.WindowsIconTool/LavaLauncher.WindowsIconTool.csproj"
$iconSourcePath = Join-Path $repoRoot "packaging/common/app-icon.svg"
$runtimeIdentifier = if ($env:RUNTIME_IDENTIFIER) { $env:RUNTIME_IDENTIFIER } else { "win-x64" }
$publishDir = if ($env:PUBLISH_DIR) { $env:PUBLISH_DIR } else { Join-Path $repoRoot "artifacts/publish/$runtimeIdentifier" }
$windowsRoot = Join-Path $repoRoot "artifacts/windows"
$generatedAssetsDir = Join-Path $windowsRoot "generated"
$packageOutputDir = Join-Path $windowsRoot "msi"
$rsvgConvertBin = if ($env:RSVG_CONVERT_BIN) { $env:RSVG_CONVERT_BIN } else { (Get-Command rsvg-convert -ErrorAction SilentlyContinue)?.Source }
$suppressValidation = if ($env:SUPPRESS_VALIDATION) { $env:SUPPRESS_VALIDATION } else { "true" }

if (-not $IsWindows) {
    throw "WiX 6 packaging currently requires Windows."
}

if ($args.Length -gt 0) {
    if (Test-Path -LiteralPath $args[0] -PathType Container) {
        $publishDir = (Resolve-Path $args[0]).Path
    }
    else {
        $runtimeIdentifier = $args[0]
        $publishDir = Join-Path $repoRoot "artifacts/publish/$runtimeIdentifier"
    }
}

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    dotnet publish $projectPath `
        -c Release `
        -r $runtimeIdentifier `
        --self-contained true `
        -p:PublishAot=true `
        -p:PublishTrimmed=true `
        -p:PublishSingleFile=true `
        -o $publishDir
}

if (-not (Test-Path -LiteralPath $iconSourcePath -PathType Leaf)) {
    throw "Shared icon not found: $iconSourcePath"
}

if (-not $rsvgConvertBin) {
    throw "rsvg-convert is required to generate the Windows app icon from the shared SVG."
}

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $generatedAssetsDir, $packageOutputDir
New-Item -ItemType Directory -Force -Path $generatedAssetsDir, $packageOutputDir | Out-Null

$iconSizes = 16, 32, 48, 64, 128, 256
foreach ($iconSize in $iconSizes) {
    & $rsvgConvertBin `
        --width $iconSize `
        --height $iconSize `
        $iconSourcePath `
        --output (Join-Path $generatedAssetsDir "app-icon-$iconSize.png")
}

dotnet run `
    --project $iconToolProject `
    -- `
    (Join-Path $generatedAssetsDir "app-icon.ico") `
    (Join-Path $generatedAssetsDir "app-icon-16.png") `
    (Join-Path $generatedAssetsDir "app-icon-32.png") `
    (Join-Path $generatedAssetsDir "app-icon-48.png") `
    (Join-Path $generatedAssetsDir "app-icon-64.png") `
    (Join-Path $generatedAssetsDir "app-icon-128.png") `
    (Join-Path $generatedAssetsDir "app-icon-256.png")

dotnet build $wixProjectPath `
    -c Release `
    -p:PublishDir=$publishDir `
    -p:GeneratedAssetsDir=$generatedAssetsDir `
    -p:AppName=$script:AppName `
    -p:AppAssemblyName=$script:AppBinary `
    -p:AppVersion=$script:PackageVersion `
    -p:WindowsFolderName=$script:WindowsFolderName `
    -p:SuppressValidation=$suppressValidation

$outputMsi = Get-ChildItem -Path (Join-Path $repoRoot "packaging/windows/bin") -Recurse -Filter "$($script:WindowsFolderName)-$($script:PackageVersion)-win-x64.msi" | Select-Object -First 1
if (-not $outputMsi) {
    throw "Could not find the built MSI in packaging/windows/bin."
}

Copy-Item -LiteralPath $outputMsi.FullName -Destination $packageOutputDir -Force

Write-Output (Join-Path $packageOutputDir "$($script:WindowsFolderName)-$($script:PackageVersion)-win-x64.msi")
