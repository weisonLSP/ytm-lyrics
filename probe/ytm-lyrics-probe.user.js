// ==UserScript==
// @name         YTM Lyrics Probe
// @namespace    ytm-lyrics
// @version      0.1.1
// @description  YTM 播放状态采集探针（PRD F1）：读 audio/video 媒体元素，报状态，别的什么都不做。
// @match        https://music.youtube.com/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

/*!
 * ytm-lyrics-probe — F1 探针
 *
 * 设计约束（PRD F1 / TDD §4）：
 *  - 唯一信任锚点：HTMLMediaElement（audio/video，HTML5 标准事件与属性，不依赖任何 YTM CSS 类名 / DOM 层级）
 *  - 零 GM_* API，纯原生 JS：可被 headless 浏览器直接注入测试，行为与油猴一致
 *  - 元数据绝不清洗：mediaSession → YTM 结构化数据（playerResponse）→ document.title 原始值，
 *    清洗是桌面端 F3 的职责
 *  - 帧格式严格按 TDD §4：type/seq/track{title,artist,duration}/position/playing/sentAt
 *  - seq 页面会话级单调递增；重连后不重置（桌面端按连接级语义独立维护 maxSeq）
 */
(function () {
  'use strict';

  var WS_URL = 'ws://127.0.0.1:17865';
  var HEARTBEAT_MS = 2000;  // 稳态心跳周期
  var POLL_MS = 2000;       // 媒体元素轮询兜底（YTM 是 SPA，媒体元素生命周期不固定）
  var RETRY_BASE_MS = 1000; // 断线重连指数退避：1s 起
  var RETRY_MAX_MS = 30000; // 退避上限 30s

  var ws = null;
  var seq = 0;              // 页面会话级单调递增
  var ready = false;        // 收到 hello 后才开始上报
  var retryDelay = RETRY_BASE_MS;
  var wsRetryTimer = null;
  var boundAudio = null;    // 当前挂载事件的 audio 引用
  var lastTrackKey = null;  // 切歌检测基准（title|artist|duration 元组）


  // ---------- 元数据链路（裁决重排）：mediaSession → ytInitialPlayerResponse → document.title（探针绝不清洗） ----------

  // 首选：W3C 标准 MediaSession API。真实 YTM 主播放器恒在维护（audio/video 均由播放器驱动），
  // title/artist 直接可用；不提供时长 → duration 仍以媒体元素为准。
  function readMediaSession() {
    try {
      var ms = navigator.mediaSession;
      if (ms && ms.metadata) return ms.metadata;
    } catch (e) { /* 环境不支持：静默降级 */ }
    return null;
  }

  // 次选：YTM 结构化数据槽位：ytInitialPlayerResponse（对象）与 ytplayer.config.args.player_response（字符串 JSON）。
  // 注：ytInitialData 是页面导航数据，不含当前播放曲目，不适用。
  function readVideoDetails() {
    try {
      var pr = window.ytInitialPlayerResponse;
      if (pr && pr.videoDetails) return pr.videoDetails;
      var args = window.ytplayer && window.ytplayer.config && window.ytplayer.config.args;
      if (args && args.player_response) {
        var parsed = typeof args.player_response === 'string'
          ? JSON.parse(args.player_response)
          : args.player_response;
        if (parsed && parsed.videoDetails) return parsed.videoDetails;
      }
    } catch (e) { /* 解析失败静默降级 */ }
    return null;
  }

  // CJK 字符检测（日文假名/汉字/中文/韩文）：识别「罗马音 vs 原文」（真机反馈⑨）。
  // YTM mediaSession.title 对日语歌可能给罗马音（纯拉丁），而 videoDetails.title / document.title 才含日文原文。
  var CJK_RE = /[\u3000-\u303f\u3040-\u30ff\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\uac00-\ud7af]/;
  function hasCjk(s) {
    return CJK_RE.test(s);
  }

  function readMetadata(audio) {
    var ms = readMediaSession();
    var vd = readVideoDetails();

    var msTitle = ms && ms.title ? String(ms.title) : '';
    var msArtist = ms && ms.artist ? String(ms.artist) : '';
    var vdTitle = vd && vd.title ? String(vd.title) : '';
    var docTitle = document.title || '';

    // 真机反馈⑨：mediaSession.title 纯拉丁（无 CJK）且另有 CJK 原文时，降级用含原文的源。
    // artist 仍取 mediaSession（videoDetails.author 是频道名非艺人，document.title 无艺人，都不能用）。
    if (msTitle && !hasCjk(msTitle)) {
      if (vdTitle && hasCjk(vdTitle)) {
        return {
          title: vdTitle,
          artist: msArtist,
          duration: audio && isFinite(audio.duration) ? audio.duration : 0
        };
      }
      if (docTitle && hasCjk(docTitle)) {
        return {
          title: docTitle,
          artist: msArtist,
          duration: audio && isFinite(audio.duration) ? audio.duration : 0
        };
      }
    }

    // 首选 mediaSession：W3C 标准，真实 YTM 主播放器恒在（title/artist 直接用，不清洗）
    if (msTitle) {
      return {
        title: msTitle,
        artist: msArtist,
        duration: audio && isFinite(audio.duration) ? audio.duration : 0 // mediaSession 不提供时长
      };
    }
    if (vd && vd.title) {
      var dur = parseFloat(vd.lengthSeconds);
      return {
        title: String(vd.title),
        artist: vd.author ? String(vd.author) : '', // 频道名原始值，不清洗
        duration: isFinite(dur) ? dur : (audio && isFinite(audio.duration) ? audio.duration : 0)
      };
    }
    // 降级路径：document.title 原始值；频道名无可用源时留空，清洗交给桌面端 F3
    return {
      title: docTitle,
      artist: '',
      duration: audio && isFinite(audio.duration) ? audio.duration : 0
    };
  }

  // ---------- 上报 ----------

  function sendState(reason) {
    if (!ws || ws.readyState !== WebSocket.OPEN || !ready) return;
    var audio = boundAudio;
    var md = readMetadata(audio);
    var frame = {
      type: 'state',
      seq: ++seq,
      track: {
        title: md.title,
        artist: md.artist,
        duration: md.duration
      },
      position: audio ? audio.currentTime : 0,
      playing: audio ? !audio.paused : false,
      sentAt: Date.now()
    };
    try { ws.send(JSON.stringify(frame)); } catch (e) { /* 发送失败静默，等待下一周期 */ }
  }

  // ---------- 切歌检测：title|artist|duration 元组变化 ----------

  function trackKey(audio) {
    var md = readMetadata(audio);
    return md.title + '|' + md.artist + '|' + Math.round(md.duration);
  }

  function checkTrackChange(forceSend) {
    var audio = boundAudio;
    if (!audio) return;
    var key = trackKey(audio);
    if (key !== lastTrackKey) {
      lastTrackKey = key;
      sendState('track');        // 切歌：事件即发
    } else if (forceSend) {
      sendState('init');         // 首次发现 audio / hello 后：立即上报当前状态
    }
  }

  // ---------- audio 生命周期（HTML5 标准事件，唯一信任锚点） ----------

  var AUDIO_EVENTS = ['play', 'pause', 'seeked', 'ended', 'loadedmetadata', 'durationchange'];

  function onAudioEvent(e) {
    switch (e.type) {
      case 'play':  sendState('play'); break;   // 恢复/开始播放：即发
      case 'pause': sendState('pause'); break;  // 暂停：即发
      case 'seeked': sendState('seek'); break;  // 拖动进度：即发
      case 'ended': sendState('ended'); break;  // 播完：即发（YTM 随后切歌走 loadedmetadata 路径）
      case 'loadedmetadata':
      case 'durationchange':
        checkTrackChange(false);                // 元数据/时长变化：可能切歌
        break;
      default: break;
    }
  }

  function bindAudio(el) {
    for (var i = 0; i < AUDIO_EVENTS.length; i++) el.addEventListener(AUDIO_EVENTS[i], onAudioEvent);
  }

  function unbindAudio(el) {
    for (var i = 0; i < AUDIO_EVENTS.length; i++) el.removeEventListener(AUDIO_EVENTS[i], onAudioEvent);
  }

  // 查找媒体元素：audio, video 全查（真实 YTM 主播放器为 <video>，纯音频亦然，页面无 <audio>）；
  // 多元素并存时优先选活跃者（未暂停 || currentTime>0），全静止退回第一个。
  // 仅用 HTML5 标准元素标签选择器（PRD F1 红线：不依赖任何 YTM CSS 类名 / DOM 层级）
  function findMedia() {
    var els = document.querySelectorAll('audio, video');
    if (!els || els.length === 0) return null;
    if (els.length === 1) return els[0];
    for (var i = 0; i < els.length; i++) {
      var el = els[i];
      if (!el.paused || el.currentTime > 0) return el;
    }
    return els[0];
  }

  // SPA 兜底轮询：2s 一次查找媒体元素；引用变化时解绑旧引用、重挂事件并立即上报
  function poll() {
    var el = findMedia();
    if (el !== boundAudio) {
      if (boundAudio) unbindAudio(boundAudio);
      boundAudio = el;
      if (el) {
        bindAudio(el);
        lastTrackKey = null;      // 强制首次上报（新元素即新状态）
        checkTrackChange(true);
      }
    } else {
      checkTrackChange(false);
    }
    if (ready) sendState('heartbeat'); // 稳态 2s 心跳（暂停时也发，桌面端据此区分离线/暂停）
  }

  // ---------- WebSocket 连接与重连 ----------

  function scheduleReconnect() {
    ready = false;
    clearTimeout(wsRetryTimer);
    wsRetryTimer = setTimeout(connect, retryDelay);
    retryDelay = Math.min(retryDelay * 2, RETRY_MAX_MS);
  }

  function connect() {
    try { ws = new WebSocket(WS_URL); } catch (e) { scheduleReconnect(); return; }
    ws.onopen = function () {
      retryDelay = RETRY_BASE_MS; // 连接成功：退避归位
    };
    ws.onmessage = function (evt) {
      var msg;
      try { msg = JSON.parse(evt.data); } catch (e) { return; }
      if (!msg) return;
      if (msg.type === 'hello') {
        ready = true;             // 等 hello 后才开始上报
        checkTrackChange(true);   // 立即给桌面端当前状态
      } else if (msg.type === 'error') {
        if (window.console) console.debug('[ytm-lyrics-probe] server error:', msg.code, msg.message);
      }
    };
    ws.onclose = function () {
      scheduleReconnect();        // 断线：指数退避重连（1s 起，×2，上限 30s）
    };
    ws.onerror = function () { /* onclose 随后触发，重连调度统一走 onclose */ };
  }

  // ---------- 启动 ----------
  connect();
  poll(); // 立即初始化：挂 audio 监听 + 上报当前状态（不等首个 2s tick）
  setInterval(poll, POLL_MS);
})();
