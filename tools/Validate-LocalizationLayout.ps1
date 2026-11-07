param(
    [Parameter(Mandatory = $true)][string]$FeedbackPath,
    [Parameter(Mandatory = $true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run this font-metric validation in an STA PowerShell process.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($ReportPath)) { $ReportPath = Join-Path (Get-Location) $ReportPath }
if (Test-Path -LiteralPath $ReportPath) { throw 'Use a new report path; earlier evidence is immutable.' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$feedback = Get-Content -LiteralPath $FeedbackPath -Raw -Encoding UTF8 | ConvertFrom-Json
$catalogs = @{}
$measurements = @()
$failures = @()
foreach ($entry in $feedback) {
    if (-not $catalogs.ContainsKey($entry.locale)) {
        $document = New-Object Xml.XmlDocument
        $document.Load((Join-Path $root "src\RedrainCast.App\Strings\$($entry.locale)\Resources.resw"))
        $values = @{}
        foreach ($data in $document.DocumentElement.SelectNodes('data')) { $values[$data.GetAttribute('name')] = $data.SelectSingleNode('value').InnerText }
        $catalogs[$entry.locale] = $values
    }
    # This first-sample KPI was reported by its stable cell ID rather than its unavailable-value resource key.
    $key = if ($entry.key -eq 'recentSkippedPercent') { 'NotAvailable' } else { $entry.key }
    $value = $catalogs[$entry.locale][$key]
    if ([string]::IsNullOrWhiteSpace($value)) { throw "Feedback key missing: $($entry.locale)/$key" }
    $culture = [Globalization.CultureInfo]::GetCultureInfo($entry.locale)
    $flow = if ($culture.TextInfo.IsRightToLeft) { [Windows.FlowDirection]::RightToLeft } else { [Windows.FlowDirection]::LeftToRight }
    foreach ($weight in @([Windows.FontWeights]::Normal, [Windows.FontWeights]::SemiBold)) {
        $typeface = New-Object Windows.Media.Typeface(
            (New-Object Windows.Media.FontFamily('Segoe UI')), [Windows.FontStyles]::Normal, $weight, [Windows.FontStretches]::Normal)
        $formatted = New-Object Windows.Media.FormattedText(
            $value, $culture, $flow, $typeface, [double]$entry.fontDip, [Windows.Media.Brushes]::Black)
        $budget = [double]$entry.availableWidthDip - 3
        $passed = $formatted.WidthIncludingTrailingWhitespace -le $budget
        $measurement = [pscustomobject]@{
            locale = $entry.locale; key = $key; nativeFieldId = $entry.key; context = $entry.context
            text = $value; font = 'Segoe UI'; weight = $weight.ToString(); fontDip = $entry.fontDip
            measuredWidthDip = $formatted.WidthIncludingTrailingWhitespace
            measuredHeightDip = $formatted.Height; availableWidthDip = $entry.availableWidthDip
            safetyMarginDip = 3; passed = $passed
        }
        $measurements += $measurement
        if (-not $passed) { $failures += "$($entry.locale)/$($entry.key): $($formatted.WidthIncludingTrailingWhitespace) > $budget DIP" }
    }
}
$report = [ordered]@{
    passed = $failures.Count -eq 0
    renderer = 'WPF FormattedText using installed Segoe UI and normal font fallback; not a native WinUI rerun.'
    nativeSlotSource = (Resolve-Path -LiteralPath $FeedbackPath).Path
    nativeSlotSourceSha256 = (Get-FileHash -LiteralPath $FeedbackPath -Algorithm SHA256).Hash
    correctedOverflowsChecked = $feedback.Count
    measurements = $measurements
    failures = $failures
}
New-Item -ItemType Directory -Path (Split-Path -Parent $ReportPath) -Force | Out-Null
[IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
if ($failures.Count) { throw ('Corrected translations exceed their measured native slots: ' + ($failures -join '; ')) }
Write-Output "Corrected font metrics passed: $($feedback.Count) native overflow slots, $($measurements.Count) Regular/SemiBold measurements; no UI or capture."
