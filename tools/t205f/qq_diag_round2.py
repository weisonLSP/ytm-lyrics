# -*- coding: utf-8 -*-
"""
TASK-205F-3 QQ 诊断 · 第二轮
============================
第一轮结论：
  - Helper 原始构造（无 comm）→ req_1.code=2001，song.list=[]（真机"搜索无数据"根因）
  - +comm 块 → code=0 但 sum=0，仍无数据（可疑）
  - c.y.qq.com/soso/fcgi-bin/client_search_cp GET 老接口 → curnum=20 有数据

本轮目标：
  1. 打印带 comm 的完整响应，弄清 sum=0 的原因
  2. 试 comm+int 参数组合
  3. 打印 client_search_cp 完整 song.list 字段结构（适配器要用的字段）
"""
import json
import urllib.request
import urllib.parse

KEYWORD = "晴天 周杰伦"
UA = ("Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36")
REFER = "https://c.y.qq.com/"


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


def search_param(**extra):
    p = {"num_per_page": 20, "page_num": 1, "query": KEYWORD, "search_type": 0}
    p.update(extra)
    return p


# ---- 1. comm 完整响应 ----
comm_basic = {
    "comm": {"ct": 24, "cv": 0, "uin": 0},
    "req_1": {"method": "DoSearchForQQMusicDesktop",
              "module": "music.search.SearchCgiService",
              "param": search_param()},
}
raw = post("https://u.y.qq.com/cgi-bin/musicu.fcg", comm_basic)
j = json.loads(raw)
print("===== 1. comm(int参数) 完整响应 =====")
print(json.dumps(j, ensure_ascii=False, indent=1)[:2500])
print()

# ---- 2. comm 变体：无 uin / ct=0 ----
for name, comm in [("comm ct=0", {"comm": {"ct": 0, "cv": 0}}),
                   ("comm ct=24 no uin", {"comm": {"ct": 24, "cv": 0}}),
                   ("comm 全量", {"comm": {"ct": 24, "cv": 0, "uin": 0, "format": "json"}})]:
    obj = {"comm": comm["comm"], "req_1": {"method": "DoSearchForQQMusicDesktop",
                                           "module": "music.search.SearchCgiService",
                                           "param": search_param()}}
    raw = post("https://u.y.qq.com/cgi-bin/musicu.fcg", obj)
    jj = json.loads(raw)
    req1 = jj.get("req_1") or {}
    data = req1.get("data") or {}
    song = (data.get("body") or {}).get("song") or {}
    meta = data.get("meta") or {}
    print(f"===== 2.{name} =====")
    print(f"  req_1.code={req1.get('code')} song.list={len(song.get('list') or [])} "
          f"sum={meta.get('sum')} perpage={meta.get('perpage')}")
    print()

# ---- 3. client_search_cp 完整 song.list 结构 ----
g_url = ("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?"
         f"w={urllib.parse.quote(KEYWORD)}&t=0&p=1&n=20&format=json")
raw = get(g_url)
jg = json.loads(raw)
print("===== 3. client_search_cp song.list 字段 =====")
songs = (jg.get("data") or {}).get("song") or {}
lst = songs.get("list") or []
print(f"curnum={songs.get('curnum')} totalnum={songs.get('totalnum')} list={len(lst)}")
if lst:
    print("首条字段键:", sorted(lst[0].keys()))
    print(json.dumps(lst[0], ensure_ascii=False, indent=1)[:1200])
print()

# ---- 4. client_search_cp 变体：t=1（单曲）+ 字段裁剪 ----
g2 = ("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?"
      f"w={urllib.parse.quote(KEYWORD)}&t=1&p=1&n=20&format=json&cr=1&aggr=1")
raw = get(g2)
jg2 = json.loads(raw)
songs2 = (jg2.get("data") or {}).get("song") or {}
lst2 = songs2.get("list") or []
print("===== 4. client_search_cp t=1 变体 =====")
print(f"curnum={songs2.get('curnum')} totalnum={songs2.get('totalnum')} list={len(lst2)}")
if lst2:
    s = lst2[0]
    print("songid:", s.get("songid"), "| songmid:", s.get("songmid"),
          "| songname:", s.get("songname"), "| interval:", s.get("interval"))
    singers = [x.get("name") for x in (s.get("singer") or [])]
    print("singer:", singers, "| albumname:", s.get("albumname"))
print()

# ---- 5. 歌词接口验证：lyric_download.fcg 还能用吗（取词路径）----
# 先拿一个 songid（client_search_cp 里有）
sid = None
if lst2:
    sid = lst2[0].get("songid")
    mid = lst2[0].get("songmid")
    if sid:
        lurl = ("https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg?"
                f"version=15&miniversion=82&lrctype=4&musicid={sid}")
        req = urllib.request.Request(lurl, method="GET")
        req.add_header("User-Agent", UA)
        req.add_header("Referer", REFER)
        try:
            with urllib.request.urlopen(req, timeout=20) as resp:
                lraw = resp.read().decode("utf-8", errors="replace")
            print("===== 5. lyric_download.fcg (GET) =====")
            print(f"musicid={sid} len={len(lraw)}")
            print(lraw[:500])
        except Exception as ex:
            print(f"lyric_download GET 失败: {type(ex).__name__}: {ex}")
