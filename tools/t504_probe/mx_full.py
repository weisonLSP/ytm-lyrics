# -*- coding: utf-8 -*-
"""
TASK-504 取证点 2 完整版：
1) 重试拿 token
2) 多首候选歌找有 zh 翻译的：translations_list 非空
3) 打印翻译条目完整字段（description/snippet/matched_line/subtitle_matched_line/...）
4) macro.subtitles.get(subtitle_format=lrc) 打印 subtitle_body 前若干行，
   判断 matched_line 匹配的是 subtitle 行还是 richsync 行
"""
import json
import random
import string
import time
import urllib.request
import urllib.error
import urllib.parse

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36")
REFER = "https://www.musixmatch.com/"


def rid():
    code = ""
    while len(code) < 10:
        code += "".join(random.choices(string.ascii_lowercase + string.digits, k=10))
    return code


def get(url):
    req = urllib.request.Request(url)
    req.add_header("User-Agent", UA)
    req.add_header("Referer", REFER)
    with urllib.request.urlopen(req, timeout=25) as resp:
        return resp.read().decode("utf-8", errors="replace")


def get_token():
    for i in range(80):
        try:
            body = get(f"https://apic-desktop.musixmatch.com/ws/1.1/token.get?app_id=web-desktop-app-v1.0&t={rid()}")
            obj = json.loads(body)
            h = obj.get("message", {}).get("header", {})
            t = obj.get("message", {}).get("body", {}).get("user_token")
            if h.get("status_code") == 200 and t:
                print(f"[OK] token attempt={i}")
                return t
        except Exception as ex:
            print(f"[token {i}] {type(ex).__name__}")
        time.sleep(0.4)
    raise SystemExit("token 拿不到")


def api(req, token, extra=""):
    return get("https://apic-desktop.musixmatch.com/ws/1.1/" + req +
               f"&usertoken={token}&format=json&app_id=web-desktop-app-v1.0&t={rid()}" + extra)


token = get_token()

candidates = [
    ("Lemon", "Kenshi Yonezu"),
    ("My Heart Will Go On", "Celine Dion"),
    ("Yesterday", "The Beatles"),
    ("Dynamite", "BTS"),
    ("How You Like That", "BLACKPINK"),
    ("Gangnam Style", "PSY"),
    ("Despacito", "Luis Fonsi"),
    ("Faded", "Alan Walker"),
]

for track, artist in candidates:
    q = "q_track=" + urllib.parse.quote(track) + "&q_artist=" + urllib.parse.quote(artist)
    try:
        body = api("matcher.track.get?" + q, token)
        obj = json.loads(body)
        trk = obj.get("message", {}).get("body", {}).get("track", {})
        tid = trk.get("track_id")
        name = trk.get("track_name")
    except Exception as ex:
        print(f"[{track}] matcher 失败: {type(ex).__name__}: {ex}")
        continue
    if not tid:
        print(f"[{track}] 无 track_id: {body[:200]}")
        continue

    print(f"\n===== {name} track_id={tid} =====")
    try:
        tbody = api(f"crowd.track.translations.get?translation_fields_set=minimal&selected_language=zh&track_id={tid}&comment_format=text&part=user", token)
        tobj = json.loads(tbody)
        lst = tobj.get("message", {}).get("body", {}).get("translations_list") or []
        print(f"translations_list len={len(lst)}")
        if not lst:
            continue
        print("----- 首条翻译完整字段 -----")
        print(json.dumps(lst[0], ensure_ascii=False, indent=1))
        print("----- 前 6 条 matched_line vs description vs snippet -----")
        for item in lst[:6]:
            t = item.get("translation", {})
            print(f"  matched_line : {t.get('matched_line')!r}")
            print(f"  description  : {t.get('description')!r}")
            print(f"  snippet      : {t.get('snippet')!r}")
            print(f"  subtitle_line: {t.get('subtitle_matched_line')!r}")
            print()
        # 取 subtitle_body 对比
        try:
            sbody = api(f"macro.subtitles.get?namespace=lyrics_richsynched&optional_calls=track.richsync&subtitle_format=lrc&track_id={tid}&f_subtitle_length_max_deviation=40", token)
            sobj = json.loads(sbody)
            mac = sobj.get("message", {}).get("body", {}).get("macro_calls", {})
            sub = mac.get("macro.subtitles.get", {}).get("message", {}).get("body", {}).get("subtitle", {})
            sub_body = sub.get("subtitle_body") or ""
            print(f"----- subtitle_body（LRC 格式）前 12 行 -----")
            for line in sub_body.split("\n")[:12]:
                print("  |" + line)
            # richsync body
            rich = mac.get("track.richsync.get", {}).get("message", {}).get("body", {}).get("richsync", {})
            rich_body = rich.get("richsync_body") or ""
            print(f"----- richsync_body 前 300 字符 -----")
            print("  |" + rich_body[:300].replace("\n", "\n  |"))
            break  # 一首有翻译的即可
        except Exception as ex:
            print(f"  subtitle 获取失败: {type(ex).__name__}: {ex}")
            continue
    except Exception as ex:
        print(f"  translations 失败: {type(ex).__name__}: {ex}")
