[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 这些目录包含缓存或构建产物，不属于项目源文件；排除后脚本可安全重复执行。
$excludedDirectories = @('.git', '.vs', 'bin', 'obj')
$textExtensions = @(
    '', '.appxmanifest', '.config', '.cs', '.csproj', '.editorconfig', '.gitattributes',
    '.gitignore', '.html', '.json', '.manifest', '.md', '.ps1', '.props', '.resw', '.sln',
    '.slnx', '.targets', '.txt', '.xaml', '.xml'
)

function Test-TextFile {
    param([byte[]]$Bytes, [string]$Extension)

    if ($textExtensions -notcontains $Extension.ToLowerInvariant()) {
        return $false
    }

    # UTF-16 文本允许包含零字节；其他含零字节的文件按二进制处理，避免破坏图片等资源。
    $hasUtf16Bom = $Bytes.Length -ge 2 -and
        (($Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE) -or
         ($Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF))
    if ($hasUtf16Bom) {
        return $true
    }

    return -not ($Bytes -contains 0)
}

function Convert-ToCrLf {
    param([byte[]]$Bytes)

    $result = [System.Collections.Generic.List[byte]]::new()

    # UTF-16 的换行符由两个字节组成，必须按字符处理，不能套用单字节规则。
    $isUtf16LittleEndian = $Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFF -and $Bytes[1] -eq 0xFE
    $isUtf16BigEndian = $Bytes.Length -ge 2 -and $Bytes[0] -eq 0xFE -and $Bytes[1] -eq 0xFF
    if ($isUtf16LittleEndian -or $isUtf16BigEndian) {
        [void]$result.Add($Bytes[0])
        [void]$result.Add($Bytes[1])
        for ($index = 2; $index + 1 -lt $Bytes.Length; $index += 2) {
            if ($isUtf16LittleEndian) {
                $codeUnit = $Bytes[$index] -bor ($Bytes[$index + 1] -shl 8)
            } else {
                $codeUnit = ($Bytes[$index] -shl 8) -bor $Bytes[$index + 1]
            }

            if ($codeUnit -eq 0x0D -or $codeUnit -eq 0x0A) {
                $lineBreak = @(0x0D, 0x0A)
                foreach ($lineBreakUnit in $lineBreak) {
                    if ($isUtf16LittleEndian) {
                        [void]$result.Add([byte]$lineBreakUnit)
                        [void]$result.Add(0)
                    } else {
                        [void]$result.Add(0)
                        [void]$result.Add([byte]$lineBreakUnit)
                    }
                }
                if ($codeUnit -eq 0x0D -and $index + 3 -lt $Bytes.Length) {
                    if ($isUtf16LittleEndian) {
                        $nextCodeUnit = $Bytes[$index + 2] -bor ($Bytes[$index + 3] -shl 8)
                    } else {
                        $nextCodeUnit = ($Bytes[$index + 2] -shl 8) -bor $Bytes[$index + 3]
                    }
                    if ($nextCodeUnit -eq 0x0A) {
                        $index += 2
                    }
                }
                continue
            }

            [void]$result.Add($Bytes[$index])
            [void]$result.Add($Bytes[$index + 1])
        }
        if (($Bytes.Length - 2) % 2 -ne 0) {
            [void]$result.Add($Bytes[$Bytes.Length - 1])
        }
        return [byte[]]$result.ToArray()
    }

    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        switch ($Bytes[$index]) {
            0x0D {
                [void]$result.Add(0x0D)
                if ($index + 1 -lt $Bytes.Length -and $Bytes[$index + 1] -eq 0x0A) {
                    $index++
                }
                [void]$result.Add(0x0A)
            }
            0x0A {
                [void]$result.Add(0x0D)
                [void]$result.Add(0x0A)
            }
            default {
                [void]$result.Add($Bytes[$index])
            }
        }
    }

    return [byte[]]$result.ToArray()
}

function Test-ByteArrayEqual {
    param([byte[]]$Left, [byte[]]$Right)

    if ($Left.Length -ne $Right.Length) {
        return $false
    }

    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) {
            return $false
        }
    }

    return $true
}

$resolvedRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$convertedCount = 0
$skippedCount = 0

Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse -Force | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($resolvedRoot, $_.FullName)
    $pathParts = $relativePath -split '[\\/]'
    if ($pathParts | Where-Object { $excludedDirectories -contains $_ }) {
        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    if (-not (Test-TextFile -Bytes $bytes -Extension $_.Extension)) {
        $skippedCount++
        return
    }

    $convertedBytes = Convert-ToCrLf -Bytes $bytes
    if (-not (Test-ByteArrayEqual -Left $bytes -Right $convertedBytes)) {
        [System.IO.File]::WriteAllBytes($_.FullName, $convertedBytes)
        $convertedCount++
    }
}

Write-Output "已转换为 CRLF 的文本文件：$convertedCount 个"
Write-Output "跳过的非文本文件：$skippedCount 个"
