"""
把 GECToR 导出成 ONNX，供 .NET 端用 onnxruntime 加载。

三件事，缺一不可：
  1. 导出 fp32 ONNX，并**逐句比对** PyTorch 的 argmax 标签是否一致——
     导出静默出错（动态轴写漏、算子降级）不会报错，只会让结果慢慢变歪。
  2. 动态量化到 int8。体积 512 MB → 129 MB 是这个特性能否随应用分发的前提。
  3. 拿 gec.json 的 42 组题在 int8 上重跑一遍准确率。判分口径和应用里一致
     （标点单独成词），所以这里打印的 34/42 就是用户实际会拿到的水平；
     对不上就说明上游权重换了版本，别继续往下发。

顺带产出 .NET 端要用的两个资源：
  labels.txt        —— 5001 个标签，行号即 id
  verb-forms.txt.gz —— $TRANSFORM_VERB_* 的词形表（来自 grammarly/gector，Apache-2.0）

用法：
    python eval/gector_export.py --dir <权重目录> --out <输出目录> [--verb-vocab verb-form-vocab.txt]
"""

import argparse
import collections
import gzip
import json
import pathlib
import time

import numpy as np
import onnxruntime as ort
import torch

from gector_run import load, tag

ROOT = pathlib.Path(__file__).resolve().parent.parent

# 导出用的样例句：长短各一，让追踪器同时看到两种长度，
# 免得把某个维度当成常量固化进图里。
SAMPLE = "I arrived at Beijing yesterday."


def export(model, tokenizer, out_path):
    encoded = tokenizer([["$START"] + SAMPLE.split()], is_split_into_words=True, return_tensors="pt")
    torch.onnx.export(
        model,
        (encoded["input_ids"], encoded["attention_mask"]),
        str(out_path),
        input_names=["input_ids", "attention_mask"],
        output_names=["logits", "detect_logits"],
        # batch 和 seq 都必须是动态的：句子长度不定，固化会导致长句直接报错、
        # 短句被静默补零后标签错位。
        dynamic_axes={
            "input_ids": {0: "batch", 1: "seq"},
            "attention_mask": {0: "batch", 1: "seq"},
            "logits": {0: "batch", 1: "seq"},
            "detect_logits": {0: "batch", 1: "seq"},
        },
        opset_version=17,
        do_constant_folding=True,
        dynamo=False,
    )


# 会被拆成独立词的标点。必须和 C# 端 EnglishText.Punctuation 保持一致——
# 模型是在分好词的语料上训练的，标点自成一个 token。按空白切的话 advices. 是一个词，
# $TRANSFORM_AGREEMENT_SINGULAR 砍掉句点得到 advices：标对了位置却给了个错的改法。
# 拆开之后 42 组题从 33 升到 34，这也是应用里实际跑出来的数字。
PUNCTUATION = ".,!?;:\"')(][}{"


def words_of(sentence):
    """按空白切，再把首尾的标点各自剥成独立的词。"""
    out = []
    for chunk in sentence.split():
        left = 0
        while left < len(chunk) and chunk[left] in PUNCTUATION:
            out.append(chunk[left])
            left += 1
        right = len(chunk)
        while right > left and chunk[right - 1] in PUNCTUATION:
            right -= 1
        if right > left:
            out.append(chunk[left:right])
        out.extend(chunk[right:])
    return out


def onnx_tag(session, tokenizer, id2label, sentence):
    """和 gector_run.tag 同样的语义，只是推理换成 onnxruntime、并且标点单独成词。"""
    encoded = tokenizer([["$START"] + words_of(sentence)], is_split_into_words=True, truncation=True)
    feeds = {
        "input_ids": np.asarray(encoded["input_ids"], dtype=np.int64),
        "attention_mask": np.asarray(encoded["attention_mask"], dtype=np.int64),
    }
    logits = session.run(["logits"], feeds)[0]
    predicted = logits[0].argmax(-1).tolist()

    seen, labels = set(), []
    for position, word_index in enumerate(encoded.word_ids(0)):
        if word_index is None or word_index in seen:
            continue
        seen.add(word_index)
        labels.append(id2label.get(predicted[position], "<OOV>"))
    return labels[1:]


def naive_onnx_tag(session, tokenizer, id2label, sentence):
    """只按空白切词的版本，专供「导出前后是否一致」的比对。"""
    encoded = tokenizer([["$START"] + sentence.split()], is_split_into_words=True, truncation=True)
    feeds = {
        "input_ids": np.asarray(encoded["input_ids"], dtype=np.int64),
        "attention_mask": np.asarray(encoded["attention_mask"], dtype=np.int64),
    }
    predicted = session.run(["logits"], feeds)[0][0].argmax(-1).tolist()
    seen, labels = set(), []
    for position, word_index in enumerate(encoded.word_ids(0)):
        if word_index is None or word_index in seen:
            continue
        seen.add(word_index)
        labels.append(id2label.get(predicted[position], "<OOV>"))
    return labels[1:]


def accuracy(tagger, pairs):
    """判分口径和 gector_run 完全一致，好和 PyTorch 那一栏横向比。"""
    hits, by_cat, elapsed = 0, {}, 0.0
    for item in pairs:
        started = time.perf_counter()
        right_tags = tagger(item["right"])
        wrong_tags = tagger(item["wrong"])
        elapsed += time.perf_counter() - started
        both = all(t == "$KEEP" for t in right_tags) and any(t != "$KEEP" for t in wrong_tags)
        hits += both
        stat = by_cat.setdefault(item["cat"], [0, 0])
        stat[0] += both
        stat[1] += 1
    return hits, by_cat, elapsed / (len(pairs) * 2)


def write_verb_forms(source, destination):
    """
    把 168750 行的 `abandon_abandoned:VB_VBD` 压成按源词分组的一行一词。

    同一个 (词, 标签对) 在原文件里有 358 处给了不同答案（dreamed/dreamt 这类），
    这里保持和 GECToR 自己的加载器一致：后出现的覆盖先出现的。
    """
    grouped = collections.defaultdict(dict)
    for line in pathlib.Path(source).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        words, pair = line.rsplit(":", 1)
        src, dst = words.split("_", 1)
        grouped[src][pair] = dst

    body = "".join(
        f"{src}\t" + " ".join(f"{k}={v}" for k, v in sorted(forms.items())) + "\n"
        for src, forms in sorted(grouped.items())
    )
    destination.write_bytes(gzip.compress(body.encode("utf-8"), 9))
    return len(grouped)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--verb-vocab")
    args = parser.parse_args()

    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    fp32, int8 = out / "gector-fp32.onnx", out / "gector-int8.onnx"

    print("加载 PyTorch 权重…")
    model, tokenizer, id2label = load(args.dir)

    print(f"导出 {fp32.name} …")
    export(model, tokenizer, fp32)
    print(f"  {fp32.stat().st_size / 1e6:.0f} MB")

    pairs = json.loads((ROOT / "eval" / "gec.json").read_text(encoding="utf-8"))["pairs"]
    sentences = [s for item in pairs for s in (item["right"], item["wrong"])]

    print("\n比对 ONNX 与 PyTorch 的标签是否逐词一致…")
    fp32_session = ort.InferenceSession(str(fp32), providers=["CPUExecutionProvider"])
    mismatch = 0
    for sentence in sentences:
        # 这一步刻意用朴素分词：要验的是「导出有没有改变模型的行为」，
        # 两边必须喂完全一样的输入，不能混进标点拆分这个变量。
        if tag(model, tokenizer, id2label, sentence) != naive_onnx_tag(fp32_session, tokenizer, id2label, sentence):
            mismatch += 1
            print(f"  [不一致] {sentence}")
    if mismatch:
        raise SystemExit(f"{mismatch}/{len(sentences)} 句对不上，导出有问题，别往下走")
    print(f"  {len(sentences)}/{len(sentences)} 句完全一致")

    print(f"\n动态量化到 int8 …")
    from onnxruntime.quantization import QuantType, quantize_dynamic

    quantize_dynamic(fp32, int8, weight_type=QuantType.QInt8)
    print(f"  {int8.stat().st_size / 1e6:.0f} MB（fp32 是 {fp32.stat().st_size / 1e6:.0f} MB）")

    int8_session = ort.InferenceSession(str(int8), providers=["CPUExecutionProvider"])
    drift = sum(
        naive_onnx_tag(fp32_session, tokenizer, id2label, s)
        != naive_onnx_tag(int8_session, tokenizer, id2label, s)
        for s in sentences
    )
    print(f"  量化后有 {drift}/{len(sentences)} 句标签发生变化")

    print("\n42 组题重新判分：")
    for name, session in (("fp32", fp32_session), ("int8", int8_session)):
        hits, by_cat, per_sentence = accuracy(
            lambda s, sess=session: onnx_tag(sess, tokenizer, id2label, s), pairs
        )
        print(f"  {name}: {hits}/{len(pairs)} ({hits / len(pairs):.0%})，每句 {per_sentence * 1000:.0f} ms")
        for cat, (hit, n) in sorted(by_cat.items(), key=lambda kv: -kv[1][0] / kv[1][1]):
            print(f"      {cat:8} {hit}/{n}")

    # config 的 id2label 有 5002 项，最后一项是 <PAD>——那是训练时给 loss 屏蔽用的，
    # 模型的输出头只有 5001 类。原样写出去，.NET 端的标签表就会比模型多一行；
    # Python 侧看不出来（argmax 永远取不到最后一个下标），只有到了 C# 才暴雷。
    names = [id2label[i] for i in range(len(id2label))]
    head = fp32_session.get_outputs()[0].shape[-1]
    while len(names) > head:
        dropped = names.pop()
        if dropped not in ("<PAD>", "<OOV>"):
            raise SystemExit(f"标签表比模型多，多出来的却是 {dropped} 而不是 <PAD>，得先搞清楚")

    labels = out / "labels.txt"
    labels.write_text("\n".join(names) + "\n", encoding="utf-8")
    print(f"\n写出 {labels.name}（{len(names)} 行，模型输出 {head} 类）")

    if args.verb_vocab:
        verbs = out / "verb-forms.txt.gz"
        count = write_verb_forms(args.verb_vocab, verbs)
        print(f"写出 {verbs.name}（{count} 个源词，{verbs.stat().st_size / 1024:.0f} KB）")


if __name__ == "__main__":
    main()
