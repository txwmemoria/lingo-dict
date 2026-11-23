# 持续集成与发行

GitHub Actions 的两条流水线，以及词典库和写作模型为什么各走各的路。

> 本文档从 README 拆出，内容未删改。返回 [README](../README.zh-CN.md)。

## 持续集成与发行

四条流水线，都在 `.github/workflows/`。

| | 触发 | 做什么 |
| --- | --- | --- |
| `ci.yml` | push 到 main / PR | Windows + macOS 上构建并跑全部测试 |
| `release.yml` | 打 `v*` 标签 | 出 Windows 安装包和 macOS 两个架构的 dmg，建 Release |
| `data.yml` | 打 `data-v*` 标签 / 手动 | 生成词典库，传到数据 Release，给 `release.yml` 下载 |
| `model.yml` | 打 `model-v*` 标签 / 手动 | 导出并量化写作检查模型，传到模型 Release，应用内「下载模型」从这里取 |

CI 不需要词典库和写作模型：依赖它们的用例走 `Skip` 而不是失败，
所以在一个新克隆的仓库上 CI 是绿的。

### CI 上不量时间

`SearchLatencyTests` 里的两条计时断言在 CI 上关掉（靠 `CI` 环境变量判断）。
共享 runner 的 CPU 和磁盘都是抢来的，本地并行构建时就已经见过搜索从 62ms 抖到 296ms，
而那条断言的健康值和悬崖只差 5 倍，噪声完全盖得住。

关掉不等于不设防：这两条真正要抓的「兜底 SQL 退回 OR 写法」和
「`book_words(entry_id)` 索引没了」，都由 `DictionaryImportTests` 直接查
`sqlite_master` 确定性地钉住。计时是第二道，本来也只在稳定的机器上才可信。
用例其余的正确性断言照跑。

### 词典库怎么进到 CI 里

`artifacts/dictionary.db`（103 MiB）不在仓库里，也不该在——它由 81 个第三方词书
zip 生成（`.gitignore` 排除了 `data/kajweb-dict/book/*.zip`），在公开仓库里再分发
一次不合适；每次 CI 都去抓上游同样不合适。

所以：**每个 schema 版本只生成一次，传到一个专门的数据 Release，发版时只是下载。**

生成交给 `data.yml`：打一个 `data-v<schema>` 标签就跑——浅克隆 kajweb/dict
（81 个压缩包就在它的仓库里，不跑它的 `download.py`），取 ECDICT，跑 Importer，
把 `dictionary.db` 传到同名 Release（已存在就覆盖）。数据 Release 标成非 latest，
不会顶掉真正的版本。

```bash
git tag data-v7 && git push origin data-v7
```

也可以在本地生成后手动传：

```bash
dotnet run --project src/LingoDict.Importer
gh release create data-v7 artifacts/dictionary.db --title "词典库 schema 7" --latest=false --notes "由 LingoDict.Importer 生成，供 release.yml 取用。"
```

标签里的版本号跟着 `SqliteDictionaryRepository.SupportedSchemaVersion` 走。
改了 schema 就要重新生成、传一个新标签，并把 `release.yml` 的 `DATA_TAG` 一起改。
`scripts/ci-preflight.ps1` 在每个构建任务最开头核对这一点，对不上直接停——
否则发出去的是一个装上就报 `no such column` 的包。同一个脚本还核对标签版本和
csproj 的 `<Version>` 一致，免得打了 `v1.1.0` 而包里标着 `1.0.0`。

### 发行版里不带写作模型

`release.yml` 一律用 `-NoGrammar`。模型是 [gotutiyan/gector](https://github.com/gotutiyan/gector)
的产物，**仅限非商业用途**（见 [grammar.md 的「模型的许可」](grammar.md#模型的许可)）；本地自用打进包里是一回事，
挂到公开 Release 上是一次公开的再分发。应用内已经有下载 / 从本地导入的入口，
用户首次打开写作页时自己装即可，词典、学习、发音都不受影响。
