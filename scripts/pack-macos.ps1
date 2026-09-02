<#
.SYNOPSIS
    把发布目录打成 macOS 的 .app，再压成 .dmg。

.DESCRIPTION
    产出 artifacts/LingoDict-<version>-<rid>.dmg。

    .app 就是一个约定好结构的目录：

        LingoDict.app/Contents/
            Info.plist              ← 系统认得它靠这个
            MacOS/LingoDict         ← 可执行文件，必须有执行位
            Resources/lingodict.icns

    dmg 用 hdiutil 打，所以这一步只能在 macOS 上跑（Windows/Linux 上会
    停在 .app，方便本地检查结构）。

    **不签名、不公证。** 用户下载后首次打开要右键 →「打开」，
    或者 xattr -dr com.apple.quarantine /Applications/LingoDict.app。
    要去掉这一步得有 Apple Developer ID 证书（$99/年）。

.PARAMETER PublishDir
    发布目录。默认 artifacts/publish/osx-arm64。

.PARAMETER Version
    产品版本。默认读 LingoDict.App.csproj 里的 <Version>。

.PARAMETER SkipDmg
    只产出 .app，不打 dmg。非 macOS 上会自动跳过，这个开关是给 macOS 上
    只想看看 bundle 结构时用的。
#>
[CmdletBinding()]
param(
    [string]$PublishDir,
    [string]$Version,
    [switch]$SkipDmg
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts/publish/osx-arm64' }
if (-not (Test-Path (Join-Path $PublishDir 'LingoDict'))) {
    throw "$PublishDir 里没有 LingoDict 可执行文件。先跑 pwsh scripts/publish.ps1 -Runtime osx-arm64。"
}

if (-not $Version) {
    $csproj = Get-Content (Join-Path $root 'src/LingoDict.App/LingoDict.App.csproj') -Raw
    $Version = ([regex]::Match($csproj, '<Version>([^<]+)</Version>')).Groups[1].Value
    if (-not $Version) { throw '读不到 csproj 里的 <Version>。' }
}

# rid 从发布目录名推出来，dmg 文件名要带上（arm64 和 x64 是两个包）
$rid = Split-Path $PublishDir -Leaf

$stage = Join-Path $root "artifacts/macos/$rid"
$app = Join-Path $stage 'LingoDict.app'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $app 'Contents/MacOS') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $app 'Contents/Resources') -Force | Out-Null

Write-Host "打包 $rid → $app" -ForegroundColor Cyan

Copy-Item (Join-Path $PublishDir '*') (Join-Path $app 'Contents/MacOS') -Recurse -Force

$icns = Join-Path $root 'src/LingoDict.App/Assets/lingodict.icns'
if (-not (Test-Path $icns)) { throw "找不到 $icns。先跑 python scripts/make-icon.py。" }
Copy-Item $icns (Join-Path $app 'Contents/Resources/lingodict.icns') -Force

# CFBundleShortVersionString 是给人看的版本，CFBundleVersion 是构建号，
# 两个都必须有——少了任何一个，Finder 的「显示简介」会是空的。
# LSMinimumSystemVersion 跟着 .NET 9 的下限走。
# NSHighResolutionCapable 不写的话 Retina 屏上整个界面都是糊的。
@"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>                  <string>LingoDict</string>
    <key>CFBundleDisplayName</key>           <string>LingoDict 离线英语词典</string>
    <key>CFBundleIdentifier</key>            <string>com.lingodict.app</string>
    <key>CFBundleVersion</key>               <string>$Version</string>
    <key>CFBundleShortVersionString</key>    <string>$Version</string>
    <key>CFBundlePackageType</key>           <string>APPL</string>
    <key>CFBundleExecutable</key>            <string>LingoDict</string>
    <key>CFBundleIconFile</key>              <string>lingodict.icns</string>
    <key>LSMinimumSystemVersion</key>        <string>12.0</string>
    <key>NSHighResolutionCapable</key>       <true/>
    <key>LSApplicationCategoryType</key>     <string>public.app-category.reference</string>
</dict>
</plist>
"@ | Set-Content (Join-Path $app 'Contents/Info.plist') -Encoding utf8 -NoNewline

# Copy-Item 不保留执行位；少了它双击 .app 会报「应用程序损坏」。
if ($IsMacOS -or $IsLinux) {
    chmod +x (Join-Path $app 'Contents/MacOS/LingoDict')
    if ($LASTEXITCODE -ne 0) { throw 'chmod 失败' }
}

$appSize = (Get-ChildItem $app -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("完成：{0:N0} MiB  {1}" -f $appSize, $app) -ForegroundColor Green

if ($SkipDmg) { return }
if (-not $IsMacOS) {
    Write-Host 'hdiutil 只有 macOS 上有，跳过 dmg。' -ForegroundColor DarkGray
    return
}

# 拖到 Applications 的那个软链接——没有它用户只能自己去 Finder 里拖
New-Item -ItemType SymbolicLink -Path (Join-Path $stage 'Applications') -Target '/Applications' | Out-Null

$dmg = Join-Path $root "artifacts/LingoDict-$Version-$rid.dmg"
if (Test-Path $dmg) { Remove-Item $dmg -Force }

Write-Host "打 dmg（UDZO 压缩，274 MiB 要一会儿）" -ForegroundColor Cyan
hdiutil create -volname "LingoDict $Version" -srcfolder $stage -ov -format UDZO -fs HFS+ $dmg
if ($LASTEXITCODE -ne 0) { throw "hdiutil 失败（退出码 $LASTEXITCODE）" }

$dmgSize = (Get-Item $dmg).Length / 1MB
Write-Host ("完成：{0:N0} MiB  {1}" -f $dmgSize, $dmg) -ForegroundColor Green
Write-Host '未签名：首次打开要右键 →「打开」，或 xattr -dr com.apple.quarantine' -ForegroundColor Yellow
