#!/usr/bin/env python3
"""TASK-102 一次性端到端冒烟探针：向 ws://127.0.0.1:17865 推送模拟 state 帧序列。

纯标准库实现 WebSocket 客户端（RFC6455）：握手、掩码帧发送、服务端帧接收（含 ping→pong 应答）。
配合 tools/smoke-host 控制台宿主验证全链路：hello 应答、state 快照发布、seq 乱序丢弃、
暂停冻结、切歌 TrackChanged、error 帧仅记日志。

用法: python probe_smoke.py [port]   (默认 17865)
"""
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time

HOST = "127.0.0.1"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 17865


def _read_exact(sock: socket.socket, n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("socket closed")
        buf += chunk
    return buf


def build_frame(opcode: int, payload: bytes) -> bytes:
    """客户端 → 服务端帧：必须掩码。"""
    mask = os.urandom(4)
    length = len(payload)
    if length < 126:
        header = bytes([0x80 | opcode, 0x80 | length])
    elif length < 65536:
        header = bytes([0x80 | opcode, 0x80 | 126]) + struct.pack(">H", length)
    else:
        header = bytes([0x80 | opcode, 0x80 | 127]) + struct.pack(">Q", length)
    masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    return header + mask + masked


def recv_frame(sock: socket.socket):
    """服务端帧（无掩码）。返回 (opcode, payload)。"""
    b1, b2 = _read_exact(sock, 2)
    opcode = b1 & 0x0F
    length = b2 & 0x7F
    if length == 126:
        length = struct.unpack(">H", _read_exact(sock, 2))[0]
    elif length == 127:
        length = struct.unpack(">Q", _read_exact(sock, 8))[0]
    masked = b2 & 0x80
    mask = _read_exact(sock, 4) if masked else None
    payload = _read_exact(sock, length)
    if mask:
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    return opcode, payload


def connect() -> socket.socket:
    sock = socket.create_connection((HOST, PORT), timeout=5)
    key = base64.b64encode(os.urandom(16)).decode()
    request = (
        f"GET / HTTP/1.1\r\n"
        f"Host: {HOST}:{PORT}\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "Sec-WebSocket-Version: 13\r\n\r\n"
    )
    sock.sendall(request.encode())
    resp = b""
    while b"\r\n\r\n" not in resp:
        resp += sock.recv(4096)
    status = resp.split(b"\r\n", 1)[0].decode()
    print(f"[probe] handshake: {status}")
    assert "101" in status, f"handshake failed: {status}"
    return sock


def send_text(sock: socket.socket, text: str):
    sock.sendall(build_frame(1, text.encode()))


def recv_until_hello(sock: socket.socket, deadline: float):
    """收帧直到 hello；期间应答 ping。"""
    while time.monotonic() < deadline:
        opcode, payload = recv_frame(sock)
        if opcode == 9:  # ping → pong
            sock.sendall(build_frame(10, payload))
            continue
        if opcode == 1:
            text = payload.decode()
            print(f"[probe] << {text}")
            if '"hello"' in text:
                return
        elif opcode == 8:
            raise ConnectionError("server closed connection")
    raise TimeoutError("hello not received")


def state(seq: int, title: str, artist: str, duration: float, position: float, playing: bool) -> str:
    return json.dumps({
        "type": "state", "seq": seq,
        "track": {"title": title, "artist": artist, "duration": duration},
        "position": position, "playing": playing,
        "sentAt": int(time.time() * 1000),
    }, ensure_ascii=False)


def main():
    print(f"[probe] connecting ws://{HOST}:{PORT}")
    sock = connect()
    recv_until_hello(sock, time.monotonic() + 5)
    print("[probe] hello received, pushing state frames...\n")

    frames = [
        # (seq, 说明) —— 播放中，位置前进模拟 2s 心跳
        (state(1, "原神 - 尘世闲游", "HoYo-MiX", 234.5, 10.0, True),   "播放 10.0s"),
        (state(2, "原神 - 尘世闲游", "HoYo-MiX", 234.5, 12.0, True),   "心跳 12.0s"),
        # 乱序：seq=4 先于 seq=3，seq=3 应被丢弃
        (state(4, "原神 - 尘世闲游", "HoYo-MiX", 234.5, 14.0, True),   "乱序 seq=4 14.0s"),
        (state(3, "原神 - 尘世闲游", "HoYo-MiX", 234.5, 13.5, True),   "乱序 seq=3 → 应丢弃"),
        # 暂停
        (state(5, "原神 - 尘世闲游", "HoYo-MiX", 234.5, 14.2, False),  "暂停 14.2s"),
        # 切歌
        (state(6, "别怕变老", "王以太/艾热AIR", 210.0, 0.5, True),     "切歌 0.5s"),
        # error 帧：仅日志
        (json.dumps({"type": "error", "code": "E_TEST", "message": "smoke test error"}), "error 帧"),
        # 稳态心跳
        (state(7, "别怕变老", "王以太/艾热AIR", 210.0, 2.0, True),     "心跳 2.0s"),
    ]

    for payload, note in frames:
        print(f"[probe] >> seq 帧 ({note})")
        send_text(sock, payload)
        time.sleep(0.6)  # 模拟帧间隔，让服务端外推可见

    # 再发一轮心跳观察外推，随后收尾
    send_text(sock, state(8, "别怕变老", "王以太/艾热AIR", 210.0, 4.0, True))
    print("[probe] >> 心跳 seq=8 4.0s")
    time.sleep(1.5)

    sock.sendall(build_frame(8, struct.pack(">H", 1000)))  # Close 帧
    print("[probe] closing")
    sock.close()


if __name__ == "__main__":
    main()
