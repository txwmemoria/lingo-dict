"""
生成分词器黄金样本，供 C# 端逐条比对。

为什么必须有这个：C# 要重新实现 RoBERTa 的 byte-level BPE。实现哪怕差一点点
（少一条 GPT-2 正则分支、字节映射表抄错一个），产生的还是合法 token 序列，
模型照样出结果——只是结果慢慢变歪，没有任何报错。唯一靠谱的验证方式是
拿 HuggingFace 的输出当基准逐条对。

样本里除了评测句，特意放了几类容易踩空的输入：缩写（don't）、连字符、
数字、多空格、大小写、非 ASCII、以及词表里根本没有的生造词。

用法：
    python eval/gector_golden.py --dir <权重目录> --out <golden.json>
"""

import argparse
import json
import pathlib

from transformers import AutoTokenizer

ROOT = pathlib.Path(__file__).resolve().parent.parent

TRICKY = [
    "I don't know.",
    "It's a well-known problem.",
    "He said: \"Hello, world!\"",
    "The price is $19.99 (about 140 RMB).",
    "  leading   and  trailing   spaces  ",
    "ALLCAPS and MixedCase and lower",
    "naive café résumé",
    "Zhang Wei went to Beijing in 2024.",
    "supercalifragilisticexpialidocious",
    "a",
    "I",
    "e-mail me at foo@bar.com",
    "1234567890",
    "Wo shi zhongguoren.",
    "Don't—really—do that.",
    "100% sure; 50/50 chance.",
    "The teacher's students' books",
    "xyzzyx qwertyuiop asdfgh",
]


def tagging_golden(onnx_path, tokenizer, labels, sentences):
    """
    再产一份「每个词拿到什么标签」的基准。

    分词器对上了不代表整条链路对得上——张量摆放、argmax、标签表行号，
    任何一处错位都只会让标签悄悄变成另一个合法标签。所以拿 Python 端
    同一个 int8 模型的输出当基准，让 C# 逐词比。
    """
    import numpy as np
    import onnxruntime as ort

    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    cases = []
    for sentence in sentences:
        encoded = tokenizer([["$START"] + sentence.split()], is_split_into_words=True)
        feeds = {
            "input_ids": np.asarray(encoded["input_ids"], dtype=np.int64),
            "attention_mask": np.asarray(encoded["attention_mask"], dtype=np.int64),
        }
        predicted = session.run(["logits"], feeds)[0][0].argmax(-1).tolist()
        seen, tags = set(), []
        for position, word_index in enumerate(encoded.word_ids(0)):
            if word_index is None or word_index in seen:
                continue
            seen.add(word_index)
            tags.append(labels[predicted[position]])
        cases.append({"text": sentence, "labels": tags[1:]})  # 去掉 $START
    return cases


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--onnx", help="给出 int8 模型就顺便产一份标签基准")
    parser.add_argument("--labels")
    parser.add_argument("--tagging-out")
    args = parser.parse_args()

    tokenizer = AutoTokenizer.from_pretrained(args.dir, add_prefix_space=True)
    pairs = json.loads((ROOT / "eval" / "gec.json").read_text(encoding="utf-8"))["pairs"]
    sentences = [s for item in pairs for s in (item["right"], item["wrong"])] + TRICKY

    cases = []
    for sentence in sentences:
        # 和推理时完全一样的调用：先按空白切词，再前置 $START。
        words = ["$START"] + sentence.split()
        encoded = tokenizer([words], is_split_into_words=True)
        cases.append(
            {
                "text": sentence,
                "ids": encoded["input_ids"][0],
                "tokens": tokenizer.convert_ids_to_tokens(encoded["input_ids"][0]),
                # None 表示特殊符号（<s> / </s>），C# 端用 -1 表示
                "wordIds": [-1 if w is None else w for w in encoded.word_ids(0)],
            }
        )

    out = pathlib.Path(args.out)
    out.write_text(json.dumps(cases, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{len(cases)} 条写入 {out}")

    if args.onnx:
        labels = pathlib.Path(args.labels).read_text(encoding="utf-8").splitlines()
        tagging = tagging_golden(args.onnx, tokenizer, labels, sentences)
        target = pathlib.Path(args.tagging_out)
        target.write_text(json.dumps(tagging, ensure_ascii=False, indent=1), encoding="utf-8")
        flagged = sum(any(t != "$KEEP" for t in c["labels"]) for c in tagging)
        print(f"{len(tagging)} 条标签写入 {target}（其中 {flagged} 条有非 $KEEP 标签）")


if __name__ == "__main__":
    main()
