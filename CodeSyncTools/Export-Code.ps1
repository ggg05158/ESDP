[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$sourceExtensions = @(
    '.cs', '.xaml', '.resx', '.settings', '.config', '.json', '.xml',
    '.csproj', '.pubxml'
)
$resourceExtensions = @('.xlsx', '.xlsm', '.ico', '.png', '.jpg', '.jpeg', '.svg', '.ttf', '.otf', '.html', '.js', '.markdown')
$excludedDirectories = @(
    '.git', '.vs', '.codex', '.agents', '.codex-build', '.codex-work',
    'bin', 'obj', 'outputs', 'publish', 'OfflineNuGet', 'CodeSyncOutput',
    '.code-sync-backups', 'Test1', 'CodeSyncTools'
)
$excludedFileNames = @(
    'NuGet.Config', 'Directory.Build.props',
    'ESDP.sln', 'ESDP.slnx'
)

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

function Test-ManagedRelativePath([string]$RelativePath) {
    $normalized = $RelativePath.Replace('/', '\')
    $segments = $normalized.Split([char[]]'\', [System.StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Count -eq 0) { return $false }

    foreach ($segment in $segments) {
        if ($excludedDirectories -contains $segment) { return $false }
    }

    $fileName = [System.IO.Path]::GetFileName($normalized)
    if ($excludedFileNames -contains $fileName) { return $false }
    if ($fileName.EndsWith('.user', [System.StringComparison]::OrdinalIgnoreCase)) { return $false }

    $extension = [System.IO.Path]::GetExtension($fileName).ToLowerInvariant()
    if ($sourceExtensions -contains $extension) { return $true }
    if (($resourceExtensions -contains $extension) -and $segments[0].Equals('Resources', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }
    return $false
}

try {
    if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
        $ProjectRoot = Join-Path $PSScriptRoot '..'
    }
    $ProjectRoot = Get-NormalizedRoot $ProjectRoot

    $projectFile = Join-Path $ProjectRoot 'ESDP.csproj'
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "ESDP.csproj was not found in: $ProjectRoot"
    }

    Write-Host 'Scanning managed source files...'
    $managedFiles = @(
        Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -Force |
            Where-Object {
                $relative = Get-RelativePath $ProjectRoot $_.FullName
                Test-ManagedRelativePath $relative
            } |
            Sort-Object FullName
    )
    if ($managedFiles.Count -eq 0) {
        throw 'No managed source files were found.'
    }

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    # Keep exported source copies outside the SDK-style project tree. Otherwise
    # MSBuild's default globbing would compile the copied .cs/.xaml files too.
    $outputParent = Join-Path (Split-Path -Parent $ProjectRoot) 'ESDP_CodeUpdates'
    $packageRoot = Join-Path $outputParent ("ESDP_Code_Update_{0}" -f $stamp)
    $filesRoot = Join-Path $packageRoot 'Files'
    New-Item -ItemType Directory -Path $filesRoot -Force | Out-Null

    $manifestFiles = New-Object System.Collections.Generic.List[object]
    foreach ($file in $managedFiles) {
        $relative = Get-RelativePath $ProjectRoot $file.FullName
        $destination = Join-Path $filesRoot $relative
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force

        $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifestFiles.Add([ordered]@{
            path = $relative.Replace('\', '/')
            size = [long]$file.Length
            sha256 = $hash
        })
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        project = 'ESDP'
        createdAt = (Get-Date).ToString('o')
        mode = 'exact-source-sync'
        sourceExtensions = $sourceExtensions
        resourceExtensions = $resourceExtensions
        excludedDirectories = $excludedDirectories
        excludedFileNames = $excludedFileNames
        files = $manifestFiles.ToArray()
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding UTF8

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README_사용방법.txt') -Destination (Join-Path $packageRoot 'README_사용방법.txt') -Force

    Write-Host ''
    Write-Host 'Export completed.' -ForegroundColor Green
    Write-Host ("Files: {0}" -f $managedFiles.Count)
    Write-Host ("Package: {0}" -f $packageRoot)
    Write-Host 'Copy the entire package folder to the closed network.'
    exit 0
}
catch {
    Write-Host ''
    Write-Host ("ERROR: {0}" -f $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 1
}
