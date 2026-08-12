# -*- coding: utf-8 -*-
"""
TASK-205F-3 QQ 诊断 · 第四轮：取词接口验证
==========================================
搜索接口结论已定：musicu.fcg DoSearchForQQMusicDesktop 模块级失效（ein=20），
client_search_cp 老接口正常。本轮验证取词接口 lyric_download.fcg 是否存活——
若取词也挂，光修搜索不完整。

晴天（周杰伦）songid=97773 来自 client_search_cp 结果（已验证存在）。
"""
import json
import urllib.request
import urllib.error

UA = ("Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36")
REFER = "https://c.y.qq.com/"
SID = "97773"  # 晴天 周杰伦（client_search_cp 返回的 songid）

# ---- 1. GET 形态 ----
print("===== 1. GET lyric_download.fcg =====")
g_url = (f"https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg?"
         f"version=15&miniversion=82&lrctype=4&musicid={SID}")
try:
    req = urllib.request.Request(g_url, method="GET")
    req.add_header("User-Agent", UA)
    req.add_header("Referer", REFER)
    with urllib.request.urlopen(req, timeout=20) as resp:
        raw = resp.read().decode("utf-8", errors="replace")
    print(f"status=200 len={len(raw)}")
    print(raw[:800])
except Exception as ex:
    print(f"GET 失败: {type(ex).__name__}: {ex}")
print()

# ---- 2. POST 形态（Helper 原始用法）----
print("===== 2. POST lyric_download.fcg（Helper 原始构造）=====")
try:
    form = urllib.parse.urlencode({
        "version": "15", "miniversion": "82",
        "lrctype": "4", "musicid": SID}).encode("utf-8")
    req = urllib.request.Request(
        "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg",
        data=form, method="POST")
    req.add_header("User-Agent", UA)
    req.add_header("Referer", REFER)
    req.add_header("Content-Type", "application/x-www-form-urlencoded")
    with urllib.request.urlopen(req, timeout=20) as resp:
        raw = resp.read().decode("utf-8", errors="replace")
    print(f"status=200 len={len(raw)}")
    print(raw[:800])
except Exception as ex:
    print(f"POST 失败: {type(ex).__name__}: {ex}")
