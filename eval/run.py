"""
小模型选型评测。

回答一个具体问题：本地小模型能不能帮上中国学习者，值不值得为它加几百兆到几 G。

两组题（eval/questions.json）：
  collocation   —— 可自动判分。中文 → 英文搭配，答案在词典里有据可查。
  discrimination —— 需人工判断。近义辨析、语法解释这类没有短答案的。

每题跑两遍：
  bare —— 只给问题
  rag  —— 附上词典里检索到的候选搭配和释义，要求只依据给定材料作答

RAG 那一遍是重点：它量化「把本地词典喂进去」到底值多少。如果小模型
带 RAG 能达到可用，那这个项目的 5.3 万条中英搭配就是最大的本钱；
如果带了也不行，那这条路就该放弃，别自欺欺人。

走 llama.cpp 的 llama-server（不是 Ollama）：LLamaSharp 包的就是 llama.cpp，
在这里测出来的速度和质量能直接迁移到真要发布的形态；Ollama 是个需要单独安装的
后台服务，选型能用，分发不能用。

用法：
    python eval/run.py --model 路径/qwen3-1.7b.gguf --server 路径/llama-server.exe
"""

import argparse
import json
import pathlib
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
SERVER = "http://127.0.0.1:8080/completion"


def dictionary():
    path = (ROOT / "artifacts" / "dictionary.db").resolve()
    if not path.exists():
        sys.exit(f"找不到 {path}，先跑 dotnet run --project src/LingoDict.Importer")
    return sqlite3.connect(f"{path.as_uri()}?mode=ro", uri=True)


def retrieve(db, chinese, limit=8):
    """按中文检索词典里的候选搭配——RAG 喂给模型的就是这些。"""
    segmented = " ".join(chinese)
    rows = db.execute(
        """
        SELECT p.text, p.translation
        FROM phrases_fts f JOIN phrases p ON p.id = f.rowid
        WHERE phrases_fts MATCH ?
        ORDER BY bm25(phrases_fts, 4.0, 1.0), length(p.text)
        LIMIT ?
        """,
        (f'"{segmented}" *', limit),
    ).fetchall()
    return [f"{t} = {cn}" for t, cn in rows]


def ask(model, prompt, timeout=300):
    # Qwen3 默认开思维链，/no_think 关掉：评测只看结论，也更快更可比。
    wrapped = f"<|im_start|>user\n{prompt} /no_think<|im_end|>\n<|im_start|>assistant\n"
    body = json.dumps(
        {
            "prompt": wrapped,
            "temperature": 0.0,
            "n_predict": 400,
            "stream": False,
            "stop": ["<|im_end|>"],
        }
    ).encode()
    request = urllib.request.Request(SERVER, data=body, headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.load(response)
    text = payload.get("content", "")
    # 关不掉思维链的版本，兜底剥一层
    text = re.sub(r"<think>.*?</think>", "", text, flags=re.S).strip()
    return text, time.perf_counter() - started


def graded(answer, accepted):
    """宽松判分：只要提到任一标准答案就算对，允许模型多说几句。"""
    lowered = answer.lower()
    return any(a.lower() in lowered for a in accepted)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--models", required=True, help="标签名，仅用于结果里区分（服务端已加载哪个模型就是哪个）")
    parser.add_argument("--out", default="eval/results.json")
    args = parser.parse_args()

    db = dictionary()
    questions = json.loads((ROOT / "eval" / "questions.json").read_text(encoding="utf-8"))
    results = {}

    for model in [m.strip() for m in args.models.split(",") if m.strip()]:
        print(f"\n===== {model} =====", flush=True)
        record = {"collocation": [], "discrimination": []}

        for item in questions["collocation"]:
            chinese, accepted = item["cn"], item["answers"]
            context = retrieve(db, chinese)

            prompts = {
                "bare": f"「{chinese}」用地道英语怎么说？只回答英文短语本身，不要解释。",
                "rag": (
                    "下面是词典里检索到的候选搭配：\n"
                    + "\n".join(f"- {line}" for line in context)
                    + f"\n\n根据以上材料回答：「{chinese}」用地道英语怎么说？"
                    "只回答英文短语本身，不要解释。材料里没有就凭你自己的判断回答。"
                ),
            }

            entry = {"cn": chinese, "answers": accepted, "retrieved": context}
            for mode, prompt in prompts.items():
                try:
                    text, seconds = ask(model, prompt)
                except (urllib.error.URLError, TimeoutError) as error:
                    text, seconds = f"[请求失败] {error}", 0.0
                entry[mode] = {"answer": text, "ok": graded(text, accepted), "seconds": round(seconds, 1)}
            record["collocation"].append(entry)
            mark = lambda m: "○●"[entry[m]["ok"]]
            print(f"  {chinese:8} bare{mark('bare')} rag{mark('rag')}  {entry['rag']['answer'][:40]}", flush=True)

        for item in questions["discrimination"]:
            question = item["q"]
            try:
                text, seconds = ask(model, question)
            except (urllib.error.URLError, TimeoutError) as error:
                text, seconds = f"[请求失败] {error}", 0.0
            record["discrimination"].append({"q": question, "answer": text, "seconds": round(seconds, 1)})
            print(f"  [辨析] {question[:24]}… {seconds:.0f}s", flush=True)

        for mode in ("bare", "rag"):
            hits = sum(x[mode]["ok"] for x in record["collocation"])
            total = len(record["collocation"])
            print(f"  {mode:4} 正确 {hits}/{total} ({hits / total:.0%})")
        results[model] = record

    (ROOT / args.out).write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n结果写入 {args.out}")


if __name__ == "__main__":
    main()
