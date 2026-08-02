param(
    [string]$ServerLogPath,
    [switch]$NoBuild,
    [string]$ResultsDirectory = "Content.IntegrationTests\TestResults\LuaMPostDeploySmoke"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root $ResultsDirectory
New-Item -ItemType Directory -Force -Path $results | Out-Null

$tests = @(
    "LuaMRestoredPowerStateTest",
    "LuaMRestoredShipShieldTest",
    "LuaMRestoredShipGunneryTest",
    "LuaMShipyardGrantedAccessPersistenceRuntimeTest",
    "LuaMRestoredInteractionUseDelayTest",
    "LuaMSalvageExpeditionConsoleBindingTest",
    "LuaMExpeditionPlannerTest",
    "LuaMSectorStoryTest",
    "LuaMRescueAgentRuntimeTest"
)
$filter = ($tests | ForEach-Object { "FullyQualifiedName~$_" }) -join "|"
$arguments = @(
    "test",
    (Join-Path $root "Content.IntegrationTests\Content.IntegrationTests.csproj"),
    "--filter", $filter,
    "--logger", "trx;LogFileName=luam-post-deploy-smoke.trx",
    "--results-directory", $results,
    "--verbosity", "minimal"
)
if ($NoBuild) { $arguments += "--no-build" }

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "LuaM post-deploy integration smoke failed with exit code $LASTEXITCODE."
}

if (-not [string]::IsNullOrWhiteSpace($ServerLogPath)) {
    $resolvedLog = Resolve-Path -LiteralPath $ServerLogPath -ErrorAction Stop
    $fatal = Select-String -LiteralPath $resolvedLog -Pattern @(
        "Unhandled exception",
        "Stack overflow",
        "OutOfMemoryException",
        "NaN.*(APC|substation|battery|power)",
        "(APC|substation|battery|power).*NaN"
    ) -CaseSensitive:$false
    if ($fatal) {
        $fatal | Select-Object -First 20 | ForEach-Object { Write-Error $_.Line }
        throw "Server log contains fatal or non-finite power-state signatures."
    }
}

Write-Output "LuaM post-deploy smoke passed: $($tests.Count) critical feature groups."
