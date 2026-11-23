# LingoDict

**English** · [简体中文](README.zh-CN.md)

An offline English–Chinese dictionary for Windows and macOS. No network, no account,
no telemetry — the dictionary, the spaced-repetition scheduler, the text-to-speech and
even the grammar checker all run on your machine.

Built with [Avalonia](https://avaloniaui.net/) 12 and .NET 9.

## What it does

**Look up words.** Search by English headword or by Chinese meaning; hits are highlighted
in both the list and the entry. 21,319 headwords with phonetics, drawn from 81 vocabulary books.

**Read through a book.** Browse any of the 81 books entry by entry. A *bookmark* remembers
where you stopped in each one — the habit you have with a paper dictionary, minus the paper.

**Learn.** SM-2 spaced repetition. After you flip a card it asks *"did you recall it?"* rather
than showing four bare labels: each of the four buttons carries what you remembered on top and
when the word comes back underneath. Daily caps for new and review cards are adjustable.

**Hear it.** The 🔊 next to a headword reads it with the system speech synthesizer — offline.
Every symbol in a phonetic transcription is clickable too: tap `æ` and it says *cat*, because
a symbol only sticks once it is tied to a word you can already pronounce. There is a full IPA
chart and a minimal-pair listening drill built around the contrasts Chinese speakers actually
confuse (θ/s, v/w, ɪ/iː, n/ŋ codas, l/r).

**Check your writing.** Paste a paragraph and get suggestions one by one, each openable in the
entry pane to verify. Runs a quantized GECToR model locally through ONNX Runtime.
[Needs a one-time model install](docs/grammar.md).

**Get around.** Synonyms and word-family links are clickable. Double-click any word inside an
example sentence to look it up; select a span and right-click to search the whole thing.
Hovering highlights only words that are actually in the dictionary, so you never double-click
into nothing.

## Install

Grab the latest build from [Releases](https://github.com/txwmemoria/lingo-dict/releases/latest):

- **Windows** — `LingoDict-*-win-x64-Setup.exe`. Installs to `%LOCALAPPDATA%\LingoDict`, no administrator rights needed.
- **macOS** — the `.dmg` for your chip (`osx-arm64` for Apple Silicon, `osx-x64` for Intel), then drag it into Applications.

The macOS build is **neither signed nor notarized**. On first launch use right-click → Open,
or run `xattr -dr com.apple.quarantine /Applications/LingoDict.app`.

Or build from source, below.

## Build from source

You need the .NET SDK 9.

The vocabulary data is **not in this repository** — it belongs to
[kajweb/dict](https://github.com/kajweb/dict). Clone it; the 81 book archives (about 71 MiB)
come with the clone:

```bash
git clone --depth 1 https://github.com/kajweb/dict data/kajweb-dict
```

Do not run its `download.py`: it re-fetches every archive from an old CDN address and would
overwrite the good copies if that address is gone. `data/kajweb-dict/` is git-ignored here.

Optionally, add word frequencies and inflected forms from
[ECDICT](https://github.com/skywind3000/ECDICT) (MIT, 63 MiB):

```bash
curl -L -o data/ecdict/ecdict.csv https://raw.githubusercontent.com/skywind3000/ECDICT/master/ecdict.csv
```

Without it the import still works — you just lose frequency ordering and the
inflected-form fallback (typing `cancelled` finds nothing instead of `cancel`).

Then build the dictionary database (~104 MiB, written to `artifacts/dictionary.db`):

```bash
dotnet run --project src/LingoDict.Importer
```

And run it:

```bash
dotnet run --project src/LingoDict.App
```

To produce a distributable build, see [docs/packaging.md](docs/packaging.md).

## Keyboard

| Key | Action |
| --- | --- |
| `Ctrl+F` / `Ctrl+L` | Focus the search box and select all |
| `Esc` | Clear the search box |
| `Ctrl+D` | Favorite / unfavorite the current entry |
| `Ctrl+E` | Add to / remove from the study plan |
| `Ctrl+S` | Save note |
| `Alt+←` / `Alt+→` | Back / forward (mouse side buttons work too) |
| `Alt+↑` / `Alt+↓` | Previous / next word in the current list |
| `Space` | Flip the card (study page) |
| `1` `2` `3` `4` | Grade: again / hard / good / easy (study page) |

History runs horizontally, list position runs vertically — they are two different things.
If you are on word 37 of a book and follow a link out of an example sentence, `Alt+←` returns
you to word 37 and `Alt+↑` goes to word 36.

## Tests

```bash
dotnet test
```

541 tests. `LingoDict.App.Tests` renders the real `MainWindow` through Avalonia's headless
backend, which is the only way to catch what the compiler cannot — `ReflectionBinding`
`$parent` paths, highlight attached properties, empty-section collapsing.

Tests that need the dictionary database or the grammar model **skip** when those are absent,
so a fresh clone is green.

## Documentation

Written in Chinese, aimed at whoever maintains this next. Each one records why a thing is the
way it is, including the approaches that did not work.

| | |
| --- | --- |
| [docs/architecture.md](docs/architecture.md) | Responsiveness, database schema, body-text storage, search and phrase lookup |
| [docs/design.md](docs/design.md) | Phonetics, bookmarks, spaced repetition, layout, theming, speech |
| [docs/grammar.md](docs/grammar.md) | The GECToR integration, model licensing, why the tokenizer is hand-written |
| [docs/packaging.md](docs/packaging.md) | Self-contained publish, the Windows installer, macOS `.app` / `.dmg` |
| [docs/ci.md](docs/ci.md) | GitHub Actions, and how the dictionary database reaches CI |

## Licensing of the data and the model

The code in this repository is one thing; what it consumes is another.

- **Vocabulary data** comes from [kajweb/dict](https://github.com/kajweb/dict). It is not
  redistributed here — you clone it yourself, and the built database is not committed.
- **The grammar model** is derived from [gotutiyan/gector](https://github.com/gotutiyan/gector),
  whose training data (NUCLE, Lang-8) is licensed for **non-commercial use only**. It is
  therefore not bundled in any release; the app downloads or imports it on first use.
  See [docs/grammar.md](docs/grammar.md#模型的许可).
