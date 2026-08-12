#!/usr/bin/env node
/**
 * TASK-205 取证推帧客户端：直连桌面端 WebSocket 探针服务（ws://127.0.0.1:17865），
 * 按预置序列推 TDD §4 协议 state 帧（track/position/playing/seq 单调递增），
 * 驱动主窗口歌词区展示（当前行跟随外推进度、来源条、暂无歌词态）。
 *
 * 预置序列：
 *   --seq=a  trackA = Test Song / Test Artist / 10s（t205_qrc*.txt 样本键一致）
 *            position 0.5→12.0 共 7 帧，帧间 1s（覆盖首行前/各行/末行后停留）
 *   --seq=b  trackB = Instrumental Piece / No Artist / 8s（t205 负缓存预置键一致）
 *            position 1.0/3.0/5.0 共 3 帧
 *
 * 用法：node tools/t205_push.js [--seq=a|b] [--count=N] [--port=17865]
 */
'use strict';

const net = require('net');
const crypto = require('crypto');

const args = process.argv.slice(2);
const seq = (args.find((a) => a.startsWith('--seq=')) || '--seq=a').split('=')[1];
const countArg = args.find((a) => a.startsWith('--count='));
const count = countArg ? parseInt(countArg.split('=')[1], 10) : Infinity;
const port = parseInt((args.find((a) => a.startsWith('--port=')) || '--port=17865').split('=')[1], 10);

const TRACK_A = { title: 'Test Song', artist: 'Test Artist', duration: 10 };
const TRACK_B = { title: 'Instrumental Piece', artist: 'No Artist', duration: 8 };

const FRAMES_A = [0.5, 2.5, 4.5, 6.5, 8.5, 10.5, 12.0]; // 首行前 → 各行 → 末行后
const FRAMES_B = [1.0, 3.0, 5.0];

const track = seq === 'b' ? TRACK_B : TRACK_A;
const frames = (seq === 'b' ? FRAMES_B : FRAMES_A).slice(0, count);
const intervalMs = 1000;

// ---------------- RFC6455 帧 ----------------
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

function stateFrame(seqNo, pos, playing) {
  return JSON.stringify({
    type: 'state', seq: seqNo,
    track: { title: track.title, artist: track.artist, duration: track.duration },
    position: pos, playing,
    sentAt: Date.now(),
  });
}

// ---------------- 客户端 ----------------
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
    console.log(`[t205_push] connected (seq=${seq}, track=${track.title}, frames=${frames.join(',')})`);
    pushLoop();
  }
});

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function pushLoop() {
  let seqNo = 1;
  for (const pos of frames) {
    sock.write(buildFrame(1, Buffer.from(stateFrame(seqNo++, pos, true)), true));
    console.log(`[t205_push] -> state seq=${seqNo - 1} pos=${pos}s playing=true`);
    await sleep(intervalMs);
  }
  console.log('[t205_push] frames done, closing');
  sock.destroy();
  process.exit(0);
}

sock.on('error', (err) => {
  console.error(`[t205_push] socket error: ${err.message}`);
  process.exit(1);
});
