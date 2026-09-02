"""
生成 LingoDict 的应用图标：深色圆角方块 + 白色 L + 蓝色重音点。

    python scripts/make-icon.py

写出 src/LingoDict.App/Assets/lingodict.ico。这个文件同时被三处引用：
csproj 的 ApplicationIcon（exe 内嵌资源，任务栏和开始菜单从这里取）、
两个窗口的 Icon、以及 Inno 的 SetupIconFile。

尺寸表来自微软自己的文档（app-icon-construction，「Icon scaling」一节）：

    缩放             100%  125%  150%  200%  250%  300%  400%
    右键菜单/标题栏/托盘   16    20    24    32    40    48    64
    任务栏/搜索结果/所有应用 24    30    36    48    60    72    96
    开始磁贴            32    40    48    64    80    96   256

Windows 先找**完全相同**的尺寸，找不到才取上一档缩下来。所以缺哪一档，
那个界面上的图标就是缩出来的——轻则发虚，重则干脆退回系统默认图标。
150% 缩放下任务栏和搜索结果要的是 36px，这一档以前没有，
而那正好就是最早报出来「图标不对」的两个地方。

**小尺寸必须用 BMP，不能用 PNG。**
ICO 从 Vista 起允许内嵌 PNG，但实际上只有 256×256 那一档是各处都认的。
小尺寸用 PNG 时，ExtractAssociatedIcon 这类 API 读得出来，
资源管理器建图标缓存的那条路径却可能读不出来——症状是 exe 图标看着是好的，
任务栏和开始菜单却显示系统默认图标，而且不报任何错。
所以 ≤96 写 BMP(DIB)，128/256 写 PNG（否则文件会大好几倍）。

没有装 PIL，所以 PNG 和 BMP 都是手写字节。
4 倍超采样后盒式降采样，边缘不会有锯齿。
"""

import math
import pathlib
import struct
import zlib

BG = (0x16, 0x20, 0x3A)      # BrandInk，与顶栏同色
FG = (0xFF, 0xFF, 0xFF)
ACCENT = (0x6C, 0x97, 0xF5)  # 深色主题下的强调色，在深底上够亮

SS = 4  # 超采样倍率上限。大尺寸会自动降档，见 supersampling()

ROOT = pathlib.Path(__file__).resolve().parent.parent
TARGET = ROOT / "src/LingoDict.App/Assets/lingodict.ico"
ICNS_TARGET = ROOT / "src/LingoDict.App/Assets/lingodict.icns"

# 超过这个尺寸才用 PNG。小尺寸一律 BMP，理由见模块开头。
PNG_FROM = 128

# 微软那张表里出现过的每一档，外加 128（缩到 96 和 256 之间时用得上）。
SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256]

# macOS 的 icns。四字符标签就是尺寸的意思，@2x 的那几档和 1x 的像素数一样，
# 但系统按标签挑：少了 ic11/ic12/ic13/ic14，Retina 屏上就是把小图拉大。
# 全部用 PNG 载荷——从 10.7 起 icns 就认 PNG，不必再写 JPEG 2000。
ICNS_ENTRIES = [
    (b"icp4", 16),    # 16x16
    (b"icp5", 32),    # 32x32
    (b"ic11", 32),    # 16x16@2x
    (b"icp6", 64),    # 64x64
    (b"ic12", 64),    # 32x32@2x
    (b"ic07", 128),   # 128x128
    (b"ic08", 256),   # 256x256
    (b"ic13", 256),   # 128x128@2x
    (b"ic09", 512),   # 512x512
    (b"ic14", 512),   # 256x256@2x
    (b"ic10", 1024),  # 512x512@2x
]


def supersampling(size):
    """
    大尺寸降低超采样倍率，把单张的采样点数封在 2048² 以内。

    纯 Python 逐点采样，1024 再乘 4 倍就是 1677 万个点，要跑好几分钟。
    而尺寸越大，超采样本来就越不重要——边缘的锯齿在 1024 上肉眼看不出来。
    ico 用到的尺寸（≤256）算出来仍是 4，和以前完全一致。
    """
    return max(1, min(SS, 2048 // size))


def rounded_rect(px, py, cx, cy, hw, hh, r):
    dx = abs(px - cx) - (hw - r)
    dy = abs(py - cy) - (hh - r)
    return math.hypot(max(dx, 0.0), max(dy, 0.0)) - r <= 0


def sample(u, v):
    """返回该点的 (r, g, b, a)，坐标为 0..1。"""
    if not rounded_rect(u, v, 0.5, 0.5, 0.5, 0.5, 0.22):
        return (0, 0, 0, 0)

    if math.hypot(u - 0.705, v - 0.325) <= 0.080:
        return (*ACCENT, 255)

    # 字母 L：竖笔 + 横笔，各带一点圆角
    stem = rounded_rect(u, v, 0.360, 0.470, 0.060, 0.230, 0.022)
    foot = rounded_rect(u, v, 0.485, 0.640, 0.185, 0.060, 0.022)
    if stem or foot:
        return (*FG, 255)

    return (*BG, 255)


def render(size):
    """自上而下的 RGBA 行。"""
    ss = supersampling(size)
    rows = []
    step = 1.0 / (size * ss)
    for y in range(size):
        row = bytearray()
        for x in range(size):
            r = g = b = a = 0
            for sy in range(ss):
                for sx in range(ss):
                    u = (x * ss + sx + 0.5) * step
                    v = (y * ss + sy + 0.5) * step
                    pr, pg, pb, pa = sample(u, v)
                    r += pr * pa
                    g += pg * pa
                    b += pb * pa
                    a += pa
            if a == 0:
                row += bytes(4)
            else:
                row += bytes((round(r / a), round(g / a), round(b / a), round(a / (ss * ss))))
        rows.append(bytes(row))
    return rows


def png(size, rows):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + row for row in rows)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def bmp(size, rows):
    """
    ICO 里的 BMP：一个 BITMAPINFOHEADER，后面跟 BGRA 像素和一张 AND 掩码。

    两个容易写错的地方：高度要填**两倍**（颜色区 + 掩码区），
    像素要**自下而上**存。少任何一样，图标要么不显示要么上下颠倒。
    """
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        size,        # biWidth
        size * 2,    # biHeight：颜色区 + AND 掩码
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression = BI_RGB
        0, 0, 0, 0, 0,
    )

    pixels = bytearray()
    for row in reversed(rows):
        for x in range(size):
            r, g, b, a = row[x * 4 : x * 4 + 4]
            pixels += bytes((b, g, r, a))

    # 32bpp 有 alpha，AND 掩码用不上了，但结构上必须在，全 0 即可。
    # 每行按 4 字节对齐。
    stride = ((size + 31) // 32) * 4
    mask = bytes(stride * size)

    return header + bytes(pixels) + mask


def icns(images):
    """
    ICNS 容器：'icns' + 总长度，然后一串 [4 字节标签][含头长度][载荷]。

    两个长度都是大端，而且都**包含**自己那 8 个字节的头——少算这 8 字节，
    预览能打开，Finder 和 Dock 却不显示。
    """
    body = b""
    for tag, data in images:
        body += tag + struct.pack(">I", len(data) + 8) + data

    return b"icns" + struct.pack(">I", len(body) + 8) + body


def make_icns():
    # 同一个尺寸出现在多个标签下（如 256 既是 ic08 又是 ic13），只渲染一次
    cache = {}
    images = []
    for tag, size in ICNS_ENTRIES:
        if size not in cache:
            cache[size] = png(size, render(size))
        images.append((tag, cache[size]))

    ICNS_TARGET.parent.mkdir(parents=True, exist_ok=True)
    ICNS_TARGET.write_bytes(icns(images))
    print(
        f"{ICNS_TARGET.relative_to(ROOT)}: {ICNS_TARGET.stat().st_size / 1024:.1f} KiB"
        f"（{len(ICNS_ENTRIES)} 档，最大 {max(s for _, s in ICNS_ENTRIES)}）"
    )


def main():
    sizes = SIZES
    images = []
    for size in sizes:
        rows = render(size)
        images.append(png(size, rows) if size >= PNG_FROM else bmp(size, rows))

    offset = 6 + 16 * len(images)
    entries = b""
    for size, data in zip(sizes, images):
        # 256 在目录里记作 0——这个字段只有一个字节
        entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    TARGET.parent.mkdir(parents=True, exist_ok=True)
    TARGET.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + entries + b"".join(images))

    kinds = ", ".join(f"{s}={'PNG' if s >= PNG_FROM else 'BMP'}" for s in sizes)
    print(f"{TARGET.relative_to(ROOT)}: {TARGET.stat().st_size / 1024:.1f} KiB（{kinds}）")


if __name__ == "__main__":
    main()
    make_icns()
