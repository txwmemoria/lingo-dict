"""
专用 GEC 模型评测：GECToR（序列标注），对照生成式小模型。

为什么值得单独测：GECToR 是 encoder-only 的序列标注器，输出空间是 5002 个
**固定编辑标签**（$KEEP / $DELETE / $APPEND_the / $REPLACE_in / …）。
它在结构上写不出散文，也就无从编造理由——这正是生成式小模型在语法题上
栽跟头的地方。参数量 ~125M，比 Qwen3-1.7B 小一个数量级。

模型不是标准 HF 架构（gotutiyan/gector 的自定义类），所以这里直接按它的
config 组装：一个 RoBERTa 编码器 + 两个线性头（labels 5002 类、detect 2 类）。

判分方式跟 gec_run.py 保持一致，好横向比：
  对每组最小差异句，看模型是否
    - 对**正确句**判为无需修改（全 $KEEP）
    - 对**错误句**判为需要修改（至少一处非 $KEEP）
  两条都成立才算这一组答对。

这个判分比 A/B 选择更严格也更贴近真实用法——真实场景没有"另一个选项"
可以对比，模型必须独立看一句话决定改不改。

用法：
    python eval/gector_run.py --dir <放 config.json/model.safetensors 的目录>
"""

import argparse
import json
import pathlib
import time

import torch
from safetensors.torch import load_file
from transformers import AutoTokenizer, RobertaConfig, RobertaModel

ROOT = pathlib.Path(__file__).resolve().parent.parent


class Gector(torch.nn.Module):
    """按 config 复原 gotutiyan/gector 的结构：编码器 + 两个线性头。"""

    def __init__(self, config, shapes):
        super().__init__()
        # 本地构造编码器骨架而不是 from_pretrained：所有权重随后都由 checkpoint
        # 覆盖，去 hub 拉一遍 roberta-base 既慢又要求联网。
        self.bert = RobertaModel(
            RobertaConfig(
                vocab_size=shapes["vocab"], hidden_size=768, num_hidden_layers=12,
                num_attention_heads=12, intermediate_size=3072,
                max_position_embeddings=514, type_vocab_size=1,
            ),
            add_pooling_layer=False,
        )
        hidden = 768
        # 名字必须和 checkpoint 里的键一致，否则 strict=False 会静默不加载，
        # 头是随机初始化的——跑出来是噪声，看着却像模型不行。
        # 形状以 checkpoint 为准：config 里写的 n_labels=5002 / n_d_labels=3
        # 跟实际权重（5001 / 2）对不上，按 config 建会直接尺寸不匹配。
        self.label_proj_layer = torch.nn.Linear(hidden, shapes["labels"])
        self.d_proj_layer = torch.nn.Linear(hidden, shapes["d_labels"])

    # 显式写出两个入参而不是 **inputs：ONNX 追踪器是按位置喂参数的，
    # 收 **inputs 的 forward 在追踪时会拿到空字典。
    def forward(self, input_ids, attention_mask):
        hidden = self.bert(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state
        return self.label_proj_layer(hidden), self.d_proj_layer(hidden)


def load(directory):
    directory = pathlib.Path(directory)
    config = json.loads((directory / "config.json").read_text(encoding="utf-8"))
    weights = load_file(directory / "model.safetensors")
    shapes = {
        "vocab": weights["bert.embeddings.word_embeddings.weight"].shape[0],
        "labels": weights["label_proj_layer.weight"].shape[0],
        "d_labels": weights["d_proj_layer.weight"].shape[0],
    }
    model = Gector(config, shapes)
    # 权重名可能带 model. / bert. 前缀，对不上就直接报出来，别静默漏加载
    missing, unexpected = model.load_state_dict(weights, strict=False)
    # 漏加载会静默变成随机初始化——跑出来是噪声，看着却像模型不行。
    # position_ids 这类 buffer 不算，其余一个都不该漏。
    real_missing = [k for k in missing if "position_ids" not in k]
    if real_missing:
        raise SystemExit(f"有 {len(real_missing)} 个参数没从 checkpoint 加载：{real_missing[:5]}")
    if unexpected:
        print(f"  [提示] checkpoint 里有 {len(unexpected)} 个用不上的键：{unexpected[:3]}")

    model.eval()
    tokenizer = AutoTokenizer.from_pretrained(directory, add_prefix_space=True)
    id2label = {int(k): v for k, v in config["id2label"].items()}
    return model, tokenizer, id2label


@torch.no_grad()
def tag(model, tokenizer, id2label, sentence):
    """返回每个词的编辑标签。$KEEP 之外的都算「模型认为这里要改」。"""
    words = sentence.split()
    encoded = tokenizer(
        [["$START"] + words], is_split_into_words=True, return_tensors="pt", truncation=True
    )
    logits, _ = model(**encoded)
    predicted = logits[0].argmax(-1).tolist()

    # 一个词可能切成多个子词，只取每个词的第一个子词的标签
    seen, labels = set(), []
    for position, word_index in enumerate(encoded.word_ids(0)):
        if word_index is None or word_index in seen:
            continue
        seen.add(word_index)
        labels.append(id2label.get(predicted[position], "<OOV>"))
    return labels[1:]  # 去掉 $START


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", required=True)
    args = parser.parse_args()

    print("加载模型…")
    model, tokenizer, id2label = load(args.dir)
    params = sum(p.numel() for p in model.parameters())
    print(f"参数量 {params / 1e6:.0f}M\n")

    pairs = json.loads((ROOT / "eval" / "gec.json").read_text(encoding="utf-8"))["pairs"]
    records, by_cat = [], {}
    started = time.perf_counter()

    for item in pairs:
        right_tags = tag(model, tokenizer, id2label, item["right"])
        wrong_tags = tag(model, tokenizer, id2label, item["wrong"])

        # 正确句应当放过，错误句应当被标出
        clean = all(t == "$KEEP" for t in right_tags)
        flagged = any(t != "$KEEP" for t in wrong_tags)
        both = clean and flagged

        edits = [f"{w}→{t}" for w, t in zip(item["wrong"].split(), wrong_tags) if t != "$KEEP"]
        records.append(
            {
                "cat": item["cat"], "right": item["right"], "wrong": item["wrong"],
                "right_clean": clean, "wrong_flagged": flagged, "consistent": both,
                "edits_on_wrong": edits,
                "false_alarm": [f"{w}→{t}" for w, t in zip(item["right"].split(), right_tags) if t != "$KEEP"],
            }
        )
        stat = by_cat.setdefault(item["cat"], [0, 0])
        stat[0] += both
        stat[1] += 1
        print(f"  {'●' if both else '○'} [{item['cat']}] {item['wrong'][:40]:42} {' '.join(edits[:3])}", flush=True)

    elapsed = time.perf_counter() - started
    total = sum(x["consistent"] for x in records)
    missed = sum(not x["wrong_flagged"] for x in records)
    noisy = sum(not x["right_clean"] for x in records)

    print(f"\n两条都满足：{total}/{len(records)} ({total / len(records):.0%})")
    print(f"  漏报（错句没标出来）：{missed}/{len(records)}")
    print(f"  误报（对句被乱改）：{noisy}/{len(records)}")
    print(f"平均每句 {elapsed / (len(records) * 2) * 1000:.0f} ms\n")
    for cat, (hit, n) in sorted(by_cat.items(), key=lambda kv: -kv[1][0] / kv[1][1]):
        print(f"  {cat:8} {hit}/{n}  {hit / n:.0%}")

    out = ROOT / "eval/gec-gector.json"
    out.write_text(json.dumps(records, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"\n结果写入 {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
