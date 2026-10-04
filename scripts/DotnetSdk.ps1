#Requires -Version 7.0
# The installed SDK list does not establish which SDK this checkout will use.
# Evaluate global.json from the checkout before cleanup, publishing or installation.
function Test-CycleArcSelectedSdk {
    param([string]$SelectedVersion, [string]$MinimumVersion = '10.0.100')
    if ($SelectedVersion -notmatch '^\d+\.\d+\.\d+$') { return $false }
    $selected = [version]$SelectedVersion
    $minimum = [version]$MinimumVersion
    $selected.Major -eq $minimum.Major -and $selected.Minor -eq $minimum.Minor -and $selected -ge $minimum
}

function Assert-CycleArcDotnetSdk {
    param([string]$RepoRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))))
    $configuration = Get-Content -LiteralPath (Join-Path $RepoRoot 'global.json') -Raw | ConvertFrom-Json
    if ($configuration.sdk.version -notmatch '^10\.0\.\d+$' -or $configuration.sdk.rollForward -ne 'latestFeature' -or $configuration.sdk.allowPrerelease -ne $false) {
        throw 'global.json must select stable .NET 10 SDKs with latestFeature roll-forward.'
    }
    Push-Location -LiteralPath $RepoRoot
    try {
        $output = @(& dotnet --version 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    $selected = ($output -join [Environment]::NewLine).Trim()
    if ($exitCode -ne 0 -or !(Test-CycleArcSelectedSdk -SelectedVersion $selected -MinimumVersion $configuration.sdk.version)) {
        throw "A stable .NET 10 SDK selected by global.json is required (minimum $($configuration.sdk.version); selected '$selected'). Install the .NET 10 SDK and retry. The current installation was not replaced."
    }
    Write-Host "Selected .NET SDK: $selected (global.json)"
}
