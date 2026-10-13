param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $Destination 'docs') -Force | Out-Null
foreach ($name in @('Branding.props','LICENSE','COPYRIGHT','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $Destination -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package/README.md') -Destination (Join-Path $Destination 'README.md') -Force
foreach ($name in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package/UserDocuments.txt')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot "docs/$name") -Destination (Join-Path $Destination 'docs') -Force
}

# Links to omitted development material remain usable through the public source tree.

$documents = @(Get-ChildItem -LiteralPath (Join-Path $Destination 'docs') -File -Filter '*.md')
foreach ($name in @('README.md','THIRD-PARTY-NOTICES.md')) { $documents += Get-Item -LiteralPath (Join-Path $Destination $name) }
$documents | ForEach-Object {
    $file = $_
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8
    $text = [regex]::Replace($text, '\]\(([^)\s]+)\)', [Text.RegularExpressions.MatchEvaluator]{ param($match)
        $link = $match.Groups[1].Value
        if ($link -match '^[a-zA-Z][a-zA-Z0-9+.-]*:|^#') { return $match.Value }
        $path, $fragment = $link.Split('#',2)
        $target = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $path))
        if (Test-Path -LiteralPath $target) { return $match.Value }
        $relative = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Destination),$target).Replace('\','/')
        if ($relative.StartsWith('../')) { return $match.Value }
        $url = 'https://github.com/mcxen/AvaMedia/blob/main/' + $relative
        if ($fragment) { $url += '#' + $fragment }
        return '](' + $url + ')'
    })
    Set-Content -LiteralPath $file.FullName -Value $text -Encoding utf8 -NoNewline
}
