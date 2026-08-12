# -*- coding: utf-8 -*-
"""
TASK-205F 真实 KRC 样本抓取（诊断证据 + KrcLyricsConverter 真实数据冒烟）
=========================================================================
复刻 Helper KugouSourceSearcher 链路：
  1. GET mobilecdn.kugou.com/api/v3/search/song?keyword=晴天 周杰伦 → hash
  2. GET lyrics.kugou.com/search?ver=1&man=yes&client=pc&hash=... → id + accesskey
  3. KRC 解密：XOR(16B key) + SharpZipLib inflate + 去首字符（Decrypter/Krc/Decrypter.cs）
输出：tools/t205f/kugou_krc_real.txt（解密后 KRC 纯文本，供冒烟测试按环境变量读取）
"""
import base64
import json
import sys
import urllib.request
import urllib.parse
import zlib

UA = ("Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36")

# Decrypter/Krc/Decrypter.cs 固定 16 字节 key（与 Helper 源码一致：@Gaw^2tGQ61-ÎÒni）
KRC_KEY = bytes([0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
                 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69])


def get(url):
    req = urllib.request.Request(url, method="GET")
    req.add_header("User-Agent", UA)
    with urllib.request.urlopen(req, timeout=20) as resp:
        return resp.read()


def decrypt_krc(b64data):
    """KRC 解密：Base64 解码 → 跳过前 4 字节头 → XOR → inflate → 去首字符（Helper Decrypter 同算法）。"""
    raw = bytearray(base64.b64decode(b64data))
    if len(raw) >= 4:
        raw = raw[4:]
    for i in range(len(raw)):
        raw[i] ^= KRC_KEY[i % 16]
    decompressed = zlib.decompress(bytes(raw))  # 789c 标准 zlib 头（SharpZipLib Inflater 默认处理）
    text = decompressed.decode("utf-8", errors="replace")
    return text[1:] if text else text  # 去首字符


keyword = sys.argv[1] if len(sys.argv) > 1 else "晴天 周杰伦"

# 1. 搜索 → hash
url = ("http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=" +
       urllib.parse.quote(keyword) + "&page=1&pagesize=20&showtype=1")
data = json.loads(get(url).decode("utf-8", errors="replace"))
infos = data.get("data", {}).get("info", [])
print(f"搜索候选: {len(infos)}")
if not infos:
    sys.exit(1)
first = infos[0]
hash_id = first.get("hash")
print(f"首条: {first.get('songname')} {first.get('singername')} hash={hash_id}")

# 2. 歌词索引 → id + accesskey
url2 = ("https://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword=" +
        urllib.parse.quote(keyword) + "&hash=" + hash_id)
data2 = json.loads(get(url2).decode("utf-8", errors="replace"))
cands = data2.get("candidates", [])
print(f"歌词索引候选: {len(cands)}")
if not cands:
    sys.exit(1)
c = cands[0]
print(f"索引: id={c.get('id')} accesskey={c.get('accesskey')[:8]}...")

# 3. 下载 + 解密 KRC
url3 = "https://lyrics.kugou.com/download?ver=1&client=pc&fmt=krc&charset=utf8&id=" + \
       c.get("id", "") + "&accesskey=" + c.get("accesskey", "")
data3 = json.loads(get(url3).decode("utf-8", errors="replace"))
content = data3.get("content", "")
if not content:
    print("下载无 content:", data3)
    sys.exit(1)
krc = decrypt_krc(content)
print(f"KRC 解密成功, 长度={len(krc)}")
print("---- 前 1200 字符 ----")
print(krc[:1200])

out = r"E:\Code\OH-WorkSpace\ytm-lyrics\tools\t205f\kugou_krc_real.txt"
with open(out, "w", encoding="utf-8") as f:
    f.write(krc)
print(f"\nsaved: {out}")
