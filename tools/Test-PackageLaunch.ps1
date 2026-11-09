param(
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [switch]$InstallFrameworkDependency
)
$ErrorActionPreference = 'Stop'
$staging = (Resolve-Path -LiteralPath $StagingDirectory).Path
$executable = Join-Path $staging 'RedrainCast.App.exe'
foreach ($required in @($executable, (Join-Path $staging 'resources.pri'), (Join-Path $staging 'AppxManifest.xml'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing extracted package input: $required" }
}
[xml]$manifest = Get-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Raw
$framework = $manifest.Package.Dependencies.PackageDependency | Where-Object { $_.Name -ceq 'Microsoft.WindowsAppRuntime.2' }
if ($null -ne $framework) {
    $architecture = [string]$manifest.Package.Identity.ProcessorArchitecture
    $installed = @(Get-AppxPackage -Name $framework.Name | Where-Object {
        [string]$_.Architecture -ieq $architecture -and [version]$_.Version -ge [version]$framework.MinVersion -and
        $_.Publisher -ceq $framework.Publisher
    })
    if ($installed.Count -eq 0 -and $InstallFrameworkDependency) {
        $dependencies = @(Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $staging) 'packages') -Recurse -File -Filter 'Microsoft.WindowsAppRuntime.2.msix' |
            Where-Object { $_.Directory.Name -ieq $architecture })
        if ($dependencies.Count -ne 1) { throw 'Exactly one matching SDK framework package is required for smoke-test setup.' }
        Add-AppxPackage -Path $dependencies[0].FullName -ErrorAction Stop
        $installed = @(Get-AppxPackage -Name $framework.Name | Where-Object {
            [string]$_.Architecture -ieq $architecture -and [version]$_.Version -ge [version]$framework.MinVersion -and
            $_.Publisher -ceq $framework.Publisher
        })
    }
    if ($installed.Count -eq 0) { throw 'Install the manifest-declared Windows App Runtime before extracted-package smoke testing.' }
}
$report = [IO.Path]::GetFullPath($ReportPath)
if ($report.Contains('"')) { throw 'Report path cannot contain a quote.' }
if (Test-Path -LiteralPath $report) { throw 'Package launch validation requires a new report path.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $report) -Force | Out-Null
$process = Start-Process -FilePath $executable -WorkingDirectory $staging `
    -ArgumentList @('--package-startup-self-test', "`"$report`"") -PassThru
try {
    if (-not $process.WaitForExit(90000)) { throw 'Extracted package startup/layout smoke timed out after 90 seconds.' }
    if ($process.ExitCode -ne 0) { throw "Extracted package startup failed with exit code $($process.ExitCode)." }
    if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw 'Package process produced no layout report.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($result.passed -isnot [bool] -or -not $result.passed -or $result.packageStartupOnly -ne $true -or
        $result.inputInjected -ne $false -or $result.captureStarted -ne $false) {
        throw 'Package startup report did not pass the isolated, no-input startup check.'
    }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $process.Dispose()
}
Write-Output 'Extracted MSIX startup/layout passed; installed-package activation and Store acceptance remain separate checks.'
