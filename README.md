# HeadTracker

[![CI](https://github.com/Suzushi/HEADTracker/actions/workflows/ci.yml/badge.svg)](https://github.com/Suzushi/HEADTracker/actions/workflows/ci.yml)

A webcam-based head tracker for flight/space simulation games (DCS World, and anything
that speaks TrackIR / freetrack / UDP). Point a normal webcam at your face and your real
head movements drive the in-game camera.

This repository contains a **ground-up C# / .NET 8 + WPF rewrite** (in [`HeadTracker.NET/`](HeadTracker.NET))
of the original C++/Qt **FOXTracker** by [@xuhao1](https://github.com/xuhao1/FOXTracker).
It talks to games **directly** — no opentrack or other middleware required — while staying
byte-compatible with the legacy freetrack shared-memory and UDP protocols, and reading the
same `config.yaml`.

---

## Features

- **Direct game output** — FreeTrack shared memory, npclient (TrackIR), and/or UDP pose data.
  In DCS World just enable FreeTrack shared memory and set *Head Tracking* to *TrackIR*.
- **Vision pipeline** — SCRFD face detection → CSRT ROI tracking (with periodic re-detect and
  self-healing on drift) → OpenSeeFace landmark heatmap → `SolvePnPRansac` head pose with a
  reprojection-error gate.
- **Fusion & smoothing** — optional FSA-Net second measurement fused through a 13-DOF EKF,
  plus a ported Accela filter on the output stage.
- **Camera calibration wizard** — print a ChArUco board, capture a few angles, and the app
  solves your camera's real intrinsics/distortion (K/D) into `config.yaml`.
- **Response curve editor** — a visual, per-axis input→output curve (monotone cubic) that
  replaces the single legacy *expo* number. Drag control points, add/remove, or reset from expo.
- **Quality of life** — live preview, system tray, global re-center hotkey (default **Ctrl+X**,
  works while the game has focus), single-instance guard, camera restart button, and full
  **English / 中文** UI.

## Requirements

- **Windows 10/11 x64** (the app is WPF + Win32; Windows only).
- A normal **webcam** (a phone-as-webcam app such as Iriun also works).
- Nothing else — models and runtime are bundled in the release build.

## Download & run

Grab the latest self-contained build (`HeadTracker-win-x64`) from the
[CI artifacts](https://github.com/Suzushi/HEADTracker/actions/workflows/ci.yml) or
[Releases](https://github.com/Suzushi/HEADTracker/releases) once published, unzip, and run
`HeadTracker.exe`. The self-contained publish includes the .NET runtime, so no install is needed.

There is **no UAC prompt**: the app runs unelevated. The one exception to be aware of — if your sim
itself runs as administrator, start HeadTracker as administrator too, because Windows (UIPI) blocks
global hotkeys sent to a process whose integrity level is below the foreground window's.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd HeadTracker.NET
dotnet build HeadTracker.sln -c Release
dotnet test  HeadTracker.sln -c Release          # 74 unit tests
# self-contained, single-folder release (assets are copied automatically):
dotnet publish src/HeadTracker.App/HeadTracker.App.csproj -c Release -r win-x64 --self-contained true -o publish
```

## Connecting to DCS World

1. In HeadTracker → **Settings → Output**, enable **FreeTrack shared memory**.
2. In DCS World → **Settings → Special → Head Tracking**, choose **TrackIR**.
3. Start tracking in HeadTracker; press the re-center hotkey to set your neutral pose.

No other software (e.g. opentrack) needs to run.

## Usage & hotkeys

| Action | Key |
|---|---|
| Start / stop tracking | **F9** |
| Re-center (neutral pose) | **C** (window focused) |
| Re-center (global, works in-game) | **Ctrl+X** (configurable in Settings → Fusion & Hotkeys) |

The global hotkey is registered on a **dedicated message-loop thread**, and that is not a detail.
`WM_HOTKEY` is a *queued* message: bound to the WPF UI thread, it runs only once that thread gets
scheduled — and the two tracking threads run at `AboveNormal` priority with one of them CPU-bound,
so a sim that already saturates the machine can leave the key press sitting in the queue until you
alt-tab out and the CPU frees up. The hotkey thread performs the re-center itself (the remapper's
neutral pose is behind a lock) and only hands the status text back to the UI.

Elevation is a second, independent condition: Windows (UIPI) drops `WM_HOTKEY` — and low-level
keyboard hook callbacks — sent to a medium-integrity process while an elevated window has the
foreground. The app ships unelevated so nobody pays a UAC prompt for a case that usually does not
apply; run it as administrator if your sim is elevated.

Both are diagnosable rather than silent: `crash.log` records `parsed=`, `registered=`, `error=` and
`elevated=` for every binding, and on every press a `hotkey fired` line plus a UI-thread latency
probe that shows how long the UI thread took to pick up work at that moment.

Closing the main window keeps tracking running in the system tray; exit from the tray menu.

## Configuration

All settings live in `config.yaml` next to the executable and are editable from the in-app
**Settings** window (Camera / Output / Fusion & Hotkeys / Mapping / Smoothing). Key names are
compatible with the legacy FOXTracker config, so an existing `config.yaml` loads as-is. Unknown
keys are ignored and missing keys fall back to defaults; a malformed file is quarantined
(`config.bad.yaml`) instead of bricking the app.

> 👉 **See the [Parameter Tuning Guide](docs/tuning_guide.md)** for what every setting does,
> recommended starting values, and which knobs to turn for common goals (smoother tracking,
> higher FPS, better mapping, fixing jitter).

## Project layout

```
HeadTracker.NET/
  HeadTracker.sln
  src/HeadTracker.Core/     # pure classlib: capture, detection, landmarks, PnP, fusion, protocols, config
  src/HeadTracker.App/      # WPF app (MVVM): preview, settings, tray, hotkeys, calibration & curve editors
  src/HeadTracker.Bench/    # console benchmark (--camera / --video) for FPS & pose logging
  tests/HeadTracker.Core.Tests/
  assets/                   # ONNX models (SCRFD, OpenSeeFace, FSA-Net) + landmark tables
config.yaml                 # runtime configuration (repo root)
```

## Legacy C++ implementation

The original C++/Qt FOXTracker implementation is **not part of this repository** — it has been fully
superseded by the C#/.NET rewrite in `HeadTracker.NET/`, which is the only active code path here. To
browse the legacy C++ sources, see the upstream [@xuhao1/FOXTracker](https://github.com/xuhao1/FOXTracker).

## CI

GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs on every push/PR to
`main` on a **windows-latest** runner: restore → build → test → self-contained publish, uploading
the test results and the `HeadTracker-win-x64` artifact.

## License

LGPL (see [LICENSE](LICENSE)), matching the original project.

> **Model licensing note:** the bundled SCRFD face detector is released for **non-commercial /
> research** use by its authors. Review the individual model licenses before any commercial use.

## Third-party

- [OpenCV](https://opencv.org/) via [OpenCvSharp4](https://github.com/shimat/opencvsharp)
- [ONNX Runtime](https://github.com/microsoft/onnxruntime)
- [YamlDotNet](https://github.com/aaubry/YamlDotNet)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon)
- Models/architectures: [SCRFD (insightface)](https://github.com/deepinsight/insightface),
  [OpenSeeFace](https://github.com/emilianavt/OpenSeeFace), [FSA-Net](https://github.com/shamangary/FSA-Net)

## Credits

Original C++/Qt project and concept by [@xuhao1 (FOXTracker)](https://github.com/xuhao1/FOXTracker).
Background videos: [demo](https://www.bilibili.com/video/BV1fv411k778) ·
[中文解说](https://www.bilibili.com/video/BV1ey4y1C7Za).

---

## 中文说明

HeadTracker 是一个只需**普通摄像头**的面部头瞄，用于 DCS World 等飞行/太空模拟游戏，功能类似
TrackIR 或 opentrack（如 pointtracker），但**可直接对接游戏，无需 opentrack 等中间件**。本仓库是原
C++/Qt 版 FOXTracker（作者 [@xuhao1](https://github.com/xuhao1/FOXTracker)）的 **C# / .NET 8 + WPF
完全重写**，协议与 `config.yaml` 与旧版保持兼容。

- **环境**：Windows 10/11 x64 + 一个普通摄像头（手机当摄像头如 Iriun 亦可）。
- **构建**：安装 .NET 8 SDK 后 `cd HeadTracker.NET; dotnet build HeadTracker.sln -c Release`。
- **接入 DCS**：设置 → 输出 里勾选 *FreeTrack 共享内存*；DCS 里 *头部跟踪* 选 *TrackIR*。
- **热键**：F9 启停，C 回中（窗口内），**Ctrl+X 全局回中（游戏内有效，可在设置里改）**。
- **功能**：相机标定向导（ChArUco 标定板求 K/D）、可视化响应曲线编辑器（替代单一 expo）、
  FSA-Net + EKF 融合、托盘常驻、单实例、摄像头重启按钮、中英文界面。

更多细节见应用内 **设置** 窗口（Camera / Output / Fusion & Hotkeys / Mapping / Smoothing，含中英文界面与工具提示）。

> 👉 每个参数的完整含义、推荐起步值与常见调法（更平滑、更高帧率、映射、消除抖动）见
> **[参数调整说明书](docs/tuning_guide.md)**（中英双语）。
