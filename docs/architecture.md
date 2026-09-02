# 数据与检索

响应性、数据库结构、正文存储、搜索与短语检索。

> 本文档从 README 拆出，内容未删改。返回 [README](../README.zh-CN.md)。

## 响应性

几处量过的数字，都是在真实的 23,755 词 / 96,876 释义 / 152,941 条词书记录上测的。

| 动作 | 修之前 | 现在 |
| --- | --- | --- |
| 换一个词（详情页） | 93 ms | **0.8 ms** |
| 搜索：前缀匹配不上（打到一半的英文） | 326 ms（冷 1864 ms） | **62 ms** |
| 切一本词书（最大的 12,197 词） | 103 ms | 88 ms |
| 启动：载入取词索引 | 112 ms | 112 ms |

三处根因，都不会报错，只会让界面慢：

- **`book_words(entry_id)` 少了索引。** 详情页要按 entry_id 反查「这条释义出自哪几本
  词书」，没有索引 SQLite 就把连接反过来做：对 81 本书逐本查，每换一个词重来一遍。
  302 ms → 0.33 ms，代价 1.7 MiB。换词是这个应用里最高频的动作。
- **兜底 SQL 里的 `OR` 让优化器放弃索引。** 前缀匹配不上任何词头时会落到子串扫描，
  原来写成 `normalized LIKE ... OR translation LIKE ...`，于是先连上 96,876 行
  entries 再开窗。拆成 `UNION` 后前缀那一支能走索引。
- **查询根本没离开 UI 线程。** Microsoft.Data.Sqlite 的 `*Async` 全是同步实现
  （SQLite 是本地文件，没有异步 I/O），返回的 Task 已经是 completed。
  查询多慢，输入就卡多久。`OpenAsync` 里 `Task.Run` 一次跳到线程池，
  该文件所有 `await` 补 `ConfigureAwait(false)`——漏一个就会在那一步弹回 UI 线程。

对应的用例在 `SearchLatencyTests`（含一条「查询不得在调用线程上跑完」的断言）
和 `DictionaryImportTests.TheIndexesQueriesDependOnAreAllPresent`。
后者只查索引在不在，不计时——缺索引在微型夹具上量不出快慢，但「在不在」是确定的。

## 「其他词书的释义」为什么砍掉英文

同一个词头在多本词书里各有一条释义，详情页把它们列在正文下面——**它们的差别
几乎全在中文写法上**：

    v 废除
    v 废除，废止；取消，革除
    vt 彻底废除， 废止
    v 废止, 废除
    v 废除；废止

但源数据每条后面都跟着同一句英文。实测：15,664 个有多条释义的词头里
**9,093 个（64%）各条英文完全一致**，而英文占这些行 **82%** 的字符。
完整的英文在上面「释义」里已经有了，在这一段再抄五遍只是把真正有区别的中文
挤到行首那一小块。

源数据把词性、中文、英文用空格拼成一行（`ContentExtractor` 的 `JoinParts`），
回头切不开。但不需要切：要去掉的正是各条之间的**公共后缀**，
`TranslationText.WithoutRepeatedTail` 拿正在显示的那条当参照做纯文本比对，
不必判断哪一段是英文。SQL 里多取一列（`$exclude` 那条的 translation）就够了。

四条约束避免误伤——尾巴够长（≥20）、从空格处断开、空格后是 ASCII 字母、
**剩下的部分必须含中文**。最后一条是关键：只要求「剩下的非空」的话，
`to officially end a law…` 会在第一个空格处被切成孤零零的 `to`。
剩不下中文就说明切错了地方，整行留着——宁可重复，不可切碎。

全库效果：73,121 行里 53,780 行（74%）被削掉尾巴，字符数 657 万 → 246 万，**减 63%**。

## 词频与词形变化

词书源数据里没有这两样，它们来自 [ECDICT](https://github.com/skywind3000/ECDICT)（MIT），
是**可选输入**：`data/ecdict/ecdict.csv` 不在时导入照常完成，只是少这两项。
实测 23,755 个词头里 22,296 个（93.9%）能在 ECDICT 的 77 万行里找到。

**词频**（`words.frequency`，越小越常用，0 表示未知，18,147 个词头有值）。
原来首屏浏览纯按词头长度排，结果是一整屏两字母缩写：

    改前  I, a, n, BA, BC, CD, CV, DJ, DP, Dr, HQ, ID
    改后  the, and, of, a, to, have, it, I, that, for, you, with

搜索结果的排序里，词频**排在 bm25 之后**，不能反过来：`off` 的词频（205）远高于
`cancel`（3915），但搜「取消」时该给 cancel。词频只是同分候选之间的裁决，
它替换掉的是原来那个更粗糙的替身——词头长度。
0（未知）在每处排序里都要垫底，不能被当成「最常用」。

**词形变化**（`word_forms`，form → word_id，24,030 条）。库里的词头基本都是原形，
输入 `cancelled` 原本一无所获。ECDICT 的 `exchange` 字段给出每个词的变化形式，
导入时展开成一张表；搜索每次都先查一次这张表（主键查找，代价可忽略），
命中就把原形顶到结果最前面。

三条实现上的取舍：

- **只收 `p/d/i/3/r/t/s`，不收 `0`/`1`。** 后两个是反向的——从变化形式指回原形，
  而我们是从已有的原形展开变化形式。收了它们会把 `cancel` 自己也登记成变化形式。
- **已经是词头的形式不登记。** `order` 是词头，它自己的释义比「某个词的复数」更准确。
  25,229 个候选里有一批因此被剔除。
- **一个形式被多个词头认领时归给更常用的那个**（`found` 既是 `find` 的过去式、
  也是动词 `found`）。实测 74 例，占 0.3%。词频相同时按词头字典序，
  保证同一份源数据每次导入结果一致。

## 数据库

两个库互相独立，重新导入词典不会影响用户数据。

| 文件 | 位置 | 说明 |
| --- | --- | --- |
| `dictionary.db` | `artifacts/`（发布时随应用分发，约 94 MiB） | 只读内容库，由导入器生成 |
| `userdata.db` | Windows `%APPDATA%\LingoDict\`，macOS/Linux `~/.config/LingoDict/` | 收藏、历史、笔记、学习卡片 |

`userdata.db` 的建表脚本集中在 `UserSchema`，`SqliteUserDataRepository` 和
`SqliteStudyRepository` 都调用它，两者指向同一个文件，谁先初始化都能拿到完整架构。
`CREATE TABLE IF NOT EXISTS` 补不了后加的列，因此新增列走 `AddColumnIfMissingAsync` 显式迁移。

用户库位置可以覆盖，便于用独立 profile 启动而不动真实数据：

```powershell
dotnet run --project src/LingoDict.App -- --user-data .\scratch\userdata.db
```

优先级为 `--user-data <path>` &gt; `LINGODICT_USERDATA` 环境变量 &gt; 默认位置。

## 正文存储

`entries.content` 存的是压缩后的紧凑 JSON，不是 kajweb 的原始 JSON。原始数据里约 43%
是界面从不读取的字段（`sSpeech`、`sContent_eng`、`sourceInfo`、`star`、`desc`…），
再加上单行超过页面内联上限造成的溢出浪费，直接存要占 230 MiB。

| 处理 | 库体积 |
| --- | ---: |
| 原始 JSON 直存 | 281.6 MiB |
| 抽取用得到的字段 + zlib，去重键改用抽取后的内容，导入后丢弃 `richness_score` | **87.6 MiB** |

去重键从「原始 JSON 的哈希」换成「抽取后正文 + 音标的哈希」之后，词条数从 111,373
降到 96,863——噪声字段的差异不再被当成不同的释义。

序列化必须用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`：默认编码器会把每个汉字
转义成 `\uXXXX`，压缩前先胀三倍。

读路径只有 `GetWordAsync` 需要解压，一次一条（约 0.1 ms）；`SearchAsync` 和
`GetBookWordsAsync` 只用 `translation` 列，完全不碰正文。

`schema_version` 为 `3`，仓储在构造时校验，旧库会给出「请重新运行 LingoDict.Importer」
而不是 `no such column: e.content`。

导入先写 `dictionary.db.importing`，全部完成后才 `File.Move` 原子替换。导入要跑一分钟以上，
中途失败（数据损坏、磁盘满、取消）不能把上一份能用的词典先删掉。连接串关掉了池化，
否则 `Close()` 之后文件句柄可能仍被持有，导致替换失败。

## 搜索

查询走 FTS5 索引。`unicode61` 分词器会把一整串连续汉字当成单个词元，
因此索引侧和查询侧都按 `SearchText` 逐字切分汉字，"美好" 才能命中 "美好的时光"。
词元拼成一个短语并在末尾加 `*`，所以 "can" 能前缀命中 "cancel"。
候选先按 bm25（词头列权重 8 倍）截断，再按"精确 > 前缀 > 其余"重排；
FTS 落空时退回 `LIKE` 子串扫描兜底。

### 短语检索

中文母语者最典型的错误是**逐词翻译**：大雨→`big rain`、浓茶→`thick tea`、开灯→`open the light`。
双语词典结构上救不了这个 —— 它给你「heavy=重的」「rain=雨」，你照样拼不出 `heavy rain`。

这批数据一直就在库里（详情页的「短语」一节，54.8 万条出现、**全部带中文**），
但 `words_fts` 只索引了词头和释义，所以搜「大雨」只能命中 `downpour`，
够不到 `rain` 词条下那条 `heavy rain=大雨`。修法就是给短语单独建一份可检索的表：

```
phrases(id, word_id, text, translation)
phrases_fts(text, translation, content='')
```

去重后 52,781 条（重复率 90%），连索引占约 5 MiB，查询 0.1–3 ms。搜索结果里
短语命中排在词条上面 —— 搜「大雨」时最该看到的是 `pouring rain`，不是 `downpour` 底下第三条。

归属取**词头出现在短语里**的那个词条：同一条短语会挂在多个词条下，
不这么挑的话 `heavy rain` 会归到 `heavy` 名下而不是 `rain`。

实测效果（都是中文母语者会写错的地方）：

| 搜 | 命中 |
| --- | --- |
| 浓茶 | `strong tea` ← 直译会写成 thick tea |
| 大雨 | `pouring rain` · `rain heavily` · `rain cats and dogs` |
| 犯错误 | `make a mistake` |
| 留下印象 | `give the impression` |

覆盖取决于源数据，不是全都有 —— 例如「交通堵塞」落空，但搜「拥挤」能拿到 `traffic congestion`。

### 一对多的辨析

中文一个词常对应英文好几个：看 → see/look/watch/read，花 → spend/cost/take。
搜过去得到一串**并列候选**，词典不告诉你该用哪个。

不写解释，让**各自的典型搭配**并排摆着，区别自己就出来了 —— 搜索结果每行下面挂三条搭配：

```
spend   spend time · spend money · spend money on
cost    low cost · high cost · at any cost
take    take a look · take effect · take the lead
```

关键在排序。按长度取最短的话，see/look/watch/read 会分别给出
`see as` / `look in` / `watch for` / `read on` —— 全是虚词搭配，**区分度为零**。
所以 `phrases.rank` 在导入期就算好：

| rank | 含义 |
| --- | --- |
| 0 | 除词头外还带名词（`watch tv`、`spend money`、`read the newspaper`） |
| 1 | 带其它实词 |
| 2 | 只有虚词（`see as`、`look in`） |

名词表是从释义的词性前缀反推的（释义行本来就以 `n ` / `v ` 开头），
不需要额外的词性标注器。实测 rank 0 有 39,589 条，rank 2 只有 5,851 条。

搭配跟着搜索结果一次批量取回（`ROW_NUMBER() OVER (PARTITION BY word_id)`），
80 个词 2.2 ms。首屏浏览不带搭配 —— 那时用户还没有要比较的对象。

**索引的一行对应一个 entry，`rowid` 就是 entry id**，所以搜索能定位到究竟是哪一条释义
命中的，结果列表和详情页展示的都是那一条。早先按词头建索引时，搜"撤销"会返回 cancel
却只显示"取消"，打开也找不到"撤销"，用户无从知道这条为什么会出现。

同一个词的多条释义可能同时命中，SQL 里用 `ROW_NUMBER` 每个词只留分数最好的一条。
词头只写在 canonical 那一行，避免被重复索引。这套改动让库从 87.6 增到 93.5 MiB。
