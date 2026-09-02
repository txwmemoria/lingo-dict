# 写作检查

GECToR 接进 .NET：模型怎么生成、许可是什么、分词器为什么自己写。

> 本文档从 README 拆出，内容未删改。返回 [README](../README.zh-CN.md)。

## 写作检查

「写作」页把一段英文交给一个本地小模型，逐词标出可能有问题的地方，
给出「介词 · at → in」这样的建议，点「查词」就在右侧打开词条用例句核对。

用的是 **GECToR**（序列标注式语法纠错，RoBERTa-base + 5001 类编辑标签），
int8 量化后 129 MB、每句 13 ms、纯 CPU。选型经过三轮评测，结论在
[`eval/RESULTS.md`](../eval/RESULTS.md)：

| 方案 | 42 组最小差异句 | 每句耗时 | 体积 |
| --- | --- | --- | --- |
| Qwen3-0.6B（判别） | 29% | ~0.5 s | 640 MB |
| Qwen3-1.7B（生成解释） | 经典题答错，还会编理由 | 18 s | 1.8 GB |
| Qwen3-1.7B（判别） | 81% | ~4 s | 1.8 GB |
| **GECToR（int8 ONNX）** | **81%** | **13 ms** | **129 MB** |

三条设计上的取舍，都是从评测结果里来的：

- **叫「建议」不叫「错误」。** 81% 意味着五条里有一条不该听。界面显示把握度百分比，
  每条单独「采纳」，不做一键全改。
- **模型不解释，词典解释。** 生成式模型讲语法会自信地讲错（`arrive at Beijing` 那道题），
  而 GECToR 结构上只能输出编辑标签，编不出理由。道理交给词典的 15 万条例句。
- **只跑一遍，不做原版的多轮迭代。** 迭代会挪动词的位置，而界面要把建议画回原文的
  字符区间上；而且 81% 就是单轮的数字，多轮会让实际表现和测出来的对不上。

### 要让这个功能能用，得准备什么

这一节是给**打包的人**看的。模型权重不在版本库里（123 MiB，`artifacts/` 整个被
`.gitignore` 排除），所以刚克隆的仓库构建出来的应用，写作页是「没有安装模型」那一屏。

#### 第一步（必做）：生成 gector-int8.onnx

三样输入，都要自己去拿：

| 要什么 | 从哪儿拿 | 放哪儿 |
| --- | --- | --- |
| GECToR 权重 | HuggingFace `gotutiyan/gector-roberta-base-5k`（国内用 `hf-mirror.com` 同路径）。需要 `config.json`、`model.safetensors`、`vocab.json`、`merges.txt`、`tokenizer.json`、`tokenizer_config.json` | 任一目录，下一步用 `--dir` 指过去 |
| 动词词形表 | [grammarly/gector](https://github.com/grammarly/gector) 的 `data/verb-form-vocab.txt`（Apache-2.0，168750 行） | 任一路径，用 `--verb-vocab` 指过去 |
| Python 依赖 | `pip install torch transformers onnx onnxruntime` | — |

`model.safetensors` 有 488 MB，**务必用支持续传的方式下载**（`curl -C -`）。
我在这台机器上被截断过两次（75 MB 和 332 MB），而截断的文件加载时不会报错，
只会让结果慢慢变歪。

```powershell
python eval/gector_export.py `
    --dir <上面那个权重目录> `
    --out artifacts/grammar `
    --verb-vocab <verb-form-vocab.txt 的路径>
```

脚本会导出 fp32 ONNX、**逐句比对 PyTorch 的标签是否一致**、动态量化到 int8、
再拿 42 组题重新判分。跑完 `artifacts/grammar/gector-int8.onnx` 就位（123 MiB）。
终端最后应当看到 `int8: 34/42 (81%)`——对不上就说明上游权重换了版本，别继续往下发。

> `--out artifacts/grammar` 这个路径是写死在 `LingoDict.App.csproj` 里的，
> 换地方构建就拷不到，写作页会安静地显示「没有安装模型」。

#### 第二步（必做）：确认它进了包

```powershell
pwsh scripts/publish.ps1
```

末行会报 `完成：371 MiB（词典库 101 MiB，写作模型 123 MiB）`。
**写作模型那一栏是 0 就说明第一步的文件没在那儿**，此时打出来的包用户装上也没有写作检查。

加 `-NoGrammar` 可以刻意不带，安装包从 193 MiB 回到 102 MiB，
用户第一次打开写作页时自己下载或导入。

#### 第三步（可选）：配下载源

只有打了 `-NoGrammar` 的精简包，或者用户想重装模型时才用得上。
**默认是空的，所以「下载模型」按钮不显示**，界面只给「从本地文件导入」。

要开启，编辑 [`src/LingoDict.Grammar/GrammarModelInstaller.cs`](../src/LingoDict.Grammar/GrammarModelInstaller.cs)
里的 `Official`，三个字段都要对：

```csharp
public static readonly GrammarModelSource Official = new(
    Urls:
    [
        "https://<你的托管>/gector-int8.onnx",        // 按顺序试，前一个不通就换下一个
        "https://<镜像>/gector-int8.onnx",
    ],
    Bytes: 128_683_357,                                 // 必须等于文件的实际字节数
    Sha256: "902e12d3……16bda7");                       // 小写十六进制，校验不过就删掉重下
```

- **`Urls`** — 直链，服务端必须支持 HTTP `Range`（断点续传要用；HuggingFace、
  GitHub Releases、阿里云 OSS 都支持）。填几个就试几个，前面的失败自动换下一个。
- **`Bytes`** — 应用会先比 `Content-Length`，对不上直接拒收。这一条挡的是
  「拿回来一个 200 的登录页/错误页」，那是这类下载最常见的失败方式。
- **`Sha256`** — 下完必校验，不过就删掉 `.part`（不删的话下次「续传」会从坏字节后面接）。

重新生成过模型的话，后两个值都得跟着换：

```powershell
(Get-Item artifacts/grammar/gector-int8.onnx).Length
(Get-FileHash artifacts/grammar/gector-int8.onnx -Algorithm SHA256).Hash.ToLower()
```

改完这三个字段，「下载模型（123 MiB）」按钮会自动出现在写作页上，不需要动别的代码。

#### 用户那一侧要填什么

**什么都不用填。** 装了带模型的安装包就直接能用；用精简包的话，写作页上点
「下载模型」（配了源）或「从本地文件导入」（选一个自己拿到的 `gector-int8.onnx`）。

导入**不**校验哈希——README 就是教人自己跑 `gector_export.py` 生成的，换个 torch
版本导出来字节就不一样，卡哈希会把好文件也拒了。改成真拿 onnxruntime 打开一次、
核对输出维度和标签表行数。

装好的权重落在程序目录的 `grammar/` 下（安装位置 `%LOCALAPPDATA%\LingoDict` 本来就可写，
卸载能一并清掉）；程序目录只读时退到 `%LOCALAPPDATA%\LingoDict\grammar`。

### 模型的许可

上游把许可分成两段写：

> Code: MIT license
> **Trained models on Hugging Face Hub: Only non-commercial purposes.**

原因在训练数据——第二阶段用了 NUCLE 和 Lang-8，这两个语料本身就要注册申请、禁止商用。

这意味着：**随包分发这个模型，也就意味着这个安装包只能非商业地分发。**
如果 LingoDict 以后要收费或带广告，这条模型链路整个得换（换官方
grammarly/gector 的检查点也解决不了——底下是同一批数据）。

`verb-form-vocab.txt` 来自 [grammarly/gector](https://github.com/grammarly/gector)（Apache-2.0），
供 `$TRANSFORM_VERB_*` 标签查词形（`go → goes`），这个没有限制。

`verb-form-vocab.txt` 来自 [grammarly/gector](https://github.com/grammarly/gector)（Apache-2.0），
供 `$TRANSFORM_VERB_*` 标签查词形（`go → goes`）。

### 分词器为什么是自己写的

ONNX 只吃 token id，而 .NET 生态里没有和 HuggingFace 逐字节对齐的 RoBERTa byte-level BPE。
分词错了不会报错——它照样吐出合法 id，模型照样出结果，只是标签悄悄错位。
所以 `tests/LingoDict.Grammar.Tests/Golden/` 里放了两份基准：102 条分词结果、
102 条逐词标签，都由 Python 侧用**同一个 int8 模型**生成，C# 端逐条比对。
