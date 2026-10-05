param([string]$Version = '1.0.2')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$') { throw 'Invalid version.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$archiveRoot = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
$archivePath = Join-Path $archiveRoot ('AvaMedia-' + $Version + '-source.zip')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location -LiteralPath $taskRoot
try {
    $sourceFiles = & rg --files --hidden -g '!**/bin/**' -g '!**/obj/**' -g '!docs/reference/**' src tests licenses scripts docs
    if ($LASTEXITCODE -ne 0) { throw 'Source inventory failed.' }
    $sourceFiles += @('AvaMedia.sln','Directory.Build.props','.gitignore','.gitattributes','LICENSE','README.md','THIRD-PARTY-NOTICES.md','Start-AvaMedia.cmd','TASK.md','UISPEC.MD')
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relativePath in ($sourceFiles | Sort-Object -Unique)) {
            $fullPath = Join-Path $taskRoot $relativePath
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $fullPath, $relativePath.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
    Write-Output $archivePath
} finally { Pop-Location }
