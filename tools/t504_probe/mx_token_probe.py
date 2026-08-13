# -*- coding: utf-8 -*-
"""
TASK-504 取证点 2 辅助：Musixmatch token.get 真实响应取证
（为何 Helper GetToken 拿不到 usertoken；顺带确认 crowd.track.translations.get 响应的字段结构）
"""
import json
import random
import string
import urllib.request
import urllib.error

UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36")


def rid():
    code = ""
    while len(code) < 10:
        code += "".join(random.choices(string.ascii_lowercase + string.digits, k=10))
    return code


def get(url):
    req = urllib.request.Request(url)
    req.add_header("User-Agent", UA)
    with urllib.request.urlopen(req, timeout=25) as resp:
        return resp.read().decode("utf-8", errors="replace")


print("===== 1. token.get =====")
try:
    body = get(f"https://apic-desktop.musixmatch.com/ws/1.1/token.get?app_id=web-desktop-app-v1.0&t={rid()}")
    print(body[:1200])
except Exception as ex:
    print(f"token.get 失败: {type(ex).__name__}: {ex}")
print()

print("===== 2. crowd.track.translations.get（Shape of You trackId=381381468）=====")
try:
    body = get("https://apic-desktop.musixmatch.com/ws/1.1/crowd.track.translations.get"
               "?translation_fields_set=minimal&selected_language=zh&track_id=381381468"
               f"&comment_format=text&part=user&usertoken=&format=json&app_id=web-desktop-app-v1.0&t={rid()}")
    print(body[:2000])
except Exception as ex:
    print(f"translations.get 失败: {type(ex).__name__}: {ex}")
