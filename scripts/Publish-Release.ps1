param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Revision,
    [string]$Repository='mcxen/AvaMedia'
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$artifacts=Join-Path $taskRoot 'artifacts'
$tag="v$Version"
$mediaTag="media-$tag"
$names=@("AvaMedia-$Version-osx-arm64.dmg","AvaMedia-$Version-win-x64-portable.zip","AvaMedia-$Version-win-x64-setup.exe")
$packages=@($names | ForEach-Object { Join-Path $artifacts $_ })
foreach($path in $packages){if(!(Test-Path -LiteralPath $path)){throw "Release package missing: $path"}}
$media=@(Get-ChildItem -LiteralPath $artifacts -File | Where-Object { $_.Name.StartsWith('AvaMedia-FFmpeg-') })
if(!($media.Name -match '^AvaMedia-FFmpeg-\d+\.\d+\.\d+-source\.tar\.gz$') -or !($media.Name -match '-win-x64-source\.tar\.gz$')){throw 'Both corresponding media source archives are required.'}
function Invoke-GitHub([string[]]$Arguments){
    $result=& gh @Arguments
    if($LASTEXITCODE -ne 0){throw "GitHub command failed: $($Arguments[0..1] -join ' ')"}
    return $result
}
function Publish-Assets([string]$ReleaseTag,[string]$Title,[string]$Notes,[string[]]$Files,[bool]$Latest){
    & gh release view $ReleaseTag --repo $Repository *> $null
    if($LASTEXITCODE -eq 0){
        Invoke-GitHub (@('release','upload',$ReleaseTag,'--repo',$Repository,'--clobber')+$Files)
        Invoke-GitHub @('release','edit',$ReleaseTag,'--repo',$Repository,'--title',$Title,'--notes-file',$Notes,"--latest=$($Latest.ToString().ToLowerInvariant())")
    }else{
        $arguments=@('release','create',$ReleaseTag,'--repo',$Repository,'--title',$Title,'--notes-file',$Notes,"--latest=$($Latest.ToString().ToLowerInvariant())")
        $arguments+=if($Latest){@('--verify-tag')}else{@('--target',$Revision)}
        Invoke-GitHub ($arguments+$Files)
    }
}
$mediaNotes=Join-Path $artifacts 'media-release-notes.md'
@"
Media tools and complete corresponding sources for [AvaMedia $tag](https://github.com/$Repository/releases/tag/$tag).

These archives preserve the exact FFmpeg and dependency sources, patches, build recipes and original licenses. The installers include the media binaries; ordinary users can download the application from the main release. This archive is not the latest application release.

Application and build scripts: [commit $Revision](https://github.com/$Repository/tree/$Revision), AGPL-3.0-only. FFmpeg runtime: GPL-3.0-or-later; component licenses accompany each runtime.
"@ | Set-Content -LiteralPath $mediaNotes -Encoding utf8
Publish-Assets $mediaTag "AvaMedia $tag · Media tools and corresponding sources" $mediaNotes $media.FullName $false
$releaseNotes=Join-Path $artifacts 'app-release-notes.md'
$notes=Get-Content -LiteralPath (Join-Path $taskRoot 'docs/RELEASE-NOTES.md') -Raw
$firstSection=[regex]::Match($notes,'(?s)\A.*?(?=\r?\n## v|\z)').Value.Trim()
$hashes=($packages | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($_) }) -join "`n"
@"
$firstSection

| Platform | Download |
| --- | --- |
| macOS Apple Silicon | [DMG](https://github.com/$Repository/releases/download/$tag/$($names[0])) |
| Windows x64 | [Portable ZIP](https://github.com/$Repository/releases/download/$tag/$($names[1])) · [Setup EXE](https://github.com/$Repository/releases/download/$tag/$($names[2])) |

[FFmpeg corresponding sources and media tools](https://github.com/$Repository/releases/tag/$mediaTag) · [AvaMedia source](https://github.com/$Repository/tree/$tag)

<details>
<summary>SHA256</summary>

~~~text
$hashes
~~~

</details>
"@ | Set-Content -LiteralPath $releaseNotes -Encoding utf8
Publish-Assets $tag "AvaMedia $tag" $releaseNotes $packages $true
$assets=Invoke-GitHub @('release','view',$tag,'--repo',$Repository,'--json','assets') | ConvertFrom-Json
foreach($asset in $assets.assets){
    if($asset.name -notin $names -and ($asset.name.StartsWith('AvaMedia-') -or $asset.name -eq 'SHA256SUMS.txt')){
        Invoke-GitHub @('release','delete-asset',$tag,$asset.name,'--repo',$Repository,'--yes')
    }
}
Write-Output "Published ${tag}: macOS DMG, Windows portable and setup. Media sources: $mediaTag."
