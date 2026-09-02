# LingoDict

[English](README.md) · **简体中文**

Windows / macOS 上的离线英汉词典。不联网、不注册、不上报——查词、间隔重复、
朗读，连语法检查都跑在本机。

基于 [Avalonia](https://avaloniaui.net/) 12 和 .NET 9。

## 能做什么

**查词。** 英文词头或中文释义都能搜，列表和释义里都会高亮命中的部分。
21,319 个词头带音标，取自 81 本词书。

**逐本读。** 81 本词书可以一个词一个词地翻。每本书各记一个**书签**，
记住你上次看到哪儿了——纸质词典的习惯，去掉纸。

**背单词。** SM-2 间隔重复。翻面后先问一句「刚才想起来了吗」，而不是直接甩出四个词：
四个按钮各占两行，上行是你刚才记得怎么样，下行是这个词下次什么时候再出现。
每日新词和复习上限可调。

**听发音。** 词头旁的 🔊 用系统语音合成朗读，完全离线。音标里**每个音都能点**——
点 `æ` 它念 *cat*，因为音标这套符号只有绑到一个你已经会读的词上才记得住。
顶栏还有一张完整的音标表和听音辨词练习，练的全是中文母语者真会混的对立
（θ/s、v/w、ɪ/iː、n/ŋ 韵尾、l/r）。

**检查写作。** 贴一段英文，逐条给建议，每条都能一键在右侧打开词条核对。
本地跑量化过的 GECToR 模型，走 ONNX Runtime。[需要单独装一次模型](docs/grammar.md)。

**到处跳。** 同近义词、同根词可点击直达。例句里**双击单词**就查，**划选后右键**查整段。
鼠标移过时只高亮词典里真有的词，所以不会双击了却毫无反应。

## 安装

还没有发行版，先从源码构建（见下）。发布之后会带 Windows 安装程序和 macOS 的 `.dmg`
（Apple Silicon 和 Intel 各一个）。

## 从源码构建

需要 .NET SDK 9 和 Python 3（后者只用于一次性导入数据）。

81 个源 ZIP（约 71 MiB）**不在本仓库里**——它们属于
[kajweb](https://github.com/kajweb) 的「单词本源」项目。先下载：

```bash
python data/kajweb-dict/download.py
```

再生成词典数据库（约 103 MiB，落在 `artifacts/dictionary.db`）：

```bash
dotnet run --project src/LingoDict.Importer
```

然后跑起来：

```bash
dotnet run --project src/LingoDict.App
```

出可分发的包见 [docs/packaging.md](docs/packaging.md)。

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+F` / `Ctrl+L` | 聚焦搜索框并全选 |
| `Esc` | 清空搜索框（焦点在搜索框时） |
| `Ctrl+D` | 收藏 / 取消收藏当前词条 |
| `Ctrl+E` | 加入 / 移出学习计划 |
| `Ctrl+S` | 保存笔记 |
| `Alt+←` / `Alt+→` | 后退 / 前进（鼠标侧键同样可用） |
| `Alt+↑` / `Alt+↓` | 当前列表的上一个 / 下一个词 |
| `空格` | 学习页翻面 |
| `1` `2` `3` `4` | 学习页评分：重来 / 困难 / 良好 / 简单 |

**横向走历史，纵向走列表位置**——这是两件事。顺着词书看到第 37 个、点例句里的词跳走之后，
`Alt+←` 回到第 37 个，`Alt+↑` 去第 36 个。

学习页的快捷键只在该页生效，且焦点在文本框里时不会抢键。

## 测试

```bash
dotnet test
```

526 条。`LingoDict.App.Tests` 用 Avalonia 无头后端渲染真正的 `MainWindow`——
编译期查不到的那些只能这么验：`ReflectionBinding` 的 `$parent` 路径、高亮附加属性、
空段落隐藏。

依赖词典库或语法模型的用例在它们缺席时**跳过**，所以新克隆的仓库上是绿的。

## 文档

都在 `docs/`，写给下一个维护这份代码的人。每篇记的都是「为什么是这样」，
包括试过但不行的那些路。

| | |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | 响应性、数据库结构、正文存储、搜索与短语检索 |
| [docs/design.md](docs/design.md) | 音标、书签、间隔重复、布局、主题、发音 |
| [docs/grammar.md](docs/grammar.md) | GECToR 怎么接进 .NET、模型许可、分词器为什么自己写 |
| [docs/packaging.md](docs/packaging.md) | 自包含发布、Windows 安装程序、macOS `.app` / `.dmg` |
| [docs/ci.md](docs/ci.md) | GitHub Actions，以及词典库怎么进到 CI 里 |

## 数据和模型的许可

仓库里的代码是一回事，它消费的东西是另一回事。

- **词书数据**来自 [kajweb/dict](https://github.com/kajweb/dict)。这里不再分发一次——
  `download.py` 去取，生成的数据库也不入库。
- **语法模型**衍生自 [gotutiyan/gector](https://github.com/gotutiyan/gector)，
  其训练数据（NUCLE、Lang-8）**仅限非商业用途**。因此任何发行版都不打包它，
  由应用在首次使用时下载或从本地导入。见
  [docs/grammar.md](docs/grammar.md#模型的许可)。
