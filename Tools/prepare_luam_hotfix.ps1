[CmdletBinding()]
param(
    [string[]]$Scope = @("Auto"),
    [switch]$IncludeBuild,
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "luam_release_state.ps1")

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $started = [DateTime]::UtcNow
    $commandOutput = @(& $FilePath @Arguments 2>&1 | ForEach-Object { [string]$_ })
    $exit = if ($null -eq $global:LASTEXITCODE) { 0 } else { $global:LASTEXITCODE }
    $finished = [DateTime]::UtcNow
    $result = [pscustomobject]@{
        name = $Name
        file = $FilePath
        arguments = $Arguments
        exitCode = $exit
        startedAtUtc = $started.ToString("o")
        finishedAtUtc = $finished.ToString("o")
        seconds = [math]::Round(($finished - $started).TotalSeconds, 3)
        status = if ($exit -eq 0) { "passed" } else { "failed" }
        outputTail = @($commandOutput | Select-Object -Last 40)
    }

    if ($exit -ne 0) {
        throw "$Name failed with exit code $exit.`n$($commandOutput -join "`n")"
    }

    return $result
}

function Get-ChangedRepoFiles {
    $previousErrorActionPreference = $null
    Push-Location $root
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        $tracked = @(git -c core.quotepath=false diff --name-only HEAD -- 2>&1 |
            Where-Object { [string]$_ -notmatch '^warning: in the working copy of ' } |
            ForEach-Object { [string]$_ })
        if ($LASTEXITCODE -ne 0) { throw "git diff failed with exit code $LASTEXITCODE" }
        $untracked = @(git -c core.quotepath=false ls-files --others --exclude-standard -- 2>&1 |
            Where-Object { [string]$_ -notmatch '^warning: in the working copy of ' } |
            ForEach-Object { [string]$_ })
        if ($LASTEXITCODE -ne 0) { throw "git ls-files failed with exit code $LASTEXITCODE" }
        return @($tracked + $untracked | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    }
    finally {
        if ($null -ne $previousErrorActionPreference) {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        Pop-Location
    }
}

function Resolve-AutoScopes {
    param([string[]]$ChangedFiles)

    $resolved = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $resolved.Add("Policy") | Out-Null

    foreach ($path in $ChangedFiles) {
        $normalized = $path.Replace('\', '/')
        if ($normalized -match '(^|/)PDA/|pda|Ringer') { $resolved.Add("PdaUi") | Out-Null }
        if ($normalized -match 'Shuttles/UI|Shuttle|Radar') { $resolved.Add("ShuttleUi") | Out-Null }
        if ($normalized -match 'Cryo|cryo') { $resolved.Add("Cryo") | Out-Null }
        if ($normalized -match 'GameRules|game_presets|roundstart|StationSpawning|GhostSystem|FrontierMap') { $resolved.Add("Round") | Out-Null }
        if ($normalized -match 'server_config\.remote\.toml|ConfigPresets|ServerInfo/Intro|launcher|infolinks') { $resolved.Add("LauncherInfo") | Out-Null }
        if ($normalized -match 'Bank|MonoCoins|PdaSystem|PdaUpdateState') { $resolved.Add("Bank") | Out-Null }
        if ($normalized -match '(^|/)Content\.(Client|Server|Shared)/_LuaM|Content\.IntegrationTests/Tests/_LuaM|Content\.Tests/.+/_LuaM|Resources/.+/_LuaM') {
            $resolved.Add("LuaM") | Out-Null
        }
    }

    if ($resolved.Count -eq 1) {
        $resolved.Add("LuaM") | Out-Null
    }

    return @($resolved | Sort-Object)
}

function Test-LauncherInfoConfig {
    $configPath = Join-Path $root "server_config.remote.toml"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "server_config.remote.toml not found."
    }

    $text = Get-Content -LiteralPath $configPath -Raw
    if ($text -notmatch '(?m)^\[infolinks\]\s*$') {
        throw "server_config.remote.toml must contain [infolinks]."
    }
    if ($text -notmatch '(?m)^discord\s*=\s*"https://discord\.gg/hfSkDrKUR2"\s*$') {
        throw "server_config.remote.toml must expose the launcher Discord link through infolinks.discord."
    }
    if ($text -match '(?is)\[game\].*desc\s*=\s*""".*discord\.gg') {
        throw "Discord invite must not be duplicated inside game.desc."
    }

    return [pscustomobject]@{
        name = "launcher-info-config"
        status = "passed"
        file = "server_config.remote.toml"
    }
}

$allowedScopes = @("Auto", "Policy", "PdaUi", "ShuttleUi", "Cryo", "Round", "LauncherInfo", "Bank", "LuaM")
$requestedScopes = @(
    foreach ($scopeItem in $Scope) {
        foreach ($piece in ([string]$scopeItem).Split([char]',', [System.StringSplitOptions]::RemoveEmptyEntries)) {
            $trimmed = $piece.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $trimmed
            }
        }
    }
)
if ($requestedScopes.Count -eq 0) {
    $requestedScopes = @("Auto")
}
$unknownScopes = @($requestedScopes | Where-Object { $_ -notin $allowedScopes })
if ($unknownScopes.Count -gt 0) {
    throw "Unknown scope(s): $($unknownScopes -join ', '). Allowed: $($allowedScopes -join ', ')"
}

$changedFiles = @(Get-ChangedRepoFiles)
$effectiveScopes = if ($requestedScopes -contains "Auto") {
    @(Resolve-AutoScopes -ChangedFiles $changedFiles)
} else {
    @($requestedScopes | Sort-Object -Unique)
}

$commands = [System.Collections.Generic.List[object]]::new()
function Add-DotnetIntegration {
    param([string]$Name, [string]$Filter)
    $commands.Add([pscustomobject]@{
        name = $Name
        file = "dotnet"
        args = @("test", "Content.IntegrationTests/Content.IntegrationTests.csproj", "--configuration", "DebugOpt", "--no-restore", "--filter", $Filter, "--", "NUnit.NumberOfTestWorkers=1")
    }) | Out-Null
}
function Add-DotnetUnit {
    param([string]$Name, [string]$Project, [string]$Filter)
    $commands.Add([pscustomobject]@{
        name = $Name
        file = "dotnet"
        args = @("test", $Project, "--configuration", "DebugOpt", "--no-restore", "--filter", $Filter)
    }) | Out-Null
}

foreach ($item in $effectiveScopes) {
    switch ($item) {
        "Policy" {
            $commands.Add([pscustomobject]@{
                name = "release-policy-contract"
                file = "powershell"
                args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "Tools/test_luam_release_contract.ps1", "-Json")
            }) | Out-Null
        }
        "PdaUi" {
            Add-DotnetIntegration "pda-text-layout" "FullyQualifiedName~LuaMPdaTextLayoutTest"
            Add-DotnetUnit "pda-navigation-button" "Content.Tests/Content.Tests.csproj" "FullyQualifiedName~LuaMPdaNavigationButtonTest"
        }
        "ShuttleUi" {
            Add-DotnetIntegration "shuttle-console-layout" "FullyQualifiedName~LuaMShuttleConsoleLayoutTest"
        }
        "Cryo" {
            Add-DotnetIntegration "deep-cryo" "FullyQualifiedName~LuaMDeepCryo"
        }
        "Round" {
            Add-DotnetIntegration "frontier-map-load" "FullyQualifiedName~LuaMFrontierMapLoadTest"
        }
        "LauncherInfo" {
            # Handled as an in-process config contract below.
        }
        "Bank" {
            Add-DotnetIntegration "bank-and-pda-contracts" "FullyQualifiedName~LuaMBankAndPdaContractsTest|FullyQualifiedName~LuaMBankPersistenceTest|FullyQualifiedName~LuaMBankDurableMutationContractTest"
        }
        "LuaM" {
            Add-DotnetUnit "content-tests-luam" "Content.Tests/Content.Tests.csproj" "FullyQualifiedName~LuaM"
            Add-DotnetIntegration "integration-tests-luam" "FullyQualifiedName~LuaM"
        }
    }
}

if ($IncludeBuild) {
    $commands.Add([pscustomobject]@{
        name = "client-build-debugopt"
        file = "dotnet"
        args = @("build", "Content.Client/Content.Client.csproj", "--configuration", "DebugOpt", "--no-restore")
    }) | Out-Null
    $commands.Add([pscustomobject]@{
        name = "server-build-debugopt"
        file = "dotnet"
        args = @("build", "Content.Server/Content.Server.csproj", "--configuration", "DebugOpt", "--no-restore")
    }) | Out-Null
}

$results = [System.Collections.Generic.List[object]]::new()
$ok = $true
$failure = $null
Push-Location $root
try {
    if ($effectiveScopes -contains "LauncherInfo") {
        $results.Add((Test-LauncherInfoConfig)) | Out-Null
    }

    foreach ($command in @($commands | Sort-Object name -Unique)) {
        $results.Add((Invoke-CheckedCommand -Name $command.name -FilePath $command.file -Arguments @($command.args))) | Out-Null
    }
}
catch {
    $ok = $false
    $failure = $_.Exception.Message
}
finally {
    Pop-Location
}

$summary = [pscustomobject]@{
    ok = $ok
    phase = "local-fast"
    scopes = $effectiveScopes
    includeBuild = [bool]$IncludeBuild
    changedFileCount = $changedFiles.Count
    results = @($results.ToArray())
    failure = $failure
}

if ($ok) {
    Set-LuaMReleaseStage -Phase "local-fast" -Result $summary -Root $root | Out-Null
} else {
    Set-LuaMReleaseStage -Phase "failed" -Result $failure -Root $root | Out-Null
}

if ($Json) {
    $summary | ConvertTo-Json -Depth 8
} else {
    Write-Host "LuaM local preparation: $(if ($ok) { 'OK' } else { 'FAILED' })"
    Write-Host "Scopes: $($effectiveScopes -join ', ')"
    foreach ($result in $results) {
        Write-Host ("[{0}] {1} ({2}s)" -f $result.status, $result.name, $result.seconds)
    }
    if (-not $ok) {
        Write-Error $failure -ErrorAction Continue
    }
}

if (-not $ok) {
    exit 1
}
