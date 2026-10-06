function Get-AppBrand {
    [xml]$appBrandDocument = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'Branding.props') -Raw -Encoding UTF8
    $appChineseName = [string]$appBrandDocument.Project.PropertyGroup.ProductChineseName
    $appEnglishName = [string]$appBrandDocument.Project.PropertyGroup.ProductEnglishName
    [pscustomobject]@{
        ChineseName = $appChineseName
        EnglishName = $appEnglishName
        DisplayName = $appChineseName + ' · ' + $appEnglishName
        MacBundleName = $appChineseName + '.app'
    }
}
