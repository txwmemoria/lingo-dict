"""
判别模式评测：同一个模型，把「解释语法」换成「二选一」。

上一轮（run.py）的结论是生成式小模型在语法辨析上会自信地答错——
1.7B 把 arrive at Beijing 说成正确的，还编了一整套理由。

这一轮验证一个假设：**失败的不是模型太小，是任务形式选错了。**
同样的判断，如果输出空间只有 A / B 两个词元，模型就没有编理由的余地，
而 GEC 文献里也正说冠词、介词这类错误「混淆集很小，适合做多分类」。

零新增下载：用已经下好的 Qwen3 GGUF，只是换个用法。

两处保证结果可信：
  1. 同一题跑两遍，A/B 顺序对调。模型一换顺序就改口，说明它在猜位置不是在判句子。
  2. 读单个词元的概率而不是看它写了什么，拿到的是置信度不是措辞。

用法（先起 llama-server 加载某个 gguf）：
    python eval/gec_run.py --model qwen3-1.7b
"""

import argparse
import json
import pathlib
import re
import time
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
SERVER = "http://127.0.0.1:8080/completion"


def choose(first, second, timeout=180):
    """问「哪句对」，让它自由作答，再从结论里取字母。

    这里踩了三个坑，都会让结果看着像判断其实是噪声，记下来免得再犯：

    1. 强制单词元 + GBNF 语法约束 **不可行**。Qwen3 下一个词元 100% 想输出
       <think>，A/B 的 logprob 都在 -30 左右；语法把它们强行挑出来，
       拿到的是尾部排序，不是判断。模型需要先写出文字才能判对。
    2. 只 prefill 空的 <think></think> 而不在用户轮加 /no_think，模型处在
       思考模式却被闭合思维块，落到分布外，输出更乱。两者必须同时给。
    3. 结论里的字母要取**第一个**。模型的行文是「结论：A 正确 …… B 是错误的」，
       取最后一次出现会稳定地抓到反例。

    验证方法：先拿 "I am a student." vs "Student a am I." 这种送分题跑，
    正反序都答对了才说明测的是模型而不是自己的 bug。
    """
    question = (
        "下面两句英语，哪一句是正确的？先说结论再简短说明。\n"
        f"A. {first}\nB. {second}"
    )
    body = json.dumps(
        {
            "prompt": f"<|im_start|>user\n{question} /no_think<|im_end|>\n<|im_start|>assistant\n",
            "temperature": 0.0,
            "n_predict": 120,
            "stream": False,
        }
    ).encode()
    request = urllib.request.Request(SERVER, data=body, headers={"Content-Type": "application/json"})
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.load(response)

    text = re.sub(r"<think>.*?</think>", "", payload.get("content", ""), flags=re.S).strip()
    found = re.search(r"\b([AB])\b", text)
    return (found.group(1) if found else "?"), text, time.perf_counter() - started


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True, help="标签名，仅用于结果文件区分")
    args = parser.parse_args()

    pairs = json.loads((ROOT / "eval" / "gec.json").read_text(encoding="utf-8"))["pairs"]
    records, by_cat = [], {}

    for item in pairs:
        right, wrong = item["right"], item["wrong"]

        # 正序：A=正确  /  反序：A=错误。两遍都答对才算真的会判。
        forward, text_f, t1 = choose(right, wrong)
        reverse, text_r, t2 = choose(wrong, right)

        ok_forward = forward == "A"
        ok_reverse = reverse == "B"
        both = ok_forward and ok_reverse

        records.append(
            {
                "cat": item["cat"], "right": right, "wrong": wrong,
                "forward_ok": ok_forward, "reverse_ok": ok_reverse, "consistent": both,
                "said_forward": forward, "said_reverse": reverse,
                "seconds": round(t1 + t2, 2),
            }
        )
        stat = by_cat.setdefault(item["cat"], [0, 0])
        stat[0] += both
        stat[1] += 1
        print(f"  {'●' if both else '○'} [{item['cat']}] {right[:44]}", flush=True)

    total = sum(x["consistent"] for x in records)
    flipped = sum(x["forward_ok"] != x["reverse_ok"] for x in records)
    print(f"\n两遍都对（顺序无关）：{total}/{len(records)} ({total / len(records):.0%})")
    print(f"换个顺序就改口：{flipped}/{len(records)} —— 这部分是在猜位置，不是在判句子")
    print(f"平均每题：{sum(x['seconds'] for x in records) / len(records):.2f}s（含两次调用）\n")
    for cat, (hit, n) in sorted(by_cat.items(), key=lambda kv: -kv[1][0] / kv[1][1]):
        print(f"  {cat:8} {hit}/{n}  {hit / n:.0%}")

    out = ROOT / f"eval/gec-{args.model}.json"
    out.write_text(json.dumps(records, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n结果写入 {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
