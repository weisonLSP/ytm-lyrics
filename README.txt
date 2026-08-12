# YTM.Lyrics 便携包

YouTube Music 桌面歌词应用。M3 完成态。

## 快速启动

1. 双击 `setup.bat`（检查 .NET 8 SDK + 还原包 + 构建）
2. 双击 `dist\YTM.Lyrics.exe`
3. 浏览器打开 YouTube Music 播放歌曲
4. 按 `Alt + Shift + W` 显示桌面歌词窗口

## 环境要求

- Windows 10/11 x64
- .NET 8 SDK + Desktop Runtime（setup.bat 会提示下载链接）

## 文件结构

```
ytm-lyrics-portable/
├── setup.bat          ← 一键环境准备
├── YTM.Lyrics.sln     ← 解决方案
├── src/               ← 源码（Desktop + Core + Tests）
├── external/          ← Lyricify.Lyrics.Helper 源码引用
├── probe/             ← 油猴探针脚本
├── docs/              ← 设计文档
├── tools/             ← 取证/测试工具
└── dist/              ← 构建产物（setup.bat 跑完后生成）
```

## 桌面歌词窗口

- 横条形态，透明背景，默认置顶
- 可拖动（鼠标拖）、可缩放（四边四角）
- 右键菜单：穿透 / 锁定 / 置顶开关
- 设置文件：`%APPDATA%\YTM.Lyrics\settings.json`

## 文档索引

- `docs/HANDOFF.md` — 会话交接入口
- `docs/PRD.md` — 产品需求
- `docs/TDD.md` — 技术设计 + ADR
- `docs/UI_SPEC.md` — UI 规格（滚动参数已校准）
- `docs/DEVELOPMENT_PLAN.md` — 任务卡进度
