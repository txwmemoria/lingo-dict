<#
.SYNOPSIS
    发行前的两项核对：标签版本和词典库 schema。

.DESCRIPTION
    两个都是「忘了同步」才会犯的错，而且都要等用户装上才暴露：

    1. 打了 v1.1.0 的标签，csproj 里还写着 1.0.0。安装包文件名、
       「应用和功能」里的版本、Info.plist 全是旧的。
    2. 改了数据库 schema 却没重新生成、上传词典库。装上之后每次启动
       都是「no such column」——SqliteDictionaryRepository 会在
       VerifySchemaVersion 那里拦下来，但那已经在用户机器上了。

    所以在 CI 最开头就核对，让它停在这里而不是发出去。

.PARAMETER DataTag
    词典库所在的 Release 标签，形如 data-v6。

.PARAMETER Ref
    触发的 git ref。refs/tags/v* 才核对版本号；手动触发时跳过。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataTag,
    [string]$Ref = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ——— schema 版本 ———
$source = Get-Content (Join-Path $root 'src/LingoDict.Infrastructure/Data/SqliteDictionaryRepository.cs') -Raw
$schema = ([regex]::Match($source, 'SupportedSchemaVersion\s*=\s*"(\d+)"')).Groups[1].Value
if (-not $schema) { throw '在 SqliteDictionaryRepository.cs 里找不到 SupportedSchemaVersion。' }

$expectedTag = "data-v$schema"
if ($DataTag -ne $expectedTag) {
    throw @"
词典库标签对不上：workflow 里写的是 $DataTag，代码要的是 schema $schema（即 $expectedTag）。

改了 schema 的话：
    dotnet run --project src/LingoDict.Importer
    gh release create $expectedTag artifacts/dictionary.db --notes "schema $schema"
然后把 .github/workflows/release.yml 的 DATA_TAG 改成 $expectedTag。
"@
}
Write-Host "schema $schema ↔ $DataTag" -ForegroundColor Green

# ——— 标签版本 ———
if (-not $Ref.StartsWith('refs/tags/v')) {
    Write-Host "不是标签触发（$Ref），跳过版本核对。" -ForegroundColor DarkGray
    return
}

$tagVersion = $Ref.Substring('refs/tags/v'.Length)
$csproj = Get-Content (Join-Path $root 'src/LingoDict.App/LingoDict.App.csproj') -Raw
$projectVersion = ([regex]::Match($csproj, '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $projectVersion) { throw '在 LingoDict.App.csproj 里找不到 <Version>。' }

if ($tagVersion -ne $projectVersion) {
    throw @"
版本对不上：标签是 v$tagVersion，csproj 里是 $projectVersion。

打包脚本读的是 csproj，发出去的包会标成 $projectVersion——和标签不一致。
先把 csproj 的 <Version> 改成 $tagVersion 提交，再重新打标签。
"@
}
Write-Host "版本 v$tagVersion ↔ csproj $projectVersion" -ForegroundColor Green
