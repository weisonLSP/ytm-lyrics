# -*- coding: utf-8 -*-
"""
TASK-205F-3 QQ 搜索"无数据"诊断
================================
复刻 Helper `QQMusic.Api.Search(keyword, SONG_ID)` 的请求构造（api-map §2.2）：
  - URL:  https://u.y.qq.com/cgi-bin/musicu.fcg
  - POST JSON body: {"req_1": {"method": "DoSearchForQQMusicDesktop",
                               "module": "music.search.SearchCgiService",
                               "param": {"num_per_page": "20", "page_num": "1",
                                         "query": KEYWORD, "search_type": 0}}}
  - 请求头: User-Agent=Chrome 63 样、Referer=https://c.y.qq.com/（Helper BaseApi 默认）

沙箱 Schannel TLS 不可用，.NET 出网失败；Python ssl 走 OpenSSL 可出网（ADR-11），
本脚本即真实源验证通道。诊断目标：确认接口当前返回结构，定位 candidates=0 根因。

用法: python qq_diag.py ["关键词"]
"""
import json
import sys
import urllib.request
import urllib.error

KEYWORD = sys.argv[1] if len(sys.argv) > 1 else "晴天 周杰伦"
URL = "https://u.y.qq.com/cgi-bin/musicu.fcg"
UA = ("Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36")
REFER = "https://c.y.qq.com/"

body = {
    "req_1": {
        "method": "DoSearchForQQMusicDesktop",
        "module": "music.search.SearchCgiService",
        "param": {
            "num_per_page": "20",
            "page_num": "1",
            "query": KEYWORD,
            "search_type": 0,
        },
    }
}

payload = json.dumps(body, ensure_ascii=False).encode("utf-8")
req = urllib.request.Request(URL, data=payload, method="POST")
req.add_header("User-Agent", UA)
req.add_header("Referer", REFER)
req.add_header("Content-Type", "application/json")

print(f"== request ==")
print(f"URL : {URL}")
print(f"BODY: {payload.decode('utf-8')}")
print(f"UA  : {UA}")
print(f"REF : {REFER}")
print()

try:
    with urllib.request.urlopen(req, timeout=20) as resp:
        status = resp.status
        raw = resp.read().decode("utf-8", errors="replace")
except urllib.error.HTTPError as e:
    print(f"HTTP {e.code}: {e.reason}")
    raw = e.read().decode("utf-8", errors="replace")
    status = e.code
except Exception as e:
    print(f"NETWORK ERROR: {type(e).__name__}: {e}")
    sys.exit(1)

print(f"== response (status={status}, len={len(raw)}) ==")
print(raw[:2000])
print()

# ---- 结构化解读：Helper 解析路径 result?.Req_1?.Data?.Body?.Song?.List ----
try:
    j = json.loads(raw)
except Exception as e:
    print(f"响应非 JSON: {e}")
    sys.exit(0)

print("== structured ==")
print(f"top.code   = {j.get('code')}")
req1 = j.get("req_1") or {}
print(f"req_1.code = {req1.get('code')}")
print(f"req_1.msg  = {req1.get('message')} (若存在)")
data = req1.get("data") or {}
print(f"data.code  = {data.get('code')}")
body1 = data.get("body") or {}
song = body1.get("song") or {}
songs = song.get("list") or []
print(f"body.song.list 存在? {'是' if song else '否'}, 数量 = {len(songs)}")
for s in songs[:5]:
    singer = ",".join(x.get("name", "") for x in (s.get("singer") or []))
    print(f"  - id={s.get('id')} mid={s.get('mid')} title='{s.get('title') or s.get('name')}' "
          f"artist=[{singer}] interval={s.get('interval')}")
print()

# ---- 对照实验 1：num_per_page 用 int（Helper 用 string "20"，接口若要求 int 会 400）----
body2 = json.loads(json.dumps(body))
body2["req_1"]["param"]["num_per_page"] = 20
req2 = urllib.request.Request(URL, data=json.dumps(body2).encode("utf-8"), method="POST")
req2.add_header("User-Agent", UA)
req2.add_header("Referer", REFER)
req2.add_header("Content-Type", "application/json")
try:
    with urllib.request.urlopen(req2, timeout=20) as resp:
        raw2 = resp.read().decode("utf-8", errors="replace")
    j2 = json.loads(raw2)
    req12 = j2.get("req_1") or {}
    d2 = req12.get("data") or {}
    song2 = (d2.get("body") or {}).get("song") or {}
    print(f"== 对照实验 1（num_per_page=int 20）==\nreq_1.code={req12.get('code')} "
          f"song.list 数量 = {len(song2.get('list') or [])}")
except Exception as e:
    print(f"对照实验 1 失败: {type(e).__name__}: {e}")

# ---- 对照实验 2：search_type 用 "0" 字符串（Helper 用 int 0）----
body3 = json.loads(json.dumps(body))
body3["req_1"]["param"]["search_type"] = "0"
req3 = urllib.request.Request(URL, data=json.dumps(body3).encode("utf-8"), method="POST")
req3.add_header("User-Agent", UA)
req3.add_header("Referer", REFER)
req3.add_header("Content-Type", "application/json")
try:
    with urllib.request.urlopen(req3, timeout=20) as resp:
        raw3 = resp.read().decode("utf-8", errors="replace")
    j3 = json.loads(raw3)
    req13 = j3.get("req_1") or {}
    d3 = req13.get("data") or {}
    song3 = (d3.get("body") or {}).get("song") or {}
    print(f"== 对照实验 2（search_type=str '0'）==\nreq_1.code={req13.get('code')} "
          f"song.list 数量 = {len(song3.get('list') or [])}")
except Exception as e:
    print(f"对照实验 2 失败: {type(e).__name__}: {e}")
