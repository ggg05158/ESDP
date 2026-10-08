[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$PackageRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function Get-NormalizedRoot([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
}

function Get-RelativePath([string]$Root, [string]$FullPath) {
    $prefix = $Root + [System.IO.Path]::DirectorySeparatorChar
    $normalized = [System.IO.Path]::GetFullPath($FullPath)
    if (-not $normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the project root: $normalized"
    }
    return $normalized.Substring($prefix.Length)
}

function Resolve-SafeChildPath([string]$Root, [string]$RelativePath) {
    $rootPath = Get-NormalizedRoot $Root
    $localRelative = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $fullPath = [System.IO.Path]::GetFullPath((Join-Path $rootPath $localRelative))
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe relative path in package: $RelativePath"
    }
    return $fullPath
}

function Select-ProjectFolder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the ESDP project folder that contains ESDP.csproj.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        return $null
    }
    return $dialog.SelectedPath
}

function Select-UpdatePackageFolder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'manifest.json과 Files 폴더가 들어 있는 ESDP 코드 업데이트 폴더를 선택하세요.'
    $dialog.ShowNewFolderButton = $false
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        return $null
    }
    return $dialog.SelectedPath
}

try {
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
        $localManifest = Join-Path $PSScriptRoot 'manifest.json'
        $localFiles = Join-Path $PSScriptRoot 'Files'
        if ((Test-Path -LiteralPath $localManifest -PathType Leaf) -and
            (Test-Path -LiteralPath $localFiles -PathType Container)) {
            $PackageRoot = $PSScriptRoot
        }
        else {
            $PackageRoot = Select-UpdatePackageFolder
            if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
                Write-Host 'Import cancelled.'
                exit 0
            }
        }
    }
    $packageRoot = Get-NormalizedRoot $PackageRoot
    $manifestPath = Join-Path $packageRoot 'manifest.json'
    $filesRoot = Join-Path $packageRoot 'Files'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'manifest.json was not found beside the importer.' }
    if (-not (Test-Path -LiteralPath $filesRoot -PathType Container)) { throw 'The Files folder was not found beside the importer.' }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$manifest.schemaVersion -ne 1 -or $manifest.project -ne 'ESDP') {
        throw 'This is not a supported ESDP code update package.'
    }

    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
        $ProjectRoot = Select-ProjectFolder
        if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
            Write-Host 'Import cancelled.'
            exit 0
        }
    }
    $ProjectRoot = Get-NormalizedRoot $ProjectRoot
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectRoot 'ESDP.csproj') -PathType Leaf)) {
        throw "ESDP.csproj was not found in: $ProjectRoot"
    }

    $sourceExtensions = @($manifest.sourceExtensions | ForEach-Object { [string]$_ })
    $resourceExtensions = @($manifest.resourceExtensions | ForEach-Object { [string]$_ })
    $excludedDirectories = @($manifest.excludedDirectories | ForEach-Object { [string]$_ })
    $excludedFileNames = @($manifest.excludedFileNames | ForEach-Object { [string]$_ })

    function Test-ManagedRelativePath([string]$RelativePath) {
        $normalized = $RelativePath.Replace('/', '\')
        $segments = $normalized.Split([char[]]'\', [System.StringSplitOptions]::RemoveEmptyEntries)
        if ($segments.Count -eq 0) { return $false }
        $fileName = [System.IO.Path]::GetFileName($normalized)
        $extension = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()
        $isManagedResource = ($resourceExtensions -contains $extension) -and
            $segments[0].Equals('Resources', [System.StringComparison]::OrdinalIgnoreCase)
        foreach ($segment in $segments) {
            if ($isManagedResource -and
                $segment.Equals('Resources', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
            if ($excludedDirectories -contains $segment) { return $false }
        }
        if ($excludedFileNames -contains $fileName) { return $false }
        if ($fileName.EndsWith('.user', [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
        if ($sourceExtensions -contains $extension) { return $true }
        if ($isManagedResource) { return $true }
        return $false
    }

    Write-Host 'Validating update package...'
    $packagePathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($manifest.files)) {
        $relative = ([string]$entry.path).Replace('\', '/')
        if (-not $packagePathSet.Add($relative)) { throw "Duplicate path in manifest: $relative" }
        $source = Resolve-SafeChildPath $filesRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Package file is missing: $relative" }
        $actualHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "Package file hash mismatch: $relative" }
    }

    $existingFiles = @(
        Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Force |
            Where-Object {
                $relative = Get-RelativePath $ProjectRoot $_.FullName
                Test-ManagedRelativePath $relative
            } |
            Sort-Object FullName
    )

    $staleFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in $existingFiles) {
        $relative = (Get-RelativePath $ProjectRoot $file.FullName).Replace('\', '/')
        if (-not $packagePathSet.Contains($relative)) { $staleFiles.Add($file) }
    }

    Write-Host ''
    Write-Host ("Target: {0}" -f $ProjectRoot)
    Write-Host ("Package files: {0}" -f @($manifest.files).Count)
    Write-Host ("Obsolete managed files to remove: {0}" -f $staleFiles.Count)
    Write-Host 'NuGet.Config, OfflineNuGet, bin, obj, .vs, .codex and project data are excluded.'
    Write-Host 'A full backup of the current managed source will be created first.'
    if (-not $Force) {
        $answer = Read-Host 'Type YES to continue'
        if ($answer -cne 'YES') {
            Write-Host 'Import cancelled.'
            exit 0
        }
    }

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $backupRoot = Join-Path (Join-Path $ProjectRoot '.code-sync-backups') $stamp
    $backupFilesRoot = Join-Path $backupRoot 'Files'
    New-Item -ItemType Directory -Path $backupFilesRoot -Force | Out-Null

    $backupEntries = New-Object System.Collections.Generic.List[object]
    foreach ($file in $existingFiles) {
        $relative = Get-RelativePath $ProjectRoot $file.FullName
        $backupPath = Join-Path $backupFilesRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $backupPath -Force
        $backupEntries.Add([ordered]@{
            path = $relative.Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    [ordered]@{
        createdAt = (Get-Date).ToString('o')
        target = $ProjectRoot
        sourcePackage = $packageRoot
        files = $backupEntries.ToArray()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backupRoot 'backup-manifest.json') -Encoding UTF8

    $copiedCount = 0
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        $source = Resolve-SafeChildPath $filesRoot $relative
        $destination = Resolve-SafeChildPath $ProjectRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $copiedCount++
    }

    foreach ($file in $staleFiles) {
        $fullPath = [System.IO.Path]::GetFullPath($file.FullName)
        $projectPrefix = $ProjectRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($projectPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a file outside the target project: $fullPath"
        }
        Remove-Item -LiteralPath $fullPath -Force
    }

    Write-Host ''
    Write-Host 'Import completed.' -ForegroundColor Green
    Write-Host ("Copied or updated: {0}" -f $copiedCount)
    Write-Host ("Removed obsolete source files: {0}" -f $staleFiles.Count)
    Write-Host ("Backup: {0}" -f $backupRoot)
    Write-Host 'Open ESDP.csproj in Visual Studio and build the project.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host ("ERROR: {0}" -f $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 1
}
