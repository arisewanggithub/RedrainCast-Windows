Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Repository = 'arisewanggithub/RedrainCast-Windows'
$script:Product = '9P1153MP6R32'
$script:Api = 'https://manage.devcenter.microsoft.com/v1.0/my'

function Assert-StoreRun {
    param($Run, [string]$RunId, [string]$WorkflowPath)
    if ($RunId -notmatch '^[1-9][0-9]*$' -or [string]$Run.id -cne $RunId -or
        $Run.repository.full_name -cne $script:Repository -or
        $Run.head_repository.full_name -cne $script:Repository -or
        $Run.head_branch -cne 'main' -or $Run.status -cne 'completed' -or
        $Run.conclusion -cne 'success' -or $Run.event -cnotin @('push', 'workflow_dispatch') -or
        $Run.path -cne $WorkflowPath -or [string]$Run.head_sha -notmatch '^[a-f0-9]{40}$' -or
        [string]$Run.run_attempt -notmatch '^[1-9][0-9]*$' -or
        [string]$Run.run_number -notmatch '^[1-9][0-9]*$') {
        throw 'Source must be a successful, completed main run of the expected workflow in this repository (not a PR or fork).'
    }
}

function Get-StoreRun {
    param([string]$RunId, [string]$WorkflowPath)
    if ($RunId -notmatch '^[1-9][0-9]*$') { throw 'Run ID must be a positive integer.' }
    $raw = & gh api "repos/$script:Repository/actions/runs/$RunId" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read source workflow run.' }
    $run = $raw -join "`n" | ConvertFrom-Json
    Assert-StoreRun $run $RunId $WorkflowPath
    return $run
}

function Receive-StoreArtifact {
    param([string]$RunId, [string]$Name, [string]$Destination)
    & gh run download $RunId --repo $script:Repository --name $Name --dir $Destination 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Required private Actions artifact is missing, expired, or could not be downloaded.' }
}

function ConvertTo-StoreCanonicalJson {
    param($Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $keys = @(if ($Value -is [Collections.IDictionary]) { $Value.Keys } else { $Value.PSObject.Properties | ForEach-Object { $_.Name } })
        $keys = [string[]]$keys
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        $pairs = foreach ($key in $keys) {
            if ($Value -is [Collections.IDictionary]) { $child = $Value[$key] }
            else { $child = $Value.PSObject.Properties[$key].Value }
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-StoreCanonicalJson $child)
        }
        return '{' + ($pairs -join ',') + '}'
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $items = foreach ($item in $Value) { ConvertTo-StoreCanonicalJson $item }
        return '[' + ($items -join ',') + ']'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}

function Get-StoreHash {
    param([string]$Text)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-StoreMetadataFingerprint {
    param($Submission, [string[]]$UploadedImageNames = @())
    # Only transport/ingestion fields are volatile. Unknown metadata is included, failing closed on changes.
    $metadata = [ordered]@{}
    foreach ($property in $Submission.PSObject.Properties) {
        if ($property.Name -cnotin @('id', 'status', 'statusDetails', 'fileUploadUrl', 'applicationPackages')) {
            $metadata[$property.Name] = $property.Value
        }
    }
    if ($UploadedImageNames.Count) {
        $listings = ConvertTo-Json -InputObject $Submission.listings -Depth 100 -WarningAction Stop | ConvertFrom-Json
        foreach ($listing in $listings.PSObject.Properties) {
            $images = $listing.Value.baseListing.PSObject.Properties['images']
            if ($null -eq $images) { continue }
            foreach ($image in $images.Value) {
                if ($image.fileName -cin $UploadedImageNames) {
                    $image.PSObject.Properties.Remove('id')
                    $image.PSObject.Properties.Remove('fileStatus')
                }
            }
        }
        $metadata['listings'] = $listings
    }
    return Get-StoreHash (ConvertTo-StoreCanonicalJson $metadata)
}

function Get-StoreListingLocales {
    return [ordered]@{
        'ar' = 'ar'; 'de' = 'de'; 'en-US' = 'en-us'; 'es' = 'es'
        'fr' = 'fr'; 'hi' = 'hi'; 'id' = 'id'; 'it' = 'it'
        'ja' = 'ja'; 'ko' = 'ko'; 'ms' = 'ms'; 'nl' = 'nl'
        'pl' = 'pl'; 'pt-BR' = 'pt-br'; 'ru' = 'ru'; 'sv' = 'sv'
        'th' = 'th'; 'tr' = 'tr'; 'uk' = 'uk'; 'vi' = 'vi'
        'zh-Hans' = 'zh-hans'; 'zh-Hant' = 'zh-hant'
    }
}

function Assert-StoreListingApproval {
    param([string]$Confirmation, [string]$SourceSha, [string]$UploadRunId)
    if ($SourceSha -cnotmatch '^[a-f0-9]{40}$' -or $Confirmation -cne $SourceSha -or
        $UploadRunId -notmatch '^[1-9][0-9]*$') {
        throw 'Listing upload requires the exact reviewed main checkout commit SHA and a valid workflow run ID.'
    }
}

function Read-StoreListings {
    param([string]$RepositoryRoot, [string]$SourceSha, [string]$UploadRunId)
    Assert-StoreListingApproval $SourceSha $SourceSha $UploadRunId
    $folder = Join-Path $RepositoryRoot 'docs\store\listings'
    $locales = Get-StoreListingLocales
    $drafts = @(Get-ChildItem -LiteralPath $folder -Filter '*.json' -File)
    if ($drafts.Count -ne $locales.Count) { throw 'Exactly 22 supported listing drafts are required.' }
    $mapping = [ordered]@{
        productName = 'title'; description = 'description'; whatsNew = 'releaseNotes'
        productFeatures = 'features'; shortTitle = 'shortTitle'; voiceTitle = 'voiceTitle'
        shortDescription = 'shortDescription'; keywords = 'keywords'
        copyrightAndTrademarkInfo = 'copyrightAndTrademarkInfo'
        additionalLicenseTerms = 'licenseTerms'; developedBy = 'devStudio'
    }
    $limits = @{
        productName = 256; description = 10000; whatsNew = 1500; shortTitle = 50
        voiceTitle = 255; shortDescription = 270; copyrightAndTrademarkInfo = 200
        additionalLicenseTerms = 10000; developedBy = 255
    }
    Add-Type -AssemblyName System.Drawing
    $entries = @()
    $files = @()
    foreach ($locale in $locales.Keys) {
        $path = Join-Path $folder "$locale.json"
        $draft = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($draft.locale -cne $locale -or $draft.productName -cne 'RedrainCast') {
            throw "Listing identity or locale is invalid: $locale."
        }
        $allowed = @($mapping.Keys) + @('locale', 'screenshots', 'submissionReview')
        foreach ($property in $draft.PSObject.Properties) {
            if ($property.Name -cnotin $allowed) { throw "Unsupported draft field in $locale." }
        }
        $fields = [ordered]@{}
        foreach ($name in $mapping.Keys) {
            $property = $draft.PSObject.Properties[$name]
            if ($null -eq $property) { throw "Missing draft field in $locale." }
            $value = $property.Value
            if ($name -cin @('productFeatures', 'keywords')) {
                $maximum = if ($name -ceq 'keywords') { 7 } else { 8 }
                $itemLimit = if ($name -ceq 'keywords') { 40 } else { 200 }
                if ($value -isnot [array] -or $value.Count -lt 1 -or $value.Count -gt $maximum) {
                    throw "Invalid listing array in $locale."
                }
                foreach ($item in $value) {
                    if ($item -isnot [string] -or [string]::IsNullOrWhiteSpace($item) -or $item.Length -gt $itemLimit) {
                        throw "Invalid listing array entry in $locale."
                    }
                }
                if ($name -ceq 'keywords' -and @(($value -join ' ') -split '\s+' | Where-Object { $_ }).Count -gt 21) {
                    throw "Keyword word limit exceeded in $locale."
                }
            } else {
                if ($value -isnot [string] -or $value.Length -gt $limits[$name]) { throw "Invalid listing text in $locale." }
                if ([string]::IsNullOrWhiteSpace($value)) {
                    if ($name -cin @('productName', 'description', 'shortDescription', 'developedBy')) {
                        throw "Required listing text is empty in $locale."
                    }
                    # Empty optional first-release fields do not erase existing legal text or release notes.
                    continue
                }
            }
            $fields[$mapping[$name]] = $value
        }
        if ($draft.screenshots -isnot [array] -or $draft.screenshots.Count -ne 2) {
            throw "Exactly two approved desktop screenshots are required in $locale."
        }
        $images = @()
        for ($index = 0; $index -lt 2; $index++) {
            $shot = $draft.screenshots[$index]
            $name = @('01-home.png', '02-sharing-settings.png')[$index]
            $expected = "../../../assets/store/screenshots/$locale/$name"
            if ($shot.file -cne $expected -or $shot.deviceFamily -cne 'Desktop' -or
                $shot.caption -isnot [string] -or [string]::IsNullOrWhiteSpace($shot.caption) -or $shot.caption.Length -gt 200) {
                throw "Screenshot path, device family or caption is invalid in $locale."
            }
            $imagePath = Join-Path $RepositoryRoot "assets\store\screenshots\$locale\$name"
            $file = Get-Item -LiteralPath $imagePath
            if ($file.Length -le 0 -or $file.Length -ge 50MB) { throw "Screenshot size is invalid in $locale." }
            $stream = [IO.File]::OpenRead($imagePath)
            try {
                $image = [Drawing.Image]::FromStream($stream, $true, $true)
                try {
                    if ($image.RawFormat.Guid -ne [Drawing.Imaging.ImageFormat]::Png.Guid -or
                        $image.Width -lt 1366 -or $image.Height -lt 768 -or
                        $image.Width -gt 3840 -or $image.Height -gt 2160) { throw "Screenshot format or dimensions are invalid in $locale." }
                } finally { $image.Dispose() }
            } finally { $stream.Dispose() }
            $uploadName = "listing-$UploadRunId-$locale-$name"
            $images += [pscustomobject]@{
                fileName = $uploadName; fileStatus = 'PendingUpload'; description = $shot.caption; imageType = 'Screenshot'
            }
            $files += [pscustomobject]@{
                locale = $locales[$locale]; fileName = $uploadName; path = $imagePath
                sha256 = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        $entries += [pscustomobject]@{ locale = $locales[$locale]; fields = [pscustomobject]$fields; images = $images }
    }
    $projection = @($files | ForEach-Object {
        [pscustomobject]@{ locale = $_.locale; fileName = $_.fileName; sha256 = $_.sha256 }
    })
    return [pscustomobject]@{
        sourceSha = $SourceSha; uploadRunId = $UploadRunId; entries = $entries; files = $files; images = $projection
        contentFingerprint = Get-StoreHash (ConvertTo-StoreCanonicalJson ([ordered]@{ entries = $entries; images = $projection }))
    }
}

function Set-StoreDraftListings {
    param($Submission, $ListingUpdate)
    foreach ($entry in $ListingUpdate.entries) {
        $match = @($Submission.listings.PSObject.Properties | Where-Object { $_.Name -ieq $entry.locale })
        if ($match.Count -gt 1) { throw 'Ambiguous Store listing language keys.' }
        if ($match.Count -eq 0) {
            $listing = [pscustomobject]@{ baseListing = [pscustomobject]@{ images = @() }; platformOverrides = [pscustomobject]@{} }
            $Submission.listings | Add-Member -NotePropertyName $entry.locale -NotePropertyValue $listing
        } else {
            $listing = $match[0].Value
            if ($listing.baseListing -isnot [pscustomobject]) { throw 'Unexpected Store base listing schema.' }
        }
        foreach ($property in $entry.fields.PSObject.Properties) {
            $listing.baseListing | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
        }
        $imagesProperty = $listing.baseListing.PSObject.Properties['images']
        $oldImages = @(if ($null -ne $imagesProperty) { $imagesProperty.Value })
        foreach ($image in $oldImages) {
            if ($image.fileName -cin @($entry.images.fileName)) { throw 'New screenshot name conflicts with an existing Store image.' }
            if ($image.imageType -ceq 'Screenshot') { $image.fileStatus = 'PendingDelete' }
        }
        $listing.baseListing | Add-Member -NotePropertyName images -NotePropertyValue (@($oldImages) + @($entry.images)) -Force
    }
}

function Assert-StoreListingImages {
    param($Submission, $Images)
    foreach ($expected in $Images) {
        $listing = @($Submission.listings.PSObject.Properties | Where-Object { $_.Name -ieq $expected.locale })
        if ($listing.Count -ne 1) { throw 'Uploaded screenshot listing language changed.' }
        $match = @($listing[0].Value.baseListing.images | Where-Object { $_.fileName -ceq $expected.fileName })
        if ($match.Count -ne 1 -or $match[0].imageType -cne 'Screenshot' -or
            $match[0].fileStatus -cnotin @('PendingUpload', 'Uploaded')) {
            throw 'Uploaded screenshot name, type or ingestion state changed.'
        }
    }
}

function Get-StoreActivePackageFingerprint {
    param($Submission)
    $packages = @(foreach ($package in $Submission.applicationPackages) {
        if ($package.fileStatus -ceq 'PendingDelete') { continue }
        $fields = [ordered]@{}
        foreach ($property in $package.PSObject.Properties) {
            if ($property.Name -cne 'fileStatus') { $fields[$property.Name] = $property.Value }
        }
        $fields
    })
    return Get-StoreHash (ConvertTo-StoreCanonicalJson $packages)
}

function Read-StorePackage {
    param([string]$Path, [string]$Architecture, $Identity)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in @('AppxManifest.xml', 'AppxBlockMap.xml', '[Content_Types].xml',
            'resources.pri', 'RedrainCast.App.pri', 'RedrainCast.App.exe', 'RedrainCast.App.dll', 'coreclr.dll',
            'capture-worker/RedrainCast.CaptureWorker.exe', 'capture-worker/RedrainCast.CaptureWorker.dll',
            'capture-worker/coreclr.dll', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt')) {
            if (@($names | Where-Object { $_ -ceq $required }).Count -ne 1) { throw 'MSIX is missing a required shipping file or has duplicate entries.' }
        }
        $forbidden = @($names | Where-Object {
            $_ -match '(?i)\.pdb$|(^|/)(CaptureProbe|ffmpeg|ffprobe|RecordExport)([^/]*)(\.exe|\.dll)$' -and
            $_ -cnotin @('FFmpeg.AutoGen.dll', 'capture-worker/FFmpeg.AutoGen.dll')
        })
        if ($forbidden.Count) {
            throw "MSIX contains debug symbols or forbidden standalone helpers: $($forbidden -join ', ')"
        }
        $reader = New-Object IO.StreamReader($archive.GetEntry('AppxManifest.xml').Open())
        try { $xmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $xmlReader = [Xml.XmlReader]::Create((New-Object IO.StringReader($xmlText)), $settings)
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        try { $document.Load($xmlReader) } finally { $xmlReader.Dispose() }
        $package = $document.DocumentElement
        if ($package.LocalName -cne 'Package' -or $package.NamespaceURI -cne 'http://schemas.microsoft.com/appx/manifest/foundation/windows10') {
            throw 'Unexpected MSIX manifest schema.'
        }
        $ns = New-Object Xml.XmlNamespaceManager($document.NameTable)
        $ns.AddNamespace('p', $package.NamespaceURI)
        $id = $document.SelectSingleNode('/p:Package/p:Identity', $ns)
        if ($null -eq $id -or $id.Name -cne $Identity.PackageName -or $id.Publisher -cne $Identity.Publisher -or
            $id.ProcessorArchitecture -cne $Architecture -or $id.Version -notmatch '^\d+\.\d+\.\d+\.0$') {
            throw 'MSIX package identity, publisher, architecture, or zero-revision version is incorrect.'
        }
        $version = [version]$id.Version
        if ($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535) { throw 'Invalid MSIX version range.' }
        $resources = $document.SelectSingleNode('/p:Package/p:Resources', $ns)
        $dependencies = $document.SelectSingleNode('/p:Package/p:Dependencies', $ns)
        $applications = $document.SelectSingleNode('/p:Package/p:Applications', $ns)
        $capabilities = $document.SelectSingleNode('/p:Package/p:Capabilities', $ns)
        if ($null -eq $resources -or $null -eq $dependencies -or $null -eq $applications -or $null -eq $capabilities) {
            throw 'Incomplete MSIX manifest.'
        }
        # SDK-generated BCP47 casing is not an architecture metadata difference.
        foreach ($resource in $resources.SelectNodes('p:Resource[@Language]', $ns)) {
            $resource.SetAttribute('Language', $resource.GetAttribute('Language').ToLowerInvariant())
        }
        return [pscustomobject]@{
            fileName = "RedrainCast-$Architecture.msix"
            sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
            version = $id.Version
            architecture = $Architecture
            manifestFingerprint = Get-StoreHash ($resources.OuterXml + $dependencies.OuterXml + $applications.OuterXml + $capabilities.OuterXml)
        }
    } finally { $archive.Dispose() }
}

function Assert-StoreArchiveLength {
    param([long]$Length)
    if ($Length -le 0) { throw 'Submission ZIP must not be empty.' }
    if ($Length -gt 1GB) { throw 'Submission ZIP exceeds the 1 GiB single-upload limit. Use manual Partner Center upload.' }
}

function New-StorePackageArchive {
    param([string]$Directory, $Identity, $ListingUpdate = $null)
    $packages = @()
    $files = @()
    foreach ($arch in @('arm64', 'x64')) {
        $folder = Join-Path $Directory $arch
        $found = @(Get-ChildItem -LiteralPath $folder -Filter '*.msix' -Recurse -File)
        if ($found.Count -ne 1) { throw 'Exactly one MSIX is required in each architecture release download.' }
        $packages += Read-StorePackage $found[0].FullName $arch $Identity
        $files += $found[0].FullName
    }
    if ($packages[0].version -cne $packages[1].version -or $packages[0].manifestFingerprint -cne $packages[1].manifestFingerprint) {
        throw 'ARM64 and x64 versions and non-architecture manifest metadata must match.'
    }
    $path = Join-Path $Directory 'store-packages.zip'
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        for ($i = 0; $i -lt $files.Count; $i++) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $files[$i], $packages[$i].fileName)
        }
        if ($null -ne $ListingUpdate) {
            foreach ($image in $ListingUpdate.files) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $image.path, $image.fileName)
            }
        }
    } finally { $zip.Dispose() }
    if ($null -ne $ListingUpdate) {
        $uploaded = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            foreach ($image in $ListingUpdate.files) {
                $stream = $uploaded.GetEntry($image.fileName).Open()
                $sha = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                    if ($hash -cne $image.sha256) { throw 'Screenshot bytes changed after review validation; no Store call was made.' }
                } finally { $sha.Dispose(); $stream.Dispose() }
            }
        } finally { $uploaded.Dispose() }
    }
    # 1 GiB accommodates both already-compressed MSIX files, below Put Blob's 5,000 MiB limit (2019-12-12).
    Assert-StoreArchiveLength (Get-Item -LiteralPath $path).Length
    return [pscustomobject]@{ path = $path; packages = $packages; listingUpdate = $ListingUpdate }
}

function Get-StoreSafeErrors {
    param($Details)
    $known = @('InvalidRequest', 'InvalidRequestBody', 'InvalidParameter', 'InvalidParameterValue',
        'BadRequest', 'InvalidSubmission', 'InvalidApplicationPackage', 'InvalidFileStatus',
        'InvalidPublishMode', 'InvalidJson', 'Conflict', 'Forbidden', 'Unauthorized',
        'AuthenticationFailed', 'AuthorizationFailure', 'AuthorizationPermissionMismatch',
        'InvalidAuthenticationInfo', 'InvalidHeaderValue', 'MissingRequiredHeader',
        'InvalidBlobType', 'RequestBodyTooLarge', 'InvalidQueryParameterValue',
        'InvalidResourceName', 'ResourceNotFound', 'BlobNotFound', 'OperationNotAllowed')
    $codes = New-Object 'Collections.Generic.List[string]'
    foreach ($item in @($Details) | Select-Object -First 20) {
        if ($null -eq $item) { continue }
        $property = $item.PSObject.Properties['code']
        if ($null -eq $property) { continue }
        $code = [string]$property.Value
        $safe = if ($code -cin $known) { $code } else { 'UnrecognizedServiceError' }
        if (-not $codes.Contains($safe)) { $codes.Add($safe) }
    }
    return $codes.ToArray()
}

function Get-StoreTransportDiagnostic {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt 16384) { return 'Service details unavailable or suppressed.' }
    $details = @()
    try {
        if ($Text.TrimStart().StartsWith('{')) {
            $json = $Text | ConvertFrom-Json -ErrorAction Stop
            $details += $json
            foreach ($name in @('error', 'errors')) {
                $property = $json.PSObject.Properties[$name]
                if ($null -ne $property) { $details += @($property.Value) }
            }
        } elseif ($Text.TrimStart().StartsWith('<')) {
            $settings = New-Object Xml.XmlReaderSettings
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create((New-Object IO.StringReader($Text)), $settings)
            try {
                $xml = New-Object Xml.XmlDocument
                $xml.XmlResolver = $null
                $xml.Load($reader)
                $node = $xml.SelectSingleNode('/Error/Code')
                if ($null -ne $node) { $details += [pscustomobject]@{ code = $node.InnerText } }
            } finally { $reader.Dispose() }
        }
    } catch { return 'Service details unavailable or suppressed.' }
    $codes = @(Get-StoreSafeErrors $details)
    if (-not $codes.Count) { return 'Service details unavailable or suppressed.' }
    # Never echo free-form service messages, which can contain submitted metadata or signed URLs.
    return 'Service codes: ' + ($codes -join ', ') + '. Service messages suppressed.'
}

function Invoke-StoreHttp {
    param([ValidateSet('GET', 'POST', 'PUT')][string]$Method, [string]$Uri, [hashtable]$Headers,
        $Body, [string]$InFile, [string]$ContentType = 'application/json')
    $operation = 'Store API request'
    if ($InFile) { $operation = 'Azure archive upload' }
    elseif ($Uri -match '^https://login\.microsoftonline\.com/') { $operation = 'Authentication' }
    elseif ($Uri -match '/submissions/[0-9]+/commit$') { $operation = 'Store submission commit' }
    elseif ($Uri -match '/submissions/[0-9]+/status$') { $operation = 'Store submission status' }
    elseif ($Uri -match '/submissions/[0-9]+$') { $operation = 'Store submission metadata' }
    elseif ($Uri -match '/submissions$') { $operation = 'Store draft creation' }
    elseif ($Uri -match '/applications/[A-Z0-9]+$') { $operation = 'Store application lookup' }
    try {
        $arguments = @{ Method = $Method; Uri = $Uri; Headers = $Headers; ContentType = $ContentType;
            TimeoutSec = 600; MaximumRedirection = 0; ErrorAction = 'Stop'; Verbose = $false; Debug = $false }
        if ($InFile) {
            $arguments.InFile = $InFile
            [void](Invoke-WebRequest @arguments -UseBasicParsing)
            return
        }
        if ($null -ne $Body) { $arguments.Body = $Body }
        return Invoke-RestMethod @arguments
    } catch {
        $status = 'unavailable'
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            $status = [string][int]$responseProperty.Value.StatusCode
        }
        $text = ''
        if ($null -ne $_.ErrorDetails) { $text = [string]$_.ErrorDetails.Message }
        if (-not $text -and $null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            $stream = $null
            $reader = $null
            try {
                $stream = $responseProperty.Value.GetResponseStream()
                $reader = New-Object IO.StreamReader($stream)
                $buffer = New-Object char[] 16385
                $length = $reader.ReadBlock($buffer, 0, $buffer.Length)
                $text = New-Object string($buffer, 0, $length)
            } catch { $text = '' }
            finally {
                if ($null -ne $reader) { $reader.Dispose() }
                elseif ($null -ne $stream) { $stream.Dispose() }
            }
        }
        $diagnostic = Get-StoreTransportDiagnostic $text
        throw "Store transport failed ($operation, $Method, HTTP $status). $diagnostic No draft is automatically removed or retried."
    }
}

function Get-StoreToken {
    foreach ($name in @('PARTNER_CENTER_TENANT_ID', 'PARTNER_CENTER_CLIENT_ID')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        $parsed = [guid]::Empty
        if (-not [guid]::TryParse($value, [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw "Missing or invalid $name GUID." }
    }
    $secret = $env:PARTNER_CENTER_CLIENT_SECRET
    if ([string]::IsNullOrWhiteSpace($secret)) { throw 'Missing PARTNER_CENTER_CLIENT_SECRET value.' }
    $response = Invoke-StoreHttp POST "https://login.microsoftonline.com/$env:PARTNER_CENTER_TENANT_ID/oauth2/token" @{} @{
        grant_type = 'client_credentials'; resource = 'https://manage.devcenter.microsoft.com'
        client_id = $env:PARTNER_CENTER_CLIENT_ID; client_secret = $secret
    } '' 'application/x-www-form-urlencoded'
    if ([string]::IsNullOrWhiteSpace($response.access_token)) { throw 'Store authentication did not return an access token.' }
    return $response.access_token
}

function Assert-StoreApplication {
    param($Application, $Identity, [switch]$AllowUnpublished)
    if ($Application.id -cne $script:Product -or $Identity.StoreId -cne $script:Product -or
        $Application.packageIdentityName -cne $Identity.PackageName -or $Application.publisherName -cne $Identity.Publisher) {
        throw 'Partner Center application does not match StoreIdentity.json and the pinned Store product.'
    }
    $published = $Application.PSObject.Properties['lastPublishedApplicationSubmission']
    if ($null -eq $published -or $null -eq $published.Value -or
        [string]$published.Value.id -notmatch '^[1-9][0-9]*$') {
        if (-not $AllowUnpublished) { throw 'First publication must be completed manually in Partner Center. No Store mutation was performed.' }
    }
}

function Get-StoreReadiness {
    param($Identity, [scriptblock]$Request)
    $app = & $Request GET "applications/$script:Product" $null $null
    Assert-StoreApplication $app $Identity -AllowUnpublished
    $published = $app.PSObject.Properties['lastPublishedApplicationSubmission']
    $pending = $app.PSObject.Properties['pendingApplicationSubmission']
    if ($null -ne $published -and $null -ne $published.Value -and [string]$published.Value.id -notmatch '^[1-9][0-9]*$') {
        throw 'Unexpected published submission pointer in the read-only Store response.'
    }
    if ($null -ne $pending -and $null -ne $pending.Value -and [string]$pending.Value.id -notmatch '^[1-9][0-9]*$') {
        throw 'Unexpected pending submission pointer in the read-only Store response.'
    }
    $hasPublished = $null -ne $published -and $null -ne $published.Value -and
        [string]$published.Value.id -match '^[1-9][0-9]*$'
    $hasPending = $null -ne $pending -and $null -ne $pending.Value
    return [pscustomobject]@{
        applicationId = $script:Product; firstPublicationComplete = $hasPublished
        pendingDraftExists = $hasPending; automatedUpdateAllowed = $hasPublished -and -not $hasPending
    }
}

function Get-StoreDraftInspection {
    param($Identity, [string]$SubmissionId, [scriptblock]$Request)
    if ($SubmissionId -cnotmatch '^[1-9][0-9]{0,19}$') { throw 'Inspection requires an exact numeric submission ID.' }
    $app = & $Request GET "applications/$script:Product" $null $null
    Assert-StoreApplication $app $Identity -AllowUnpublished
    $pending = $app.PSObject.Properties['pendingApplicationSubmission']
    if ($null -eq $pending -or $null -eq $pending.Value -or [string]$pending.Value.id -cne $SubmissionId) {
        throw 'Exact inspection ID does not match the current pending submission; no draft was changed.'
    }
    $draft = & $Request GET "applications/$script:Product/submissions/$SubmissionId" $null $null
    if ([string]$draft.id -cne $SubmissionId) { throw 'Inspection response ID mismatch.' }
    $packages = @()
    foreach ($package in @($draft.applicationPackages) | Select-Object -First 20) {
        $row = [ordered]@{}
        foreach ($field in @('fileName', 'fileStatus', 'version', 'architecture')) {
            $property = $package.PSObject.Properties[$field]
            $value = if ($null -ne $property) { [string]$property.Value } else { '' }
            $allowed = switch ($field) {
                'fileName' { $value -cmatch '^RedrainCast-(arm64|x64)\.msix$' }
                'fileStatus' { $value -cin @('None', 'PendingUpload', 'Uploaded', 'PendingDelete') }
                'version' { $value -cmatch '^[0-9]{1,5}(\.[0-9]{1,5}){3}$' }
                'architecture' { $value -cin @('arm64', 'x64', 'x86', 'neutral', 'ARM64', 'X64', 'X86', 'Neutral') }
            }
            $row[$field] = if ($allowed) { $value } else { 'UnknownOrSuppressed' }
        }
        $packages += [pscustomobject]$row
    }
    $errors = @()
    $details = $draft.PSObject.Properties['statusDetails']
    if ($null -ne $details -and $null -ne $details.Value) {
        $property = $details.Value.PSObject.Properties['errors']
        if ($null -ne $property) { $errors = @(Get-StoreSafeErrors $property.Value) }
    }
    $status = if ($draft.status -cin @('PendingCommit', 'CommitStarted', 'CommitFailed', 'PreProcessing',
        'PreProcessingFailed', 'Certification', 'CertificationFailed', 'Release', 'ReleaseFailed',
        'PendingPublication', 'Publishing', 'PublishFailed', 'Published')) { $draft.status } else { 'UnknownOrSuppressed' }
    $mode = if ($draft.targetPublishMode -cin @('Immediate', 'Manual', 'SpecificDate')) { $draft.targetPublishMode } else { 'UnknownOrSuppressed' }
    return [pscustomobject]@{
        submissionId = $SubmissionId; status = $status; targetPublishMode = $mode
        ownership = 'Unknown'; applicationPackages = $packages; validationErrorCodes = $errors
    }
}

function Assert-StorePackages {
    param($Submission, $Packages, [string]$DeletedFingerprint)
    $active = @($Submission.applicationPackages | Where-Object { $_.fileStatus -cne 'PendingDelete' })
    $deleted = @($Submission.applicationPackages | Where-Object { $_.fileStatus -ceq 'PendingDelete' })
    if ($active.Count -ne 2 -or (Get-StoreHash (ConvertTo-StoreCanonicalJson $deleted)) -cne $DeletedFingerprint) {
        throw 'Draft package set differs from the API-owned replacement.'
    }
    foreach ($expected in $Packages) {
        $match = @($active | Where-Object { $_.fileName -ceq $expected.fileName })
        if ($match.Count -ne 1 -or $match[0].fileStatus -cnotin @('PendingUpload', 'Uploaded')) { throw 'Draft package name or upload state changed.' }
        foreach ($field in @('version', 'architecture')) {
            $property = $match[0].PSObject.Properties[$field]
            if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value) -and
                [string]$property.Value -ine [string]$expected.$field) { throw 'Draft package version or architecture changed.' }
        }
    }
}

function New-StoreDraft {
    param($Identity, $Archive, [string]$BuildRunId, [string]$BuildHeadSha, [scriptblock]$Request)
    $app = & $Request GET "applications/$script:Product" $null $null
    Assert-StoreApplication $app $Identity
    $pending = $app.PSObject.Properties['pendingApplicationSubmission']
    if ($null -ne $pending -and $null -ne $pending.Value) { throw 'An existing pending submission is preserved. Resolve it manually before starting another draft.' }
    $previousId = [string]$app.lastPublishedApplicationSubmission.id
    $draft = & $Request POST "applications/$script:Product/submissions" $null $null
    if ([string]$draft.id -notmatch '^[1-9][0-9]*$' -or $draft.status -cne 'PendingCommit') { throw 'Unexpected new draft response; inspect Partner Center manually.' }
    $id = [string]$draft.id
    foreach ($old in $draft.applicationPackages) { $old.fileStatus = 'PendingDelete' }
    $deleted = @($draft.applicationPackages)
    $deletedHash = Get-StoreHash (ConvertTo-StoreCanonicalJson $deleted)
    $newPackages = @($Archive.packages | ForEach-Object {
        [pscustomobject]@{ fileName = $_.fileName; fileStatus = 'PendingUpload'; minimumDirectXVersion = 'None'; minimumSystemRam = 'None' }
    })
    $draft.applicationPackages = @($deleted) + @($newPackages)
    $draft.targetPublishMode = 'Immediate'
    $listingUpdate = $Archive.listingUpdate
    $imageNames = @()
    if ($null -ne $listingUpdate) {
        Set-StoreDraftListings $draft $listingUpdate
        $imageNames = @($listingUpdate.images.fileName)
    }
    $metadataHash = Get-StoreMetadataFingerprint $draft $imageNames
    $sas = $null
    if (-not [uri]::TryCreate([string]$draft.fileUploadUrl, [UriKind]::Absolute, [ref]$sas) -or
        $sas.Scheme -cne 'https' -or $sas.Host -notmatch '^[a-z0-9-]+\.blob\.core\.windows\.net$' -or
        $sas.UserInfo -or $sas.Port -ne 443 -or -not $sas.Query) { throw 'Store supplied an unexpected upload destination; inspect the new draft manually.' }
    [void](& $Request PUT "applications/$script:Product/submissions/$id" $draft $null)
    [void](& $Request PUT $sas.AbsoluteUri $null $Archive.path)
    $saved = & $Request GET "applications/$script:Product/submissions/$id" $null $null
    if ($saved.id -cne $id -or $saved.status -cne 'PendingCommit' -or
        (Get-StoreMetadataFingerprint $saved $imageNames) -cne $metadataHash) { throw 'Server draft metadata differs from the preserved draft. Inspect manually; no certification was started.' }
    Assert-StorePackages $saved $Archive.packages $deletedHash
    if ($null -ne $listingUpdate) { Assert-StoreListingImages $saved $listingUpdate.images }
    $packageReceipt = @($Archive.packages | ForEach-Object {
        [pscustomobject]@{ fileName = $_.fileName; sha256 = $_.sha256; version = $_.version; architecture = $_.architecture }
    })
    $receipt = [pscustomobject]@{
        schemaVersion = 1; applicationId = $script:Product; submissionId = $id
        repository = $script:Repository; sourceBuildRunId = $BuildRunId; buildHeadSha = $BuildHeadSha
        priorLastPublishedId = $previousId; packages = $packageReceipt
        metadataFingerprint = $metadataHash; deletedPackagesFingerprint = $deletedHash
        activePackagesFingerprint = Get-StoreActivePackageFingerprint $saved; targetPublishMode = 'Immediate'
    }
    if ($null -ne $listingUpdate) {
        $receipt.schemaVersion = 2
        $receipt | Add-Member -NotePropertyName listingUpdate -NotePropertyValue ([pscustomobject]@{
            sourceSha = $listingUpdate.sourceSha; uploadRunId = $listingUpdate.uploadRunId
            contentFingerprint = $listingUpdate.contentFingerprint; images = $listingUpdate.images
        })
    }
    return $receipt
}

function Submit-StoreDraft {
    param($Identity, $Receipt, $Packages, [string]$BuildRunId, [string]$BuildHeadSha, [scriptblock]$Request)
    if ($Receipt.schemaVersion -notin @(1, 2) -or $Receipt.repository -cne $script:Repository -or
        $Receipt.applicationId -cne $script:Product -or [string]$Receipt.submissionId -notmatch '^[1-9][0-9]*$' -or
        $Receipt.sourceBuildRunId -cne $BuildRunId -or $Receipt.buildHeadSha -cne $BuildHeadSha -or
        $Receipt.targetPublishMode -cne 'Immediate' -or $Receipt.metadataFingerprint -notmatch '^[a-f0-9]{64}$' -or
        $Receipt.deletedPackagesFingerprint -notmatch '^[a-f0-9]{64}$' -or
        $Receipt.activePackagesFingerprint -notmatch '^[a-f0-9]{64}$') { throw 'Receipt identity or build binding is invalid.' }
    $imageNames = @()
    if ($Receipt.schemaVersion -eq 2) {
        $update = $Receipt.listingUpdate
        Assert-StoreListingApproval $update.sourceSha $update.sourceSha $update.uploadRunId
        if ($update.contentFingerprint -cnotmatch '^[a-f0-9]{64}$' -or @($update.images).Count -ne 44) {
            throw 'Listing receipt content binding is invalid.'
        }
        $locales = Get-StoreListingLocales
        foreach ($locale in $locales.Keys) {
            foreach ($name in @('01-home.png', '02-sharing-settings.png')) {
                $expectedName = "listing-$($update.uploadRunId)-$locale-$name"
                $match = @($update.images | Where-Object { $_.fileName -ceq $expectedName -and $_.locale -ceq $locales[$locale] })
                if ($match.Count -ne 1 -or $match[0].sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'Listing receipt screenshot binding is invalid.' }
            }
        }
        $imageNames = @($update.images.fileName)
    }
    $projection = @($Packages | ForEach-Object {
        [pscustomobject]@{ fileName = $_.fileName; sha256 = $_.sha256; version = $_.version; architecture = $_.architecture }
    })
    if ((ConvertTo-StoreCanonicalJson $projection) -cne (ConvertTo-StoreCanonicalJson $Receipt.packages)) { throw 'Downloaded build packages do not match the upload receipt.' }
    $app = & $Request GET "applications/$script:Product" $null $null
    Assert-StoreApplication $app $Identity
    if ($null -eq $app.pendingApplicationSubmission -or $app.pendingApplicationSubmission.id -cne $Receipt.submissionId -or
        $app.lastPublishedApplicationSubmission.id -cne $Receipt.priorLastPublishedId) { throw 'Pending or last-published submission no longer matches the upload receipt.' }
    $path = "applications/$script:Product/submissions/$($Receipt.submissionId)"
    $draft = & $Request GET $path $null $null
    if ($draft.id -cne $Receipt.submissionId -or $draft.status -cne 'PendingCommit' -or $draft.targetPublishMode -cne 'Immediate' -or
        (Get-StoreMetadataFingerprint $draft $imageNames) -cne $Receipt.metadataFingerprint) { throw 'Draft metadata changed or draft is no longer pending commit. Certification was not started.' }
    Assert-StorePackages $draft $Packages $Receipt.deletedPackagesFingerprint
    if ($Receipt.schemaVersion -eq 2) { Assert-StoreListingImages $draft $Receipt.listingUpdate.images }
    if ((Get-StoreActivePackageFingerprint $draft) -cne $Receipt.activePackagesFingerprint) {
        throw 'Active package metadata changed after upload. Certification was not started.'
    }
    # Recheck the application pointer immediately before commit; the API has no documented compare-and-swap.
    $currentApp = & $Request GET "applications/$script:Product" $null $null
    Assert-StoreApplication $currentApp $Identity
    if ($null -eq $currentApp.pendingApplicationSubmission -or $currentApp.pendingApplicationSubmission.id -cne $Receipt.submissionId -or
        $currentApp.lastPublishedApplicationSubmission.id -cne $Receipt.priorLastPublishedId) { throw 'Submission pointer changed before commit.' }
    [void](& $Request POST "$path/commit" $null $null)
    # Bounded read-only monitoring, not a certification/publishing success claim.
    $status = & $Request GET "$path/status" $null $null
    if ($status.status -in @('CommitFailed', 'PreProcessingFailed', 'CertificationFailed', 'ReleaseFailed', 'PublishFailed')) {
        throw 'Partner Center reports submission processing failure. Review it manually.'
    }
    if ($status.status -cnotin @('PendingCommit', 'CommitStarted', 'PreProcessing', 'Certification', 'Release',
        'PendingPublication', 'Publishing', 'Published')) { throw 'Unrecognized submission state; review Partner Center manually.' }
    return [string]$status.status
}

Export-ModuleMember -Function Assert-StoreRun, Get-StoreRun, Receive-StoreArtifact, ConvertTo-StoreCanonicalJson,
    Get-StoreHash, Get-StoreMetadataFingerprint, Get-StoreActivePackageFingerprint, Read-StorePackage,
    Assert-StoreArchiveLength, New-StorePackageArchive,
    Get-StoreListingLocales, Assert-StoreListingApproval, Read-StoreListings, Set-StoreDraftListings, Assert-StoreListingImages,
    Get-StoreSafeErrors, Get-StoreTransportDiagnostic, Get-StoreDraftInspection,
    Invoke-StoreHttp, Get-StoreToken, Assert-StoreApplication, Get-StoreReadiness, Assert-StorePackages, New-StoreDraft, Submit-StoreDraft
