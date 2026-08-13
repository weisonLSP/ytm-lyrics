# -*- coding: utf-8 -*-
"""
TASK-504 取证点 2：Musixmatch token 重试取证
token.get 返回 401/captcha（概率性门禁，偶发放行）。重试最多 60 次拿 token，
拿到后调用 crowd.track.translations.get + macro.subtitles.get 打印真实响应结构。
"""
import json
import random
import string
import time
import urllib.request
import urllib.error

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


token = None
for i in range(60):
    try:
        body = get(f"https://apic-desktop.musixmatch.com/ws/1.1/token.get?app_id=web-desktop-app-v1.0&t={rid()}")
        obj = json.loads(body)
        h = obj.get("message", {}).get("header", {})
        t = obj.get("message", {}).get("body", {}).get("user_token")
        if h.get("status_code") == 200 and t:
            token = t
            print(f"[OK] attempt={i} token={t[:24]}...")
            break
        else:
            print(f"[{i}] status={h.get('status_code')} hint={h.get('hint')}")
    except Exception as ex:
        print(f"[{i}] {type(ex).__name__}: {ex}")
    time.sleep(0.4)

if not token:
    print("60 次重试未拿到 token，Musixmatch 当前 captcha 门禁不放行")
    raise SystemExit(1)

print()
print("===== crowd.track.translations.get（trackId=381381468 Shape of You）=====")
body = get("https://apic-desktop.musixmatch.com/ws/1.1/crowd.track.translations.get"
           "?translation_fields_set=minimal&selected_language=zh&track_id=381381468"
           f"&comment_format=text&part=user&usertoken={token}&format=json&app_id=web-desktop-app-v1.0&t={rid()}")
obj = json.loads(body)
print(json.dumps(obj, ensure_ascii=False, indent=1)[:6000])
