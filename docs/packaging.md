# 打包与分发

自包含发布、Windows 安装程序、macOS .app / .dmg，以及一路踩过的坑。

> 本文档从 README 拆出，内容未删改。返回 [README](../README.zh-CN.md)。

## 打包分发

```powershell
pwsh scripts/publish.ps1
```

产出 `artifacts/publish/win-x64/`，自包含，目标机器不需要装 .NET 运行时，
双击 `LingoDict.exe` 即可。约 226 MiB，其中 95 MiB 是词典库。
`-Runtime osx-arm64` 可以换目标平台。

启动时间实测（同一台机器，各跑 5 次取最小值，都在页缓存已预热的状态下）：

| 构建方式 | 启动到出窗口 |
| --- | --- |
| `dotnet build -c Release`（依赖框架） | 2.27 s |
| `scripts/publish.ps1`（自包含 + ReadyToRun） | 1.52 s |

刚打完包的第一次启动会明显更慢（2.8 s 上下）——226 MiB 刚写完盘，页缓存还是冷的，
不代表稳态表现。

### Windows 安装包

```powershell
pwsh scripts/publish.ps1
pwsh scripts/pack-installer.ps1
```

产出 `artifacts/LingoDict-0.5.0-win-x64-Setup.exe`，约 102 MiB（226 MiB 走 lzma2/max + solid）。

| | |
| --- | --- |
| 安装位置 | `%LOCALAPPDATA%\LingoDict`（`PrivilegesRequired=lowest`，全程不弹 UAC） |
| 快捷方式 | 开始菜单 `LingoDict`，桌面图标可选（默认勾选） |
| 卸载 | 「应用和功能」里的「LingoDict 离线英语词典」，非管理员即可卸 |
| 用户数据 | `%APPDATA%\LingoDict\userdata.db` 不在安装目录里，卸载不删 |

需要 Inno Setup 6：

```powershell
winget install --id JRSoftware.InnoSetup
```

界面是单窗口自定义的，不是默认的多页向导：一张品牌背景图上放安装位置和两个勾选项，
右侧是主按钮，底部是进度条。改安装位置走「浏览…」，不直接编辑。实现全在 `packaging/LingoDict.iss` 的 `[Code]` 里，
中文文案来自 `packaging/ChineseSimplified.isl`
（[kira-96 的社区翻译](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)，MIT）。

改这段界面前值得知道的几件事：

- 背景在**运行时按客户区实际尺寸绘制**（渐变 + 文字直接画进位图），不是预生成图片。
  高 DPI 下拉伸位图会糊，直接画则任何缩放都清晰；静态文字画进位图还省掉一堆
  要处理透明背景的 Label。
- 窗体尺寸要**读回**而不是直接用设进去的值。Inno 对 `ClientHeight` 有自己的下限，
  按设定值铺背景会在底部露出一条没盖住的按钮栏底色。
- 导航按钮要**又藏又挪走**。Inno 每次翻页都会重设它们的可见性，只在
  `InitializeWizard` 里藏一次，进下一页就又冒出来；而它把 `Visible` 设回 `True`
  发生在 `CurPageChanged` 之前，所以哪怕在那里再藏一次，中间也会闪一帧
  （进完成页时那颗「完成」就是这么露出来的）。位置 Inno 不会重设，
  把它们 `SetBounds` 到可视区外才是彻底的。
- **界面上没有原生控件**，按钮、勾选框、路径框全是画出来的。深色背景配原生控件
  处处碰壁，逐个换掉之后反而最省心：
  - `TNewButton` 由系统主题画，设 `Color` 无效，深色背景上是一块白。
  - `TPanel` 能上色（要 `ParentBackground := False`，否则主题下画父背景），
    但它的 `OnClick` 在 Inno 的 Pascal 里不触发，做出来是个点不动的色块。
  - `TNewCheckBox` 的标题由主题引擎绘制，无视 `Font.Color`——深色背景上是
    一行几乎看不见的黑字。
  - `TNewEdit` 是窗体上唯一能聚焦的控件，一拿到焦点 Windows 就把整条路径反白，
    看着像报错；在 `CurPageChanged` 里清选区跑得比 Inno 设焦点还早，压不住。
    改成只读展示 + 点击浏览就没这个问题了。
  - `TNewProgressBar` 走系统主题，就是那根绿的，改不了颜色。
  - `TNewStaticText` 不设 `Color` 会顶着一块 `clBtnFace` 白底。
- `TBitmapImage` 的 `OnClick` 是可用的，自绘控件的点击就靠它。
- 自绘按钮当不了 `Default`，所以另摆了一颗挪到可视区外的真按钮接管回车。
- 每个自绘控件都要**先把同一条背景渐变按自己的绝对位置铺进位图**，圆角切掉的
  四角才不会是一块突兀的纯色。这就是 `GradientAt` 存在的原因。

`AppId` 是产品的永久标识，升级靠它认出旧版本，任何时候都不能改。

#### 为什么不是 WiX / MSI

一开始做的是 WiX MSI（109 MiB），后来换掉了：

- 这个项目没有组策略分发需求，MSI 相对 exe 的主要优势用不上。
- WiX v5 停更于 2024-10，v6/v7 引入 Open Source Maintenance Fee，构建前必须先接受
  一份 EULA（年收入一万美元以上的组织还需赞助）。那道 EULA 只发生在构建机上，
  与终端用户无关，但这个岔路口本身是长期负担。Inno 免费、活跃、无附加条款。
- 界面定制省事得多。MSI 的 UI 是 Dialog 表驱动的 Win32 对话框，要换个样子得再套一层
  Burn bootstrapper；Inno 改几行 `[Setup]` 就有背景图和现代样式。
- 包还小一点：102 MiB vs 109 MiB。实测 `dictionary.db` 95 MiB 用 LZMA2 能压到 60 MiB，
  运行时那 131 MiB 压到 40 MiB，都比 MSI 的 CAB/LZX 好一档。
- 顺带省掉一层转义：Inno 的 `InfoBeforeFile` 直接吃纯文本，不像 MSI 的许可页只认 RTF。

代价是失去 `.msi` 才有的组策略部署。静默安装两边都支持（Inno 用 `/VERYSILENT`）。

**不开 `PublishTrimmed`。** 界面里大量使用
`{ReflectionBinding $parent[Window].DataContext.X}`，属性名在运行时才解析，
裁剪器看不到引用会把成员删掉，症状是运行起来空白控件而不是编译错误。
能省的那几兆运行时，跟 95 MiB 的词典库比也不值得冒这个险。

### macOS 安装包

```powershell
pwsh scripts/publish.ps1 -Runtime osx-arm64 -NoGrammar
pwsh scripts/pack-macos.ps1 -PublishDir artifacts/publish/osx-arm64
```

产出 `artifacts/LingoDict-0.5.0-osx-arm64.dmg`。`.app` 是照约定摆出来的目录：

```
LingoDict.app/Contents/
    Info.plist              ← 系统认得它靠这个
    MacOS/LingoDict         ← 可执行文件，必须有执行位
    Resources/lingodict.icns
```

三个容易漏的地方：

- **执行位**。`Copy-Item` 不保留它，少了就是「应用程序已损坏」，
  而不是任何有用的报错。脚本在 macOS/Linux 上补一次 `chmod +x`。
- **`NSHighResolutionCapable`**。不写的话 Retina 屏上整个界面是糊的。
- **`.icns` 要和 `.ico` 同源**。`scripts/make-icon.py` 一次出两个，
  用的是同一套矢量描述，不然两个平台的图标会长得不一样。
  icns 里 11 档从 16 到 1024，`@2x` 那几档（`ic11`/`ic12`/`ic13`/`ic14`）
  和 1x 的像素数一样但标签不同——少了它们 Retina 上就是把小图拉大。

**dmg 没有签名和公证。** 用户首次打开要右键 →「打开」，或者：

```bash
xattr -dr com.apple.quarantine /Applications/LingoDict.app
```

去掉这一步需要 Apple Developer ID 证书（99 美元/年）+ `codesign` + `notarytool`。

`hdiutil` 只有 macOS 上有，所以在 Windows/Linux 上跑这个脚本会停在 `.app`
（方便本地检查 bundle 结构），不报错。
