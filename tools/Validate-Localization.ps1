param(
    [Parameter(Mandatory = $true)][string]$Pri,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [string]$ManifestPath,
    [string]$PackagePri
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Pri = (Resolve-Path -LiteralPath $Pri).Path
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path (Get-Location) $ReportPath }
if (Test-Path -LiteralPath $ReportPath) { throw 'Localization evidence must use a new report path.' }
$languages = @('ar', 'de', 'en-US', 'es', 'fr', 'hi', 'id', 'it', 'ja', 'ko', 'ms', 'nl',
    'pl', 'pt-BR', 'ru', 'sv', 'th', 'tr', 'uk', 'vi', 'zh-Hans', 'zh-Hant')
$strings = Join-Path $root 'src\RedrainCast.App\Strings'
$actualDirectories = @(Get-ChildItem -LiteralPath $strings -Directory | Select-Object -ExpandProperty Name | Sort-Object)
if (($actualDirectories -join '|') -cne (($languages | Sort-Object) -join '|')) {
    throw 'Resource directories must exactly match the supported 22-language set.'
}
$catalogs = @{}
$hashes = @{}
$utf8 = New-Object Text.UTF8Encoding($false, $true)
foreach ($language in $languages) {
    $path = Join-Path $strings "$language\Resources.resw"
    [void]$utf8.GetString([IO.File]::ReadAllBytes($path))
    $document = New-Object Xml.XmlDocument
    $document.Load($path)
    $values = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($data in $document.DocumentElement.SelectNodes('data')) {
        $key = $data.GetAttribute('name')
        $value = $data.SelectSingleNode('value').InnerText
        if ($values.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($value)) {
            throw "Duplicate/empty resource: $language/$key"
        }
        $values.Add($key, $value)
    }
    $catalogs[$language] = $values
    $hashes[$language] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$keys = @($catalogs['en-US'].Keys | Sort-Object)
foreach ($language in $languages) {
    if ((@($catalogs[$language].Keys | Sort-Object) -join '|') -cne ($keys -join '|')) {
        throw "Resource keys differ from canonical English: $language"
    }
}
$reportDirectory = Split-Path -Parent $ReportPath
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$dumpPath = Join-Path $reportDirectory ("pri-validation-" + [Guid]::NewGuid().ToString('N') + '.xml')
$packageDumpPath = Join-Path $reportDirectory ("package-pri-validation-" + [Guid]::NewGuid().ToString('N') + '.xml')
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$makepri = Join-Path $cache "microsoft.windows.sdk.buildtools\10.0.26100.4654\bin\10.0.26100.0\$hostArchitecture\makepri.exe"
if (-not (Test-Path -LiteralPath $makepri)) { throw 'Pinned MakePri is missing; build the app first.' }
$counts = @{}
foreach ($language in $languages) { $counts[$language] = 0 }
try {
    & $makepri dump /if $Pri /of $dumpPath /dt detailed /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'MakePri could not inspect the compiled resource index.' }
    $dump = New-Object Xml.XmlDocument
    $dump.Load($dumpPath)
    if ($null -eq $dump.SelectSingleNode("//ResourceMap[@primary='true' and @name='RedrainCast.App']")) {
        throw 'Application PRI primary map must remain RedrainCast.App.'
    }
    foreach ($key in $keys) {
        $resource = $dump.SelectSingleNode("//*[local-name()='NamedResource' and @uri='ms-resource://RedrainCast.App/Resources/$key']")
        if ($null -eq $resource) { throw "Compiled PRI is missing resource: $key" }
        $candidates = @($resource.SelectNodes('Candidate'))
        if ($candidates.Count -ne $languages.Count) { throw "Compiled PRI has the wrong candidate count: $key" }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($candidate in $candidates) {
            $qualifier = $candidate.SelectSingleNode("QualifierSet/Qualifier[@name='Language']")
            if ($null -eq $qualifier) { throw "Compiled candidate has no Language qualifier: $key" }
            $language = $languages | Where-Object { $_ -ieq $qualifier.GetAttribute('value') } | Select-Object -First 1
            if (-not $language -or -not $seen.Add($language)) { throw "Duplicate/unsupported PRI language: $key" }
            if ($candidate.GetAttribute('type') -ne 'String' -or
                $candidate.SelectSingleNode('Value').InnerText -cne $catalogs[$language][$key]) {
                throw "Compiled PRI value differs from translated source: $language/$key"
            }
            if ($language -eq 'en-US' -and [double]$qualifier.GetAttribute('scoreAsDefault') -ne 1) {
                throw "Canonical English is not the compiled default: $key"
            }
            $counts[$language]++
        }
    }
    $manifestLanguages = @()
    $packageEvidence = $null
    if ($PackagePri -and -not $ManifestPath) { throw 'Package PRI validation requires its manifest identity.' }
    if ($ManifestPath) {
        $manifest = New-Object Xml.XmlDocument
        $manifest.Load((Resolve-Path -LiteralPath $ManifestPath).Path)
        $manifestLanguages = @($manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Resources']/*[local-name()='Resource']") |
            ForEach-Object { $_.GetAttribute('Language') } | Sort-Object)
        if (($manifestLanguages -join '|') -cne (($languages | Sort-Object) -join '|')) {
            throw 'Manifest languages do not exactly match the compiled 22-language set.'
        }
        if (-not $PackagePri) { throw 'Manifest localization requires the package-root resources.pri, independently of the application PRI.' }
        $PackagePri = (Resolve-Path -LiteralPath $PackagePri).Path
        if ([IO.Path]::GetFileName($PackagePri) -cne 'resources.pri' -or
            [IO.Path]::GetDirectoryName($PackagePri) -ine [IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $ManifestPath).Path)) {
            throw 'Shell resource index must be resources.pri beside AppxManifest.xml.'
        }
        $packageName = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']").GetAttribute('Name')
        $resourceUri = "ms-resource://$packageName/Resources/AppDescription"
        $propertiesDescription = $manifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Properties']/*[local-name()='Description']")
        $visualElements = $manifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']/*[local-name()='VisualElements']")
        if ($null -eq $propertiesDescription -or $propertiesDescription.InnerText -cne $resourceUri -or $visualElements.Count -eq 0) {
            throw 'Package description must reference its package-identity resource URI.'
        }
        foreach ($visual in $visualElements) {
            if ($visual.GetAttribute('Description') -cne $resourceUri) { throw 'VisualElements description must reference its package-identity resource URI.' }
        }
        & $makepri dump /if $PackagePri /of $packageDumpPath /dt detailed /o | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'MakePri could not inspect the package resource index.' }
        $packageDump = New-Object Xml.XmlDocument
        $packageDump.Load($packageDumpPath)
        $maps = @($packageDump.SelectNodes('//ResourceMap'))
        if ($maps.Count -ne 1 -or $maps[0].GetAttribute('primary') -ne 'true' -or $maps[0].GetAttribute('name') -cne $packageName) {
            throw 'Package PRI primary resource map does not match the manifest package identity.'
        }
        $resources = @($maps[0].SelectNodes('.//NamedResource'))
        if ($resources.Count -ne 1 -or $resources[0].GetAttribute('uri') -cne $resourceUri) {
            throw 'Package PRI must contain the Shell description URI under Resources/AppDescription.'
        }
        $candidates = @($resources[0].SelectNodes('Candidate'))
        if ($candidates.Count -ne $languages.Count) { throw 'Package description must have all 22 language candidates.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($candidate in $candidates) {
            $qualifiers = @($candidate.SelectNodes('QualifierSet/Qualifier'))
            if ($qualifiers.Count -ne 1 -or $qualifiers[0].GetAttribute('name') -ne 'Language') {
                throw 'Package description must be qualified only by Language.'
            }
            $qualifier = $qualifiers[0]
            $language = $languages | Where-Object { $_ -ieq $qualifier.GetAttribute('value') } | Select-Object -First 1
            if (-not $language -or -not $seen.Add($language)) { throw 'Duplicate/unsupported package description language.' }
            if ($candidate.GetAttribute('type') -ne 'String' -or
                $candidate.SelectSingleNode('Value').InnerText -cne $catalogs[$language]['AppDescription']) {
                throw "Package description differs from translated source: $language"
            }
            $expectedDefault = if ($language -eq 'en-US') { 1 } else { 0 }
            if ([double]$qualifier.GetAttribute('scoreAsDefault') -ne $expectedDefault) {
                throw 'Package description must use canonical English as its only default.'
            }
        }
        if (-not ('RedrainCast.PackageResourceProbe' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace RedrainCast {
    public static class PackageResourceProbe {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int SHLoadIndirectString(string source, StringBuilder output, uint capacity, IntPtr reserved);
    }
}
'@
        }
        $buffer = New-Object Text.StringBuilder 4096
        $hr = [RedrainCast.PackageResourceProbe]::SHLoadIndirectString(("@{$PackagePri`?$resourceUri}"), $buffer, 4096, [IntPtr]::Zero)
        $resolvedLanguages = @($languages | Where-Object { $catalogs[$_]['AppDescription'] -ceq $buffer.ToString() })
        if ($hr -ne 0 -or $resolvedLanguages.Count -eq 0) { throw "Shell PRI-path description lookup failed or returned an unknown value: HRESULT=$hr" }
        $negativeBuffer = New-Object Text.StringBuilder 4096
        $negativeHr = [RedrainCast.PackageResourceProbe]::SHLoadIndirectString(("@{$PackagePri`?$resourceUri/DefinitelyMissing}"), $negativeBuffer, 4096, [IntPtr]::Zero)
        if ($negativeHr -eq 0) { throw 'Shell PRI-path negative lookup unexpectedly succeeded.' }
        $packageEvidence = [ordered]@{
            pri = $PackagePri
            priSha256 = (Get-FileHash -LiteralPath $PackagePri -Algorithm SHA256).Hash
            primaryMap = $packageName
            descriptionUri = $resourceUri
            exactCompiledValuesChecked = $candidates.Count
            compiledDefaultLanguage = 'en-US'
            shellPriPathHResult = $hr
            shellResolvedLanguages = $resolvedLanguages
            shellMissingResourceHResult = $negativeHr
            scope = 'Documented SHLoadIndirectString absolute PRI-path lookup; no installed-package or Store acceptance claim.'
        }
    }
    $report = [ordered]@{
        passed = $true
        pri = $Pri
        priSha256 = (Get-FileHash -LiteralPath $Pri -Algorithm SHA256).Hash
        languages = $languages
        keysPerLanguage = $keys.Count
        exactCompiledValuesChecked = $keys.Count * $languages.Count
        compiledDefaultLanguage = 'en-US'
        compiledCandidateCounts = $counts
        sourceCatalogSha256 = $hashes
        manifestLanguages = $manifestLanguages
        packageResources = $packageEvidence
        validation = 'Every actual compiled Language candidate and Unicode value equals its source catalog; no file-count proxy.'
    }
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-Output "Compiled localization verified: $($languages.Count) languages, $($keys.Count) keys each, $($report.exactCompiledValuesChecked) exact values."
} finally {
    if (Test-Path -LiteralPath $dumpPath) { Remove-Item -LiteralPath $dumpPath -Force }
    if (Test-Path -LiteralPath $packageDumpPath) { Remove-Item -LiteralPath $packageDumpPath -Force }
}
