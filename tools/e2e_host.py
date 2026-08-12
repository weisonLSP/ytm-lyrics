#!/usr/bin/env python3
"""TASK-103 端到端等价宿主：标准库 WebSocket 服务端（RFC6455）。

按 TDD §4 验证探针全链路：连接即回 hello（{"type":"hello","version":"1"}），
收到 state 帧逐条打印并落盘 tools/e2e_frames.log。支持多连接（探针断线重连测试）。

用法: python e2e_host.py [port]   (默认 17865)
"""
import base64
import json
import os
import socket
import struct
import sys
import threading
import time

HOST = "127.0.0.1"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 17865
LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "e2e_frames.log")

_lock = threading.Lock()


def log(line: str):
    ts = time.strftime("%H:%M:%S") + ".%03d" % (int(time.time() * 1000) % 1000)
    out = f"[{ts}] {line}"
    with _lock:
        print(out, flush=True)
        with open(LOG_PATH, "a", encoding="utf-8") as f:
            f.write(out + "\n")


def _read_exact(sock: socket.socket, n: int) -> bytes:
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError("socket closed")
        buf += chunk
    return buf


def build_frame(opcode: int, payload: bytes) -> bytes:
    """服务端帧：不掩码。"""
    length = len(payload)
    if length < 126:
        header = bytes([0x80 | opcode, length])
    elif length < 65536:
        header = bytes([0x80 | opcode, 126]) + struct.pack(">H", length)
    else:
        header = bytes([0x80 | opcode, 127]) + struct.pack(">Q", length)
    return header + payload


def recv_client_frame(sock: socket.socket):
    """客户端帧：带掩码，需要解掩码。返回 (opcode, payload)。"""
    b1, b2 = _read_exact(sock, 2)
    opcode = b1 & 0x0F
    length = b2 & 0x7F
    if length == 126:
        length = struct.unpack(">H", _read_exact(sock, 2))[0]
    elif length == 127:
        length = struct.unpack(">Q", _read_exact(sock, 8))[0]
    mask = _read_exact(sock, 4) if (b2 & 0x80) else None
    payload = _read_exact(sock, length)
    if mask:
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
    return opcode, payload


def handshake(conn: socket.socket) -> str:
    req = b""
    while b"\r\n\r\n" not in req:
        req += conn.recv(4096)
    headers = {}
    for line in req.decode(errors="replace").split("\r\n")[1:]:
        if ":" in line:
            k, v = line.split(":", 1)
            headers[k.strip().lower()] = v.strip()
    key = headers.get("sec-websocket-key", "")
    accept = base64.b64encode(
        __import__("hashlib").sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()
    ).decode()
    conn.sendall(
        ("HTTP/1.1 101 Switching Protocols\r\n"
         "Upgrade: websocket\r\n"
         "Connection: Upgrade\r\n"
         f"Sec-WebSocket-Accept: {accept}\r\n\r\n").encode()
    )
    return headers.get("sec-websocket-protocol", "")


def handle(conn: socket.socket, conn_id: int):
    try:
        handshake(conn)
    except Exception as e:
        log(f"[conn#{conn_id}] handshake failed: {e}")
        conn.close()
        return
    log(f"[conn#{conn_id}] opened (hello sent)")
    conn.sendall(build_frame(1, json.dumps({"type": "hello", "version": "1"}).encode()))
    while True:
        try:
            opcode, payload = recv_client_frame(conn)
        except (ConnectionError, OSError):
            log(f"[conn#{conn_id}] closed by peer")
            return
        if opcode == 8:  # close
            log(f"[conn#{conn_id}] close frame received")
            conn.sendall(build_frame(8, payload))
            return
        if opcode == 9:  # ping → pong
            conn.sendall(build_frame(10, payload))
            continue
        if opcode == 1:  # text
            text = payload.decode("utf-8", errors="replace")
            try:
                parsed = json.loads(text)
                tag = f"seq={parsed.get('seq')} pos={parsed.get('position')} playing={parsed.get('playing')}"
                if parsed.get("type") == "state":
                    t = parsed.get("track", {})
                    tag += f" track={t.get('title')!r}/{t.get('artist')!r}/{t.get('duration')}"
                else:
                    tag = f"type={parsed.get('type')} code={parsed.get('code')}"
            except Exception:
                tag = "(non-json)"
            log(f"[conn#{conn_id}] >> {tag}  raw={text}")
        else:
            log(f"[conn#{conn_id}] >> opcode={opcode} len={len(payload)}")


def main():
    if os.path.exists(LOG_PATH):
        os.remove(LOG_PATH)
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind((HOST, PORT))
    server.listen(8)
    log(f"e2e host listening on ws://{HOST}:{PORT} (log: {LOG_PATH})")
    conn_id = 0
    while True:
        conn, addr = server.accept()
        conn_id += 1
        threading.Thread(target=handle, args=(conn, conn_id), daemon=True).start()


if __name__ == "__main__":
    main()
