<#
.SYNOPSIS
    打包可分发的自包含应用。

.DESCRIPTION
    产出 artifacts/publish/<rid>/，目标机器不需要装 .NET 运行时，双击 LingoDict.exe 即可。

    刻意不开 PublishTrimmed：界面里大量使用 {ReflectionBinding $parent[Window].DataContext.X}，
    这类绑定在运行时才解析属性名，裁剪器看不到引用会把它们删掉，跑起来是空白控件而不是编译错误。
    体积的大头是 95 MiB 的词典库，裁那几兆运行时不划算。

    ReadyToRun 预编译为本机代码，主要为了压首屏启动时间；代价是包大一些。

.PARAMETER Runtime
    目标 RID。默认按当前机器推断。

.PARAMETER NoR2R
    关掉 ReadyToRun，产出更小但启动更慢的包。

.PARAMETER NoGrammar
    不把写作检查模型打进包里。安装包从 193 MiB 回到 102 MiB，
    用户首次打开写作页时可以在应用内下载或从本地导入。
#>
[CmdletBinding()]
param(
    [string]$Runtime,
    [switch]$NoR2R,
    [switch]$NoGrammar
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Runtime) {
    $Runtime = if ($IsMacOS) {
        if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'osx-arm64' } else { 'osx-x64' }
    } elseif ($IsLinux) { 'linux-x64' } else { 'win-x64' }
}

$db = Join-Path $root 'artifacts/dictionary.db'
if (-not (Test-Path $db)) {
    throw "找不到 $db。先跑 dotnet run --project src/LingoDict.Importer 生成词典库，否则打出来的包没有词典。"
}

$output = Join-Path $root "artifacts/publish/$Runtime"
if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

Write-Host "打包 $Runtime → $output" -ForegroundColor Cyan

dotnet publish (Join-Path $root 'src/LingoDict.App/LingoDict.App.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishReadyToRun=$(if ($NoR2R) { 'false' } else { 'true' }) `
    -p:IncludeGrammarModel=$(if ($NoGrammar) { 'false' } else { 'true' }) `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

# SkiaSharp / HarfBuzz 的原生符号表是随 nuget 的 runtimes 目录进来的，
# DebugType 管不着，只能发布后删——它们占几十兆，对用户毫无用处。
Get-ChildItem $output -Recurse -Include *.pdb | Remove-Item -Force

$size = (Get-ChildItem $output -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
$dbSize = (Get-Item $db).Length / 1MB
$model = Join-Path $output 'grammar/gector-int8.onnx'
$modelSize = if (Test-Path $model) { (Get-Item $model).Length / 1MB } else { 0 }
Write-Host ("完成：{0:N0} MiB（词典库 {1:N0} MiB，写作模型 {2:N0} MiB）" -f $size, $dbSize, $modelSize) -ForegroundColor Green
if ($modelSize -eq 0) {
    Write-Host "未包含写作检查模型，用户首次打开写作页时可在应用内下载或导入。" -ForegroundColor DarkGray
}
# 非 Windows 的 RID 产出的是没有扩展名的 LingoDict
$entry = if ($Runtime.StartsWith('win')) { 'LingoDict.exe' } else { 'LingoDict' }
Write-Host "入口：$(Join-Path $output $entry)"
if ($Runtime.StartsWith('osx')) {
    Write-Host "这只是个裸目录，还不是 .app。接着跑：pwsh scripts/pack-macos.ps1 -PublishDir $output" -ForegroundColor DarkGray
}
