[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$DestinationPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Keep source and output paths constrained so this generator cannot overwrite arbitrary files.
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$englishResourcePath = [IO.Path]::Combine($repositoryRoot, "FeedCustomizer", "Strings", "en-US", "Resources.resw")
$pseudoResourcePath = [IO.Path]::Combine($repositoryRoot, "FeedCustomizer", "Strings", "qps-ploc", "Resources.resw")
$repositoryRootPrefix = $repositoryRoot + [IO.Path]::DirectorySeparatorChar
$intermediateMarker = [IO.Path]::DirectorySeparatorChar + "obj" + [IO.Path]::DirectorySeparatorChar

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = $englishResourcePath
}

if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
    $DestinationPath = $pseudoResourcePath
}

$sourceFullPath = [IO.Path]::GetFullPath($SourcePath)
$destinationFullPath = [IO.Path]::GetFullPath($DestinationPath)

if (-not [StringComparer]::OrdinalIgnoreCase.Equals($sourceFullPath, $englishResourcePath)) {
    throw "Pseudo-localization source must be the default English resource: $englishResourcePath"
}

if (-not [StringComparer]::OrdinalIgnoreCase.Equals($destinationFullPath, $pseudoResourcePath) -and
    ($destinationFullPath.IndexOf($intermediateMarker, [StringComparison]::OrdinalIgnoreCase) -lt 0 -or
     -not $destinationFullPath.StartsWith($repositoryRootPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw "Pseudo-localization output must be the controlled test resource or a repository obj directory: $destinationFullPath"
}

if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "English resource file was not found: $sourceFullPath"
}

# Preserve placeholders and opaque values so the generated file remains a faithful UI-only test.
$protectedTokenPattern = '\{\{|\}\}|\{[0-9]+(?:,-?[0-9]+)?(?::[^{}]+)?\}|https?://[^\s]+|(?:[A-Za-z]:\\|\\\\)[^\r\n]*|[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}'

function Convert-PseudoSegment {
    param([string]$Value)

    # ASCII markers keep this script stable under Windows PowerShell 5.1 without a BOM.
    # qps-ploca will cover font and non-Latin glyph validation separately.
    return $Value
}

function Convert-ToPseudoLocalizedText {
    param([string]$Value)

    $builder = [Text.StringBuilder]::new()
    $currentIndex = 0
    foreach ($match in [regex]::Matches($Value, $protectedTokenPattern)) {
        [void]$builder.Append((Convert-PseudoSegment $Value.Substring($currentIndex, $match.Index - $currentIndex)))
        [void]$builder.Append($match.Value)
        $currentIndex = $match.Index + $match.Length
    }

    [void]$builder.Append((Convert-PseudoSegment $Value.Substring($currentIndex)))

    $unprotectedLength = [regex]::Replace($Value, $protectedTokenPattern, "").Length
    $paddingLength = [Math]::Max(3, [Math]::Ceiling($unprotectedLength * 0.4))
    return "[[$($builder.ToString())$([string]::new('~', $paddingLength))]]"
}

$document = [xml](Get-Content -LiteralPath $sourceFullPath -Raw -Encoding UTF8)
foreach ($data in @($document.root.data)) {
    if ($null -eq $data.value) {
        continue
    }

    # This value selects packaged document language; keep documents on the English fallback.
    if ($data.name -eq "LanguageTag") {
        $data.value = "en-US"
        continue
    }

    $data.value = Convert-ToPseudoLocalizedText $data.value
}

$destinationDirectory = Split-Path -Parent $destinationFullPath
[IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = "`n"
$settings.NewLineHandling = [Xml.NewLineHandling]::Replace

$writer = [Xml.XmlWriter]::Create($destinationFullPath, $settings)
try {
    $document.Save($writer)
}
finally {
    $writer.Dispose()
}
