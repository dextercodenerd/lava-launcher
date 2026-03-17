$ErrorActionPreference = "Stop"

$script:RepoRoot = if ($env:REPO_ROOT) { $env:REPO_ROOT } else { (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path }
$script:UserPropsPath = Join-Path $script:RepoRoot "user.props"

function Get-XmlPropertyValue {
    param(
        [string]$FilePath,
        [string]$PropertyName
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        return $null
    }

    $match = Select-String -Path $FilePath -Pattern "<$PropertyName>(.*)</$PropertyName>" | Select-Object -First 1
    if (-not $match) {
        return $null
    }

    return $match.Matches[0].Groups[1].Value
}

function Resolve-PackageProperty {
    param(
        [AllowEmptyString()][string]$CurrentValue,
        [string]$PropertyName,
        [string]$FallbackValue
    )

    if (-not [string]::IsNullOrWhiteSpace($CurrentValue)) {
        return $CurrentValue
    }

    $userValue = Get-XmlPropertyValue -FilePath $script:UserPropsPath -PropertyName $PropertyName
    if (-not [string]::IsNullOrWhiteSpace($userValue)) {
        return $userValue
    }

    return $FallbackValue
}

$script:PackageVersion = if ($env:PACKAGE_VERSION) {
    $env:PACKAGE_VERSION
}
else {
    Get-XmlPropertyValue -FilePath (Join-Path $script:RepoRoot "Directory.Build.props") -PropertyName "AppVersion"
}

$script:AppName = Resolve-PackageProperty -CurrentValue $env:APP_NAME -PropertyName "AppName" -FallbackValue "Yet Another Minecraft Launcher"
$script:AppBinary = Resolve-PackageProperty -CurrentValue $env:APP_BINARY -PropertyName "AppAssemblyName" -FallbackValue "YamLauncher"
$script:WindowsFolderName = Resolve-PackageProperty -CurrentValue $env:WINDOWS_FOLDER_NAME -PropertyName "WindowsFolderName" -FallbackValue "YamLauncher"
