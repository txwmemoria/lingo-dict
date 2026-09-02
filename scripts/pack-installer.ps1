<#
.SYNOPSIS
    把发布目录打成 Windows 安装程序。

.DESCRIPTION
    产出 artifacts/LingoDict-<version>-win-x64-Setup.exe。
    单窗口界面：一张品牌背景图上放安装位置和两个勾选项，底部是进度条，
    默认的多页向导整个藏掉（实现见 packaging/LingoDict.iss 的 [Code]）。
    装到 %LOCALAPPDATA%\LingoDict，全程不弹 UAC；开始菜单建快捷方式，
    可选桌面图标，能从「应用和功能」卸载。
    卸载不会删 %APPDATA%\LingoDict\userdata.db。

    需要 Inno Setup 6：

        winget install --id JRSoftware.InnoSetup

    为什么不是 WiX/MSI：这个项目没有组策略分发需求，MSI 的主要优势用不上；
    而 WiX v5 已停更、v6/v7 要先接受 Open Source Maintenance Fee 的 EULA，
    Inno 免费、活跃、无附加条款，界面定制也省事得多。

.PARAMETER PublishDir
    发布目录。默认 artifacts/publish/win-x64，没有就先跑 scripts/publish.ps1。

.PARAMETER Version
    产品版本。默认读 LingoDict.App.csproj 里的 <Version>。
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packaging = Join-Path $root 'packaging'

if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts/publish/win-x64' }
if (-not (Test-Path (Join-Path $PublishDir 'LingoDict.exe'))) {
    throw "$PublishDir 里没有 LingoDict.exe。先跑 pwsh scripts/publish.ps1。"
}

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    $iscc = Get-ChildItem `
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", `
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", `
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
}
if (-not $iscc) { throw "找不到 ISCC.exe。执行：winget install --id JRSoftware.InnoSetup" }
$isccPath = if ($iscc -is [System.Management.Automation.CommandInfo]) { $iscc.Source } else { $iscc.FullName }

if (-not $Version) {
    $csproj = Join-Path $root 'src/LingoDict.App/LingoDict.App.csproj'
    $Version = (dotnet msbuild $csproj -getProperty:Version -v:quiet).Trim()
}

$outDir = Join-Path $root 'artifacts'
Write-Host "打包 $Version → $outDir" -ForegroundColor Cyan
Write-Host ("（{0:N0} MiB 走 lzma2/max + solid，要几分钟）" -f (
    (Get-ChildItem $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

& $isccPath `
    "/DAppVersion=$Version" `
    "/DRepoDir=$root" `
    "/DSourceDir=$PublishDir" `
    "/DOutputDir=$outDir" `
    (Join-Path $packaging 'LingoDict.iss')

if ($LASTEXITCODE -ne 0) { throw "ISCC 失败（退出码 $LASTEXITCODE）" }

$setup = Join-Path $outDir "LingoDict-$Version-win-x64-Setup.exe"
Write-Host ("完成：{0:N0} MiB  {1}" -f ((Get-Item $setup).Length / 1MB), $setup) -ForegroundColor Green
