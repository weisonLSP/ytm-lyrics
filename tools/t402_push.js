#!/usr/bin/env node
/**
 * TASK-402 取证推帧客户端：直连桌面端 WebSocket（ws://127.0.0.1:<port>），
 * 推 TDD §4 协议 state 帧（同一曲目，位置推进），每帧间隔 2s。
 * 用法：node tools/t402_push.js --port=17901 --title="X" --artist="Y" --duration=10
 * 用途：探针 A 连旧端口推曲目；ws_port 保存变更后探针 B 连新端口推新曲目，
 *       验证 RestartWebSocketListener 重连 + RebuildEngine 后新引擎按新优先级搜索。
 */
'use strict';

const net = require('net');
const crypto = require('crypto');

const args = process.argv.slice(2);
function arg(name, dflt) {
  const hit = args.find((a) => a.startsWith(`--${name}=`));
  return hit ? hit.split('=').slice(1).join('=') : dflt;
}
const port = parseInt(arg('port', '17901'), 10);
const title = arg('title', 'Test Song');
const artist = arg('artist', 'Test Artist');
const duration = parseFloat(arg('duration', '10'));

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

function stateFrame(seq, pos) {
  return JSON.stringify({
    type: 'state', seq,
    track: { title, artist, duration },
    position: pos, playing: true, sentAt: Date.now(),
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
    console.log(`[t402_push] connected (port=${port}) track='${title}'`);
    run();
  }
});

function sleep(ms) { return new Promise((r) => setTimeout(r, ms)); }

async function run() {
  let seq = 1;
  for (const pos of [0.5, 2.5, 4.5]) {
    sock.write(buildFrame(1, Buffer.from(stateFrame(seq++, pos)), true));
    console.log(`[t402_push] -> state seq=${seq - 1} pos=${pos}s`);
    await sleep(2000);
  }
  console.log('[t402_push] frames done, closing');
  sock.destroy();
  process.exit(0);
}

sock.on('error', (err) => {
  console.error(`[t402_push] socket error: ${err.message}`);
  process.exit(1);
});
