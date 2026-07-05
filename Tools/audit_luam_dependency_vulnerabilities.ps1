param(
    [string[]]$Projects = @(
        "Content.Server.Database/Content.Server.Database.csproj",
        "Content.Server/Content.Server.csproj",
        "Content.Client/Content.Client.csproj",
        "Content.IntegrationTests/Content.IntegrationTests.csproj",
        "Content.Packaging/Content.Packaging.csproj"
    ),
    [switch]$Json
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$issues = New-Object System.Collections.Generic.List[string]
$steps = New-Object System.Collections.Generic.List[object]
$vulnerabilities = New-Object System.Collections.Generic.List[object]

function Add-Step {
    param(
        [string]$Name,
        [string]$Status,
        [string]$Detail
    )

    $steps.Add([pscustomobject]@{
        name = $Name
        status = $Status
        detail = $Detail
    }) | Out-Null
}

function Format-OutputTail {
    param([string[]]$Output)

    $tail = @($Output | Select-Object -Last 20)
    return ($tail -join [Environment]::NewLine)
}

foreach ($project in $Projects) {
    $relativeProject = $project.Replace('\', [System.IO.Path]::DirectorySeparatorChar).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $projectPath = Join-Path $root $relativeProject
    $projectStep = "dependency-vulnerability-audit:$project"

    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        $issues.Add("Dependency audit project is missing: $project") | Out-Null
        Add-Step $projectStep "failed" "Project file is missing."
        continue
    }

    $output = New-Object System.Collections.Generic.List[string]
    & dotnet list $projectPath package --vulnerable --include-transitive --format json 2>&1 |
        ForEach-Object { $output.Add([string]$_) | Out-Null }
    $exitCode = $LASTEXITCODE
    $auditJson = $output -join [Environment]::NewLine

    if ($exitCode -ne 0) {
        $issues.Add("Dependency vulnerability audit failed for ${project}: $(Format-OutputTail -Output $output)") | Out-Null
        Add-Step $projectStep "failed" "dotnet list package returned $exitCode."
        continue
    }

    try {
        $audit = $auditJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        $issues.Add("Dependency vulnerability audit returned invalid JSON for ${project}: $($_.Exception.Message)") | Out-Null
        Add-Step $projectStep "failed" "dotnet list package output was not parseable JSON."
        continue
    }

    $projectVulnerabilityCount = 0
    foreach ($auditProject in @($audit.projects)) {
        if ($null -eq $auditProject.frameworks) {
            continue
        }

        foreach ($framework in @($auditProject.frameworks)) {
            foreach ($packageSection in @("topLevelPackages", "transitivePackages")) {
                $packages = $framework.$packageSection
                if ($null -eq $packages) {
                    continue
                }

                foreach ($package in @($packages)) {
                    if ($null -eq $package.vulnerabilities) {
                        continue
                    }

                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        $projectVulnerabilityCount++
                        $vulnerabilities.Add([pscustomobject]@{
                            project = $project
                            framework = $framework.framework
                            dependency_type = if ($packageSection -eq "topLevelPackages") { "direct" } else { "transitive" }
                            package = $package.id
                            version = $package.resolvedVersion
                            severity = $vulnerability.severity
                            advisory = $vulnerability.advisoryurl
                        }) | Out-Null
                    }
                }
            }
        }
    }

    if ($projectVulnerabilityCount -gt 0) {
        $issues.Add("Dependency vulnerability audit found $projectVulnerabilityCount vulnerable package entry(s) for $project.") | Out-Null
        Add-Step $projectStep "failed" "$projectVulnerabilityCount vulnerable package entry(s) found."
    } else {
        Add-Step $projectStep "passed" "No vulnerable package entries found."
    }
}

$projectItems = @($Projects)
$vulnerabilityItems = @($vulnerabilities.ToArray())
$issueItems = @($issues.ToArray())
$stepItems = @($steps.ToArray())

$result = [pscustomobject]@{
    ok = ($issues.Count -eq 0)
    projectCount = $projectItems.Count
    projects = $projectItems
    vulnerabilityCount = $vulnerabilities.Count
    vulnerabilities = $vulnerabilityItems
    issues = $issueItems
    steps = $stepItems
}

if ($Json) {
    $result | ConvertTo-Json -Depth 10
} else {
    $status = if ($result.ok) { "OK" } else { "FAILED" }
    Write-Host "LuaM dependency vulnerability audit: $status"
    foreach ($step in $steps) {
        Write-Host ("[{0}] {1}: {2}" -f $step.status, $step.name, $step.detail)
    }

    foreach ($issue in $issues) {
        Write-Host "Issue: $issue"
    }
}

if (-not $result.ok) {
    exit 1
}
