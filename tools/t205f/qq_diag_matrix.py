# -*- coding: utf-8 -*-
"""
TASK-205F-3 QQ 诊断 · 对照实验矩阵
==================================
第一轮已确认：Helper 原始构造 → req_1.code=2001, song.list=[], sum=0。
本脚本系统化尝试参数变体，定位使 code=0 且 song.list 非空的构造。

实验矩阵（method/module × 附加参数 × URL 形态）：
  A. 原始构造（对照组，预期 2001）
  B. +comm 块（ct/cv/uin）           —— musicu.fcg 大多数业务调用带 comm
  C. param 加 g_tk/loginUin/hostUin/needNewCode/platform（旧 fcg 参数族）
  D. method 改 DoSearchForQQMusic（无 Desktop 后缀，老方法名）
  E. 换模块 music.search.SearchCgiService → method GlobalSearch（新接口名）
  F. URL 带查询串 ?g_tk=5381&format=json&inCharset=utf8&outCharset=utf-8
  G. c.y.qq.com 老接口 fcg_v8_search?w=...&t=0&p=1&n=20
"""
import json
import sys
import urllib.request
import urllib.error

KEYWORD = sys.argv[1] if len(sys.argv) > 1 else "晴天 周杰伦"
UA = ("Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36")
REFER = "https://c.y.qq.com/"

SEARCH_MODULE = {
    "method": "DoSearchForQQMusicDesktop",
    "module": "music.search.SearchCgiService",
    "param": {
        "num_per_page": "20",
        "page_num": "1",
        "query": KEYWORD,
        "search_type": 0,
    },
}


def post(url, obj):
    req = urllib.request.Request(url, data=json.dumps(obj).encode("utf-8"), method="POST")
    req.add_header("User-Agent", UA)
    req.add_header("Referer", REFER)
    req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, timeout=20) as resp:
        return resp.read().decode("utf-8", errors="replace")


def get(url):
    req = urllib.request.Request(url, method="GET")
    req.add_header("User-Agent", UA)
    req.add_header("Referer", REFER)
    with urllib.request.urlopen(req, timeout=20) as resp:
        return resp.read().decode("utf-8", errors="replace")


def summarize(raw):
    """返回 (req_1.code, song数量, sum, 截断响应)。"""
    try:
        j = json.loads(raw)
    except Exception:
        return None, None, None, raw[:200]
    req1 = j.get("req_1") or {}
    data = req1.get("data") or {}
    song = (data.get("body") or {}).get("song") or {}
    meta = data.get("meta") or {}
    n = len(song.get("list") or [])
    return req1.get("code"), n, meta.get("sum"), raw[:300]


experiments = []

# A. 原始（对照组）
experiments.append(("A 原始构造(对照组)", "https://u.y.qq.com/cgi-bin/musicu.fcg",
                    {"req_1": SEARCH_MODULE}))

# B. +comm 块
b = {"req_1": SEARCH_MODULE, "comm": {"ct": 24, "cv": 0, "uin": 0}}
experiments.append(("B 原始+comm块", "https://u.y.qq.com/cgi-bin/musicu.fcg", b))

# C. param 加 fcg 参数族
c = json.loads(json.dumps(SEARCH_MODULE))
c["param"].update({"g_tk": "5381", "loginUin": "0", "hostUin": "0",
                   "needNewCode": "0", "platform": "yqq"})
experiments.append(("C param+fcg参数族", "https://u.y.qq.com/cgi-bin/musicu.fcg",
                    {"req_1": c}))

# D. method 无 Desktop 后缀
d = json.loads(json.dumps(SEARCH_MODULE))
d["method"] = "DoSearchForQQMusic"
experiments.append(("D method=DoSearchForQQMusic", "https://u.y.qq.com/cgi-bin/musicu.fcg",
                    {"req_1": d}))

# E. GlobalSearch 方法名
e = json.loads(json.dumps(SEARCH_MODULE))
e["method"] = "GlobalSearch"
experiments.append(("E method=GlobalSearch", "https://u.y.qq.com/cgi-bin/musicu.fcg",
                    {"req_1": e}))

# F. URL 带查询串
experiments.append(("F URL带查询串", "https://u.y.qq.com/cgi-bin/musicu.fcg?g_tk=5381&format=json&inCharset=utf8&outCharset=utf-8",
                    {"req_1": SEARCH_MODULE}))

# G. c.y.qq.com 老 GET 接口
from urllib.parse import quote
g_url = f"https://c.y.qq.com/soso/fcgi-bin/client_search_cp?w={quote(KEYWORD)}&t=0&p=1&n=20&format=json"
experiments.append(("G c.y.qq.com client_search_cp(GET)", g_url, None))

for name, url, obj in experiments:
    print(f"===== {name} =====")
    try:
        if obj is None:
            raw = get(url)
        else:
            raw = post(url, obj)
        code, n, s, preview = summarize(raw)
        print(f"  req_1.code={code}  song.list={n}  sum={s}")
        print(f"  preview: {preview}")
    except Exception as ex:
        print(f"  FAIL {type(ex).__name__}: {ex}")
    print()
