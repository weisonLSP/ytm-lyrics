#!/usr/bin/env node
/**
 * TASK-FIX-SCROLL 滚动取证推帧客户端（自用，不入产品代码）。
 * 轨道 Test Song / 40s / 20 行歌词（t205_qrc_long.txt，行时间 0/2/4/.../38s）。
 * 帧序列：pos 0.5→40.5 共 21 帧（帧间 1s），覆盖首行前 → 各行 → 末行后停留，
 * 与 t205_scroll.ps1 的 shot@3000/13000/23000/39000 对齐（首帧 ~2.1s 到达）。
 *
 * 用法：node tools/t205_scroll_push.js --port=17899
 */
'use strict';

const net = require('net');
const crypto = require('crypto');

const port = parseInt((process.argv.find((a) => a.startsWith('--port=')) || '--port=17899').split('=')[1], 10);

const TRACK = { title: 'Test Song', artist: 'Test Artist', duration: 40 };
const FRAMES = [];
for (let i = 0; i <= 20; i++) FRAMES.push(0.5 + i * 2); // 0.5, 2.5, ..., 40.5
const intervalMs = 1000;

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
    track: { title: TRACK.title, artist: TRACK.artist, duration: TRACK.duration },
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
    console.log(`[scroll_push] connected (frames=${FRAMES.length})`);
    pushLoop();
  }
});

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function pushLoop() {
  let seqNo = 1;
  for (const pos of FRAMES) {
    sock.write(buildFrame(1, Buffer.from(stateFrame(seqNo++, pos, true)), true));
    console.log(`[scroll_push] -> state seq=${seqNo - 1} pos=${pos}s playing=true`);
    await sleep(intervalMs);
  }
  console.log('[scroll_push] frames done, closing');
  sock.destroy();
  process.exit(0);
}

sock.on('error', (err) => {
  console.error(`[scroll_push] socket error: ${err.message}`);
  process.exit(1);
});
