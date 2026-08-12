# -*- coding: utf-8 -*-
"""
TASK-205F-3 QQ 诊断 · 第三轮：query 形态 × musicu/老接口 交叉验证
================================================================
第二轮结论：
  - musicu.fcg + comm → code=0 但 song.list=[]（sum=0, estimate_sum=73193, ein=20, is_filter=-2）
  - client_search_cp（GET 老接口）→ 20 条数据正常

本轮：musicu.fcg 换不同 query 形态，判断是 query 问题还是接口问题；
      并复核 client_search_cp 的多 query 形态稳定性。
"""
import json
import urllib.request
import urllib.parse

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


def musicu(query):
    obj = {
        "comm": {"ct": 24, "cv": 0, "uin": 0},
        "req_1": {"method": "DoSearchForQQMusicDesktop",
                  "module": "music.search.SearchCgiService",
                  "param": {"num_per_page": 20, "page_num": 1,
                            "query": query, "search_type": 0}},
    }
    raw = post("https://u.y.qq.com/cgi-bin/musicu.fcg", obj)
    j = json.loads(raw)
    req1 = j.get("req_1") or {}
    data = req1.get("data") or {}
    song = (data.get("body") or {}).get("song") or {}
    meta = data.get("meta") or {}
    return req1.get("code"), len(song.get("list") or []), meta.get("sum"), meta.get("ein")


def old_cp(query):
    url = ("https://c.y.qq.com/soso/fcgi-bin/client_search_cp?"
           f"w={urllib.parse.quote(query)}&t=0&p=1&n=20&format=json&cr=1&aggr=1")
    raw = get(url)
    j = json.loads(raw)
    data = j.get("data") or {}
    song = data.get("song") or {}
    return song.get("curnum"), len(song.get("list") or []), song.get("totalnum")


queries = ["晴天 周杰伦", "晴天", "周杰伦", "晴天周杰伦", "夜曲 周杰伦", "起风了 买辣椒也用券"]

print(f"{'query':<18} {'musicu(code/list/sum/ein)':<30} {'old_cp(curnum/list/total)':<26}")
for q in queries:
    mc = musicu(q)
    oc = old_cp(q)
    print(f"{q:<18} {'/'.join(str(x) for x in mc):<30} {'/'.join(str(x) for x in oc):<26}")
