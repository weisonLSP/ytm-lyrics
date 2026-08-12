#!/usr/bin/env node
/**
 * TASK-401 取证推帧客户端：直连桌面端 WebSocket（ws://127.0.0.1:<port>），
 * 推 TDD §4 协议 state 帧驱动 F9 自测时间线：
 *   t+0s    track A（Test Song/Test Artist/10s）pos0.5  → 负缓存 None → 搜索面板出现
 *   t+6s    track B（Instrumental Piece/No Artist/8s）  → 切歌（TrackChanged）
 *   t+8s    track A 重播 pos0.5                          → 绑定缓存命中（userBound=True，不再自动搜索）
 *   t+11s   track A pos2.5（重播后位置推进）
 * 时间线与 MainWindow --f9-auto（搜索@2.5s / 绑定@5s / 解除@13s）对齐：
 * 绑定发生在 t≈5s（在 B 之前），重播命中发生在 t≈8s（截图@12s 拍到绑定态）。
 * 用法：node tools/t401_push.js [--port=17901]
 */
'use strict';

const net = require('net');
const crypto = require('crypto');

const args = process.argv.slice(2);
const port = parseInt((args.find((a) => a.startsWith('--port=')) || '--port=17901').split('=')[1], 10);

const A = { title: 'Test Song', artist: 'Test Artist', duration: 10 };
const B = { title: 'Instrumental Piece', artist: 'No Artist', duration: 8 };

function buildFrame(opcode, payload, mask) {
  let header;
  const len = payload.length;
  if (len < 126) header = Buffer.from([0x80 | opcode, (mask ? 0x80 : 0) | len]);
  else if (len < 65536) header = Buffer.from([0x80 | opcode, (mask ? 0x80 : 0) | 126, len >> 8, len & 0xff]);
  else throw new Error('frame too long');
  if (!mask) return Buffer.concat([header, payload]);
  const m = crypto.randomBytes(4);
  const masked = Buffer.from(payload.map((b, i) => b ^ m[i % 4]));
  return Buffer.concat([header, m, masked]);
}

function stateFrame(seqNo, track, pos, playing) {
  return JSON.stringify({
    type: 'state', seq: seqNo,
    track: { title: track.title, artist: track.artist, duration: track.duration },
    position: pos, playing,
    sentAt: Date.now(),
  });
}

const sock = net.connect(port, '127.0.0.1');
let buf = Buffer.alloc(0);
let open = false;

sock.on('connect', () => {
  const key = crypto.randomBytes(16).toString('base64');
  sock.write(
    `GET / HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
    `Sec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
});

sock.on('data', (chunk) => {
  buf = Buffer.concat([buf, chunk]);
  if (!open) {
    const idx = buf.indexOf('\r\n\r\n');
    if (idx < 0) return;
    buf = buf.subarray(idx + 4);
    open = true;
    console.log(`[t401_push] connected (port=${port})`);
    run();
  }
});

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function run() {
  let seq = 1;
  const push = (track, pos, playing) => {
    sock.write(buildFrame(1, Buffer.from(stateFrame(seq++, track, pos, playing)), true));
    console.log(`[t401_push] -> state seq=${seq - 1} track=${track.title} pos=${pos}s playing=${playing}`);
  };

  push(A, 0.5, true);          // t+0：track A 首帧 → None（负缓存）→ 搜索面板出现
  await sleep(6000);           // t+6：给 --f9-auto 搜索@2.5s + 绑定@5s 留时间
  push(B, 0.5, true);          // t+6：切歌 → TrackChanged
  await sleep(2000);
  push(A, 0.5, true);          // t+8：track A 重播 → 绑定缓存命中（userBound=True）
  await sleep(3000);
  push(A, 2.5, true);          // t+11：重播后位置推进
  await sleep(1500);
  console.log('[t401_push] frames done, closing');
  sock.destroy();
  process.exit(0);
}

sock.on('error', (err) => {
  console.error(`[t401_push] socket error: ${err.message}`);
  process.exit(1);
});
