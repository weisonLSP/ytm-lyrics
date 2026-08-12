#!/usr/bin/env node
/**
 * TASK-103 自测：Node 模拟浏览器运行时注入探针（受限沙箱无 headless Chromium 的等价方案）
 *
 * 探针是零 GM_* 依赖的纯原生 JS，注入到带标准 API 模拟的 JS 运行时后行为与浏览器一致：
 *  - document.querySelector('audio') → FakeAudio（事件系统 / currentTime / duration / paused / src）
 *  - window.ytInitialPlayerResponse / ytplayer / console
 *  - WebSocket → Node net 实现的 RFC6455 客户端（真 TCP，连本进程内的 mock 服务端）
 *  - setInterval/setTimeout/Date 由宿主时钟驱动
 *
 * 验证：播放/暂停/seek/切歌（重建 audio 引用）/切歌（元数据降级）/切歌3（真机形态 video + mediaSession
 * 优先，干扰源 playerResponse/title 不被采用）/切歌4（多元素并存选活跃者）/2s 心跳/断线指数退避重连，
 * 并按 TDD §4 断言帧格式与 seq 单调递增。
 *
 * 用法: node tools/e2e_node.js   （输出断言结果 + tools/e2e_node_frames.log）
 */
'use strict';

const fs = require('fs');
const path = require('path');
const net = require('net');
const crypto = require('crypto');
const vm = require('vm');

const ROOT = path.dirname(path.dirname(__filename));
const LOG_PATH = path.join(ROOT, 'tools', 'e2e_node_frames.log');
const PROBE_PATH = path.join(ROOT, 'probe', 'ytm-lyrics-probe.user.js');
const PORT = 17865;

// --real：不启动内置 mock server，连接外部真桌面端（SmokeHost.exe / WebSocketProbeProvider）
const REAL = process.argv.includes('--real');

const logLines = [];
function log(line) {
  const ts = (Date.now() / 1000).toFixed(3);
  const out = `[t=${ts}s] ${line}`;
  logLines.push(out);
  console.log(out);
}

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

/** 从缓冲区解析一帧，返回 {opcode, payload, used}；不足一帧返回 null */
function tryParseFrame(buf) {
  if (buf.length < 2) return null;
  const opcode = buf[0] & 0x0f;
  let len = buf[1] & 0x7f;
  let off = 2;
  if (len === 126) {
    if (buf.length < 4) return null;
    len = buf.readUInt16BE(2); off = 4;
  } else if (len === 127) {
    if (buf.length < 10) return null;
    len = Number(buf.readBigUInt64BE(2)); off = 10;
  }
  const masked = (buf[1] & 0x80) !== 0;
  const mask = masked ? buf.subarray(off, off + 4) : null;
  if (masked) off += 4;
  if (buf.length < off + len) return null;
  let payload = buf.subarray(off, off + len);
  if (mask) payload = Buffer.from(payload.map((b, i) => b ^ mask[i % 4]));
  return { opcode, payload, used: off + len };
}

// ---------------- Mock WebSocket 服务端（模拟桌面端，TDD §4 协议） ----------------
function startServer(connTag) {
  const server = net.createServer((sock) => {
    let buf = Buffer.alloc(0);
    let handshaken = false;
    sock.on('data', (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      if (!handshaken) {
        const idx = buf.indexOf('\r\n\r\n');
        if (idx < 0) return;
        const accept = crypto.createHash('sha1')
          .update(/Sec-WebSocket-Key:\s*([^\r\n]+)/i.exec(buf.toString())[1].trim() + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')
          .digest('base64');
        sock.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
          `Sec-WebSocket-Accept: ${accept}\r\n\r\n`);
        handshaken = true;
        buf = buf.subarray(idx + 4);
        log(`[${connTag}] opened, hello sent`);
        sock.write(buildFrame(1, Buffer.from(JSON.stringify({ type: 'hello', version: '1' }))));
      }
      while (true) {
        const f = tryParseFrame(buf);
        if (!f) break;
        buf = buf.subarray(f.used);
        if (f.opcode === 1) {
          const text = f.payload.toString('utf8');
          try {
            const j = JSON.parse(text);
            const t = j.track || {};
            log(`[${connTag}] >> ${text}`);
          } catch (e) { log(`[${connTag}] >> (non-json) ${text}`); }
        } else if (f.opcode === 8) {
          log(`[${connTag}] close frame`);
          sock.end(); return;
        } else if (f.opcode === 9) {
          sock.write(buildFrame(10, f.payload));
        }
      }
    });
    sock.on('close', () => log(`[${connTag}] socket closed`));
    sock.on('error', () => {});
    server._conns.add(sock);
  });
  server._conns = new Set();
  return new Promise((resolve) => {
    server.listen(PORT, '127.0.0.1', () => resolve(server));
  });
}

function stopServer(server) {
  for (const s of server._conns) s.destroy();
  return new Promise((r) => server.close(r));
}

// ---------------- Mock WebSocket 客户端（探针里的 new WebSocket(...)） ----------------
class MockWebSocket {
  static CONNECTING = 0; static OPEN = 1; static CLOSED = 3;
  constructor(url) {
    this.readyState = MockWebSocket.CONNECTING;
    this.onopen = null; this.onmessage = null; this.onclose = null; this.onerror = null;
    this._buf = Buffer.alloc(0);
    const m = /^ws:\/\/([^:]+):(\d+)/.exec(url);
    this._sock = net.connect(Number(m[2]), m[1]);
    this._sock.on('connect', () => this._handshake());
    this._sock.on('data', (chunk) => this._onData(chunk));
    this._sock.on('close', () => { this.readyState = MockWebSocket.CLOSED; if (this.onclose) this.onclose(); });
    this._sock.on('error', () => { if (this.onerror) this.onerror(); });
  }
  _handshake() {
    const key = crypto.randomBytes(16).toString('base64');
    this._sock.write(`GET / HTTP/1.1\r\nHost: 127.0.0.1:${PORT}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`);
  }
  _onData(chunk) {
    this._buf = Buffer.concat([this._buf, chunk]);
    if (this.readyState === MockWebSocket.CONNECTING) {
      const idx = this._buf.indexOf('\r\n\r\n');
      if (idx < 0) return;
      this._buf = this._buf.subarray(idx + 4);
      this.readyState = MockWebSocket.OPEN;
      if (this.onopen) this.onopen();
    }
    while (true) {
      const f = tryParseFrame(this._buf);
      if (!f) break;
      this._buf = this._buf.subarray(f.used);
      if (f.opcode === 1 && this.onmessage) this.onmessage({ data: f.payload.toString('utf8') });
    }
  }
  send(text) { this._sock.write(buildFrame(1, Buffer.from(text), true)); }
}

// ---------------- FakeAudio：模拟 HTMLAudioElement ----------------
class FakeAudio {
  constructor(duration, src) {
    this.duration = duration;
    this.currentTime = 0;
    this.paused = true;
    this.src = src;
    this._listeners = new Map();
  }
  addEventListener(type, fn) {
    if (!this._listeners.has(type)) this._listeners.set(type, []);
    this._listeners.get(type).push(fn);
  }
  removeEventListener(type, fn) {
    const arr = this._listeners.get(type);
    if (!arr) return;
    this._listeners.set(type, arr.filter((f) => f !== fn));
  }
  dispatch(type) {
    log(`[fake-ytm] audio event: ${type} (t=${this.currentTime.toFixed(2)} paused=${this.paused})`);
    const arr = this._listeners.get(type);
    if (arr) arr.forEach((fn) => fn({ type }));
  }
  play() { this.paused = false; this.dispatch('play'); }
  pause() { this.paused = true; this.dispatch('pause'); }
  seek(t) { this.currentTime = t; this.dispatch('seeked'); }
  loadMeta() { this.dispatch('loadedmetadata'); this.dispatch('durationchange'); }
}

// ---------------- 模拟浏览器运行时上下文 ----------------
// 页面媒体元素列表（audio/video 共存）：探针按 querySelectorAll('audio, video') 全查，多元素时选活跃者
function makeEnv() {
  let metadata = null; // navigator.mediaSession.metadata（W3C 标准；mock 为普通对象）
  let title = 'Song A (Original Mix) - Channel A - YouTube Music';
  const media = [];
  let audio = null;

  function addMedia(tagName, duration, src) {
    const el = new FakeAudio(duration, src);
    el.tagName = tagName.toUpperCase();
    media.push(el);
    return el;
  }
  function queryMedia(sel) {
    const wanted = sel.split(',').map((s) => s.trim().toUpperCase());
    return media.filter((el) => wanted.includes(el.tagName));
  }

  audio = addMedia('audio', 3.0, 'wav://song-a'); // 场景一/二：audio（原 TASK-103 场景）

  const env = {
    mediaEls: media,
    // 兼容切歌1：重建 audio 元素（替换媒体列表中的 audio 槽位）
    audioRef: {
      get: () => audio,
      set: (a) => {
        const i = media.indexOf(audio);
        a.tagName = 'AUDIO';
        if (i >= 0) media[i] = a; else media.push(a);
        audio = a;
      },
    },
    setMediaSession: (m) => { metadata = m; },
  };

  const documentStub = {
    title,
    querySelector: (sel) => { const list = queryMedia(sel); return list.length > 0 ? list[0] : null; },
    querySelectorAll: queryMedia,
  };
  const windowStub = {
    console,
    ytInitialPlayerResponse: { videoDetails: { title: 'Song A (Original Mix)', author: 'Channel A', lengthSeconds: '3' } },
    ytplayer: undefined,
  };
  const navigatorStub = {
    mediaSession: {
      get metadata() { return metadata; },
      set metadata(m) { metadata = m; },
    },
  };
  return { env, documentStub, windowStub, navigatorStub };
}

// ---------------- 注入探针并执行行为序列 ----------------
async function run() {
  try { fs.unlinkSync(LOG_PATH); } catch (e) {}
  const out = fs.createWriteStream(LOG_PATH, { flags: 'a' });
  const origLog = console.log;
  console.log = (...a) => { origLog(...a); out.write(a.join(' ') + '\n'); };

  const { env, documentStub, windowStub, navigatorStub } = makeEnv();
  const audio = () => env.audioRef.get();

  let server = null;
  let server2 = null;
  if (!REAL) {
    server = await startServer('conn#1');
    log('=== e2e_node: mock server up (ws://127.0.0.1:17865) ===');
  } else {
    log('=== e2e_node: REAL mode — 连接外部桌面端 (SmokeHost.exe, 15s 窗口) ===');
  }

  const sandbox = {
    window: windowStub, document: documentStub, navigator: navigatorStub, WebSocket: MockWebSocket,
    console, setInterval, clearInterval, setTimeout, clearTimeout,
  };
  vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(PROBE_PATH, 'utf8'), sandbox);
  log('=== probe injected ===');

  const t0 = Date.now();
  const at = (ms) => sleep(ms - (Date.now() - t0));
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

  await at(REAL ? 600 : 1000);  audio().play();
  await at(REAL ? 1500 : 1800);  audio().seek(1.2);
  await at(REAL ? 2500 : 2500);  audio().pause();
  await at(REAL ? 3500 : 3200);  audio().play();
  await at(REAL ? 4500 : 4000);  // 切歌1：重建 audio 元素（引用变化 → 探针 poll 重挂并上报）
    env.audioRef.set(new FakeAudio(4.0, 'wav://song-b'));
    windowStub.ytInitialPlayerResponse = { videoDetails: { title: 'Song B - Live at Tokyo', author: 'Channel B', lengthSeconds: '4' } };
    log('[fake-ytm] 切歌1：重建 audio 元素 + 更新结构化数据');
  await at(6000);  // 切歌2：降级路径
    delete windowStub.ytInitialPlayerResponse;
    documentStub.title = 'Song C - Raw Channel C - YouTube Music';
    audio().duration = 5.0; audio().src = 'wav://song-c'; audio().loadMeta();
    log('[fake-ytm] 切歌2：结构化数据删除 → 降级 document.title');

  await at(8000);  // 切歌3：真机形态 —— 仅 video + mediaSession（真实 YTM 主播放器，页面无 <audio>）
    env.mediaEls.length = 0;
    const videoD = new FakeAudio(6.0, 'wav://song-d');
    videoD.tagName = 'VIDEO';
    env.mediaEls.push(videoD);
    env.setMediaSession({ title: 'Song D (Video Session)', artist: 'Channel D', album: 'Album D' });
    // 干扰源：playerResponse 与 document.title 均指向别处 → 必须走 mediaSession（不降级）
    windowStub.ytInitialPlayerResponse = { videoDetails: { title: 'Song D - WRONG CHANNEL', author: 'WRONG', lengthSeconds: '999' } };
    documentStub.title = 'Song D - Raw Title - YouTube Music';
    videoD.play();
    log('[fake-ytm] 切歌3：真机形态 video + mediaSession（干扰：playerResponse/title 指向别处）');

  await at(10000); // 切歌4：多元素并存 → 活跃者优先
    env.mediaEls.length = 0;
    const idleE = new FakeAudio(3.0, 'wav://song-e'); idleE.tagName = 'AUDIO'; // 静止：paused=true、currentTime=0
    const activeF = new FakeAudio(7.0, 'wav://song-f'); activeF.tagName = 'VIDEO';
    activeF.paused = false; activeF.currentTime = 2.0;
    env.mediaEls.push(idleE, activeF); // DOM 序：静止 audio 在前 → 活跃优先逻辑必须越过它
    env.setMediaSession({ title: 'Song F (Active Video)', artist: 'Channel F', album: '' });
    windowStub.ytInitialPlayerResponse = { videoDetails: { title: 'Song E - Idle Audio', author: 'Channel E', lengthSeconds: '3' } };
    documentStub.title = 'Song F - Raw Title - YouTube Music';
    log('[fake-ytm] 切歌4：静止 audio + 播放中 video 并存 → 探针必须选活跃 video');

  if (REAL) {
    await at(13000);
    log('=== e2e_node: REAL sequence done (桌面端日志另行采集) ===');
    out.end();
    process.exit(0);
  }

  await at(14000);
  log('=== 断线：stopping mock server ===');
  await stopServer(server);

  await at(19000);
  log('=== 重启 mock server（探针退避重连应成功） ===');
  server2 = await startServer('conn#2');

  await at(26500);
  log('=== e2e_node: sequence done, asserting... ===');
  // ---------------- 断言 ----------------
  const frames = logLines.filter((l) => l.includes('"type":"state"')).map((l) => {
    const m = />> (\{.*\})$/.exec(l);
    return JSON.parse(m[1]);
  });
  const assert = (cond, msg) => {
    log((cond ? '[PASS] ' : '[FAIL] ') + msg);
    if (!cond) process.exitCode = 1;
  };

  const fields = ['type', 'seq', 'track', 'position', 'playing', 'sentAt'];
  assert(frames.length > 0, `收到帧数 > 0 (实际 ${frames.length})`);
  assert(frames.every((f) => fields.every((k) => k in f)), '所有帧字段齐全 (type/seq/track/position/playing/sentAt)');
  assert(frames.every((f) => ['title', 'artist', 'duration'].every((k) => k in f.track)), 'track 字段齐全 (title/artist/duration)');
  assert(frames.every((f, i) => i === 0 || f.seq > frames[i - 1].seq), 'seq 严格单调递增');
  assert(frames.some((f) => f.playing === true && Math.abs(f.position - 0) < 0.01 && f.track.title.includes('Song A')), 'play 事件帧（曲A 播放）');
  assert(frames.some((f) => f.playing === true && Math.abs(f.position - 1.2) < 0.01), 'seek 事件帧 (position=1.2)');
  assert(frames.some((f) => f.playing === false && f.track.title.includes('Song A')), 'pause 事件帧');
  const b = frames.find((f) => f.track.title === 'Song B - Live at Tokyo');
  assert(!!b && b.track.artist === 'Channel B' && Math.round(b.track.duration) === 4, '切歌1：结构化元数据原样上报 (title/artist/duration)');
  const c = frames.find((f) => f.track.title === 'Song C - Raw Channel C - YouTube Music');
  assert(!!c && c.track.artist === '' && Math.round(c.track.duration) === 5, '切歌2：降级路径 document.title 原样上报 (artist 空)');
  // 场景三：真机形态 —— 仅 video + mediaSession（mediaSession 优先，不用 playerResponse/title 降级；duration 取媒体元素）
  const d = frames.find((f) => f.track.title === 'Song D (Video Session)');
  assert(!!d && d.track.artist === 'Channel D' && Math.round(d.track.duration) === 6 && d.playing === true,
    '场景三：video + mediaSession 命中（不用 title 降级；duration=媒体元素 6s 而非干扰源 999s）');
  assert(!frames.some((f) => f.track.title === 'Song D - WRONG CHANNEL' || f.track.title === 'Song D - Raw Title - YouTube Music'),
    '场景三：干扰源（playerResponse/document.title）未被采用');
  // 场景四：多元素并存 → 活跃者优先（静止 audio 在前被跳过，报播放中 video 的 pos=2.0）
  const f4 = frames.find((f) => f.track.title === 'Song F (Active Video)');
  assert(!!f4 && f4.playing === true && Math.abs(f4.position - 2.0) < 0.01 && Math.round(f4.track.duration) === 7,
    '场景四：多元素选活跃者（静止 audio 被跳过，报播放中 video pos=2.0）');
  // 心跳：稳态段（间隔 >1.5s 的帧对）应全部 ≈ 2s 且至少 3 个（事件帧间隔 <1.5s 被排除）
  const conn1 = logLines.filter((l) => l.includes('[conn#1] >> ') && l.includes('"type":"state"'));
  const tOf = (l) => parseFloat(/^\[t=([\d.]+)s\]/.exec(l)[1]);
  const allGaps = [];
  for (let i = 1; i < conn1.length; i++) allGaps.push(tOf(conn1[i]) - tOf(conn1[i - 1]));
  const steady = allGaps.filter((g) => g > 1.5);
  assert(steady.length >= 3 && steady.every((g) => Math.abs(g - 2.0) < 0.4),
    `2s 心跳稳定 (稳态间隔: ${steady.map((g) => g.toFixed(2)).join(',')})`);
  // 断线重连：conn#2 存在、有帧、seq 继续递增（页面会话级）
  const conn2 = logLines.filter((l) => l.includes('[conn#2] >> ') && l.includes('"type":"state"'));
  assert(conn2.length > 0, `断线后重连成功并恢复上报 (conn#2 帧数 ${conn2.length})`);
  const maxSeq1 = Math.max(...frames.map((f) => f.seq));
  // 重连后 seq 不重置（页面会话级单调递增）
  const lastSeq = frames[frames.length - 1].seq;
  assert(conn2.length > 0 && lastSeq > 0, '重连后 seq 保持页面会话级递增');

  log(`=== summary: 共 ${frames.length} 帧, maxSeq=${maxSeq1}, conn#2 帧数=${conn2.length} ===`);
  out.end();
  process.exit();
}

run().catch((e) => { console.error('e2e_node failed:', e); process.exit(1); });
