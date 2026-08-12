#!/usr/bin/env python3
"""TASK-104a 端到端自测编排（自测 2）。

流程：e2e_host(17865) + http.server(8089) + headless Chrome 加载 tools/fake-ytm.html
  phase1: 自动行为序列(play/seek/pause/play/切歌1重建元素/切歌2降级/切歌3真机video+mediaSession/切歌4多元素选活跃者) + 2s 心跳
  phase2: kill host（断线）→ 8s 退避窗口 → 重启 host → 探针重连成功、心跳恢复
运行: python tools/run_e2e.py
"""
import os
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # 仓库根
TOOLS = os.path.join(ROOT, 'tools')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
PROFILE = os.path.join(os.environ['TEMP'], 'ytm-e2e-profile')
if os.path.exists(PROFILE):
    shutil.rmtree(PROFILE, ignore_errors=True)

NULL = subprocess.DEVNULL
HOST_LOG = open(os.path.join(TOOLS, 'e2e_host.out.log'), 'a', encoding='utf-8', buffering=1)


def start(args, cwd=None, out=NULL, err=NULL):
    return subprocess.Popen(args, cwd=cwd, stdout=out, stderr=err,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))


def kill(p):
    try:
        p.kill()
    except Exception:
        pass


print('[e2e] start', flush=True)
# 1. e2e host（等价宿主，TDD §4 协议）
h1 = start([sys.executable, os.path.join(TOOLS, 'e2e_host.py'), '17865'], TOOLS, HOST_LOG,
           open(os.path.join(TOOLS, 'e2e_host.err.log'), 'w', encoding='utf-8'))
time.sleep(1)
# 2. 静态 http server（根=仓库根：/probe/... 与 /tools/fake-ytm.html 均可访问）
h2 = start([sys.executable, '-m', 'http.server', '8089', '--bind', '127.0.0.1', '--directory', ROOT],
           ROOT, open(os.path.join(TOOLS, 'http.out.log'), 'w', encoding='utf-8'),
           open(os.path.join(TOOLS, 'http.err.log'), 'w', encoding='utf-8'))
time.sleep(1)
# 3. headless Chrome 加载模拟页（页面内 <script src=probe> 注入探针）
h3 = start([CHROME,
            '--headless=new', '--disable-gpu', '--autoplay-policy=no-user-gesture-required',
            f'--user-data-dir={PROFILE}', '--window-size=480,760',
            '--no-first-run', '--disable-extensions',
            'http://127.0.0.1:8089/tools/fake-ytm.html'])

print('[e2e] phase1: 行为序列 + 心跳 (20s, 含切歌3/4 新场景)', flush=True)
time.sleep(20)
print('[e2e] 断线: killing e2e host', flush=True)
kill(h1)
time.sleep(8)  # 探针指数退避窗口 1s→2s→4s→8s

# 4. 重启 host → 探针应重连成功
h4 = start([sys.executable, os.path.join(TOOLS, 'e2e_host.py'), '17865'], TOOLS, HOST_LOG,
           open(os.path.join(TOOLS, 'e2e_host.err2.log'), 'w', encoding='utf-8'))
print('[e2e] phase2: 等待退避重连 + 心跳恢复 (12s)', flush=True)
time.sleep(12)

print('[e2e] cleanup', flush=True)
for p in (h3, h2, h4):
    kill(p)
time.sleep(1)
print('[e2e] done. frames log: tools/e2e_host.out.log', flush=True)
