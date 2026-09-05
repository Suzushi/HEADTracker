# Parameter Tuning Guide / 参数调整说明书

A practical guide to every HeadTracker setting: what it does, sane values, and which knobs to
turn for common goals (smoother tracking, higher FPS, better mapping, fixing jitter).

本说明书逐项讲解 HeadTracker 的每个参数：含义、推荐取值，以及针对常见目标（更平滑、更高帧率、
更合适的映射、消除抖动）应该调哪些旋钮。

- **Where settings live / 参数存放位置**: all parameters are stored in `config.yaml` next to the
  executable, and most are editable from the in-app **Settings** window. Key names are compatible
  with the legacy FOXTracker config. 所有参数保存在可执行文件旁的 `config.yaml`，大部分可在应用内
  **设置**窗口修改；键名与旧版 FOXTracker 兼容。
- **How to apply / 如何生效**: editing in the Settings window and clicking **Save & Apply** restarts
  the tracking pipeline immediately. Editing `config.yaml` by hand requires restarting the app
  (or toggling tracking off/on for a few keys). 在设置窗口点**保存并应用**会立即重启管线生效；手改
  `config.yaml` 需重启程序（少数键可停/启跟踪生效）。
- **"Default" column / “默认”列**: the value used when a key is **absent** from `config.yaml`. The
  `config.yaml` that ships with a release may set different values — those are what actually load.
  “默认”指 `config.yaml` **缺少该键**时采用的值；随发布包附带的 `config.yaml` 可能设了不同值，以文件里的为准。
- **Safety / 安全**: unknown keys are ignored, missing keys fall back to defaults, and a malformed
  file is quarantined to `config.bad.yaml` instead of breaking the app. 未知键被忽略、缺失键回退默认、
  文件损坏会被隔离为 `config.bad.yaml`，不会让程序崩溃。

> The **Settings** window groups parameters into five tabs — **Camera / Output / Fusion & Hotkeys /
> Mapping / Smoothing** (中文：摄像头 / 输出 / 融合与热键 / 映射 / 平滑). A few advanced keys are only
> in `config.yaml` (see **§6 Advanced** below).
> 设置窗口把参数分为五个标签页；少数高级键只在 `config.yaml` 中（见 §6）。

---

## Quick reference / 快速上手

### Recommended starting point / 推荐起步配置

For a typical 60 fps-capable webcam driving DCS World on a normal PC:
对于一台支持 60 fps 的普通摄像头、在常规电脑上驱动 DCS World：

```yaml
fps: 60                    # or 30 if your camera/lighting can't hold 60 / 摄像头或光线撑不住 60 就用 30
landmark_detect_method: 1  # 1 = fast 112px model; use 2 if you want more accuracy / 想要更精确用 2
detect_duration: 20        # full re-detect every 20 frames / 每 20 帧全帧重检测
use_ft: true               # FreeTrack shared memory (DCS) / DCS 用这个
use_npclient: true
use_accela: true           # output smoothing / 输出平滑
accela_rot_deadzone: 2.5
accela_rot_smoothing: 0.08
use_one_euro: true         # adaptive low-pass on angles + translation / 角度与平移自适应低通
one_euro_min_cutoff: 1.2
one_euro_beta: 0.25
one_euro_pos_min_cutoff: 1.0   # translation axes are in metres / 平移轴单位为米
one_euro_pos_beta: 0.5
use_ekf: false             # leave off unless you want EKF/FSA fusion and have CPU headroom
                           # 除非要 EKF/FSA 融合且 CPU 有余量，否则关闭
```

Start here, confirm the view tracks your head correctly, then fine-tune mapping and smoothing.
先按此起步，确认视角能正确跟随头部，再微调映射与平滑。

### Common goals → what to tune / 常见需求 → 调哪些参数

| Goal / 目标 | Turn these / 调这些 | Notes / 说明 |
|---|---|---|
| **Smoother, less jitter** / 更平滑、去抖动 | `use_one_euro: true` + lower `one_euro_min_cutoff` (0.8–1.2); `use_accela: true` + raise `accela_rot_deadzone` (2–4) | One-Euro adapts: strong smoothing at rest, less lag when moving. / One-Euro 会自适应：静止重滤波、运动降延迟。 |
| **Lower latency / less lag** / 更低延迟 | raise `one_euro_beta` (0.25→0.5) and `one_euro_min_cutoff`; lower `accela_rot_smoothing` and `accela_rot_deadzone` | Less filtering = snappier but noisier. / 滤波越弱越跟手但越抖。 |
| **View drifts / wobbles while your head is still** / 头不动视角仍漂移晃动 | lower `one_euro_min_cutoff` (0.6–1.0); for position drift lower `one_euro_pos_min_cutoff`; then check the mapping gain (`out_bound_*` ÷ `inp_bound_*`) isn't extreme | The gain multiplies whatever noise survives filtering: 6.9x turns 0.2° of jitter into 1.4° of wander. / 增益会放大滤波后的残余噪声：6.9 倍会把 0.2° 抖动变成 1.4° 游走。 |
| **Higher FPS on a weak CPU / heavy games** / 弱机或大型游戏提帧率 | lower `landmark_detect_method` to 1 (or 0); raise `detect_duration` to 20–30; **keep the main window hidden/minimized in-game** | The landmark model is the main per-frame cost; hiding the window auto-skips preview drawing. / 关键点模型是每帧主要开销；隐藏窗口会自动跳过预览绘制。 |
| **View rotates too far / not far enough** / 视角转动幅度过大/不足 | `out_bound_yaw/pitch/roll` (game-side range); `inp_bound_*` (sensitivity) | See **§4 Mapping** below. |
| **Pitch buzzes up/down while looking straight** / 平视时上下震动 | lower `one_euro_min_cutoff`; raise `accela_rot_deadzone`; check `out_bound_pitch` isn't extreme | This build already adds SolvePnP frame-continuity + One-Euro to reduce it. / 本版已用 PnP 帧间连续 + One-Euro 抑制此现象。 |
| **Green / garbled / tiled frames** / 画面绿屏、花屏、错位 | `capture_api: msmf`; or `capture_fourcc: mjpg`; then click **Restart Camera** | Common with virtual/phone cameras (Iriun, etc.). / 虚拟/手机摄像头常见。 |
| **Left/right reversed** / 左右方向相反 | toggle `mirror_camera` | For selfie-mirrored front cameras. / 用于前置摄像头的自拍镜像。 |
| **Re-center without alt-tabbing** / 不切窗口回中 | `recenter_hotkey` (global, default `Ctrl+X`) | The hotkey runs on its own message-loop thread rather than the UI thread: `WM_HOTKEY` is a queued message, and with both tracking threads at `AboveNormal` (one of them CPU-bound) a sim that saturates the machine would otherwise leave the key press sitting in the queue until you alt-tab out. It also needs HeadTracker to run at least as elevated as the game — UIPI drops hotkeys sent to a lower-integrity process — so if the sim runs as administrator, run HeadTracker as administrator too. / 热键跑在独立的消息循环线程上而非 UI 线程：`WM_HOTKEY` 是排队消息，两条跟踪线程都是 `AboveNormal`（其中一条持续占满 CPU），游戏再榨干机器时按键会滞留在队列里，直到你切出窗口才被处理。它还要求本程序的权限级别不低于游戏——UIPI 会丢弃发往较低完整性进程的热键——所以若模拟器以管理员身份运行，本程序也要以管理员身份运行。 |
| **More accurate pose (esp. pitch/depth)** / 姿态更精确（尤其俯仰/深度） | run the **Camera calibration** wizard; raise `landmark_detect_method` to 2–3 | Calibration solves your real lens intrinsics/distortion into `config.yaml`. / 标定求出真实内参与畸变并写入配置。 |

---

## Complete reference / 参数完整参考

### 1. Camera / 摄像头

Settings → **Camera** tab. Capture and per-frame pipeline cost.
设置 → **摄像头** 标签页。采集与每帧管线开销。

| Key (config.yaml) | Settings UI (EN / 中文) | Default | Description / 说明 |
|---|---|---|---|
| `ui_language` | Interface language / 界面语言 | `auto` | `auto` (follow OS), `en`, or `zh`. / `auto` 跟随系统、`en` 英文、`zh` 中文。 |
| `camera_id` | Camera device / 摄像头设备 | `0` | Device index of the webcam. Pick from the dropdown (it lists detected cameras). / 摄像头设备索引，可从下拉框选择。 |
| `mirror_camera` | Mirror / 镜像画面（前置摄像头） | `false` | Horizontally flips the image; use for selfie-mirrored front cameras. / 水平翻转画面，用于前置自拍镜像。 |
| `fps` | Target FPS / 目标帧率 | `30` | Requested capture frame rate. Real rate depends on the camera and lighting; if the camera can't hold it, frames get dim/slow. Shipped config uses `60`. / 请求的采集帧率，实际受摄像头与光照限制；发布配置为 60。 |
| `enable_auto_expo` | Auto exposure / 自动曝光 | `true` | Let the camera auto-expose. Turn off to fix flicker from auto-exposure hunting (then set `camera_expo` manually). / 交给摄像头自动曝光；关闭可消除自动曝光抖动，再手动设 `camera_expo`。 |
| `camera_gain` | Gain (0..1) / 增益 | `0.5` | Sensor gain, mapped to 0..255. Higher = brighter but noisier. / 传感器增益，映射到 0..255，越高越亮但噪点越多。 |
| `camera_expo` | Exposure (0..1) / 曝光 | `0.5` | Manual exposure, mapped to 0..255. Applies when `enable_auto_expo` is off. / 手动曝光，映射到 0..255，关闭自动曝光时生效。 |
| `detect_duration` | Re-detect every N frames / 每 N 帧重新检测 | `10` | How often the full-frame SCRFD face detector re-runs (CSRT tracks the ROI in between). Higher = less CPU but slower recovery if the ROI drifts. Shipped config uses `20`. / 全帧 SCRFD 重检测间隔（其间由 CSRT 跟踪 ROI）；越大越省 CPU，但 ROI 漂移后恢复越慢。发布配置为 20。 |
| `landmark_detect_method` | Landmark model / 关键点模型 | `4` | OpenSeeFace model tier `0..4`: **0/1 = 112px (fast)**, **2/3/4 = 224px (accurate)**. Lower = faster / less CPU. This is the single biggest per-frame cost. Shipped config uses `1`. / 关键点模型档位：0/1 为 112px 快速档，2/3/4 为 224px 精确档；越低越快越省 CPU，是每帧最大开销。发布配置为 1。 |
| `roi_filter_rate` | ROI smoothing / ROI 平滑率 | `0.7` | EMA weight (0..1) applied to the landmark detection ROI. Higher = smoother but slower-following ROI box. / 关键点检测 ROI 的 EMA 权重（0..1）；越大 ROI 框越平滑但跟随越慢。 |
| *(button)* | Camera calibration / 相机标定 | — | Opens the ChArUco wizard that solves real intrinsics/distortion and writes `camera_fx/fy/cx/cy`, `dist_*`, `calibrated_width/height` into `config.yaml`. / 打开 ChArUco 标定向导，求出真实内参与畸变并写入 `config.yaml`。 |

> **Performance tip / 性能提示**: to raise FPS, first lower `landmark_detect_method`, then raise
> `detect_duration`. Also keep the main window hidden/minimized while gaming — preview drawing is
> then skipped automatically. 提帧率先降 `landmark_detect_method`、再升 `detect_duration`；游戏时把主
> 窗口隐藏/最小化会自动跳过预览绘制。

### 2. Output / 输出

Settings → **Output** tab. Which game protocols to emit.
设置 → **输出** 标签页。向游戏发送哪些协议。

| Key | Settings UI (EN / 中文) | Default | Description / 说明 |
|---|---|---|---|
| `use_ft` | FreeTrack shared memory / FreeTrack 共享内存 | `false` | Emit pose via the FreeTrack/TrackIR shared-memory protocol (`FT_SharedMem`). **DCS World uses this** (set DCS Head Tracking to *TrackIR*). Shipped config uses `true`. / 通过 FreeTrack/TrackIR 共享内存协议（`FT_SharedMem`）发送姿态；**DCS 用这个**（DCS 里头部跟踪选 TrackIR）。发布配置为 true。 |
| `use_npclient` | npclient (TrackIR) / npclient (TrackIR) | `false` | Emit the npclient/TrackIR `NPClient.dll` UDP protocol. Shipped config uses `true`. / 发送 npclient/TrackIR 协议。发布配置为 true。 |
| `send_posedata_udp` | Send UDP pose data / 发送 UDP 姿态数据 | `true` | Emit the 48-byte opentrack-compatible UDP pose stream (used when feeding opentrack). / 发送 48 字节、兼容 opentrack 的 UDP 姿态流。 |
| `udp_host` | UDP host / UDP 主机 | `127.0.0.1` | Destination host for the UDP pose stream. / UDP 姿态流目标主机。 |
| `port` | UDP port / UDP 端口 | `4242` | Destination port (both the 48-byte pose stream and npclient). / 目标端口（48 字节姿态流与 npclient 共用）。 |

> **Important behavior / 重要行为**: enabling `use_ft` **or** `use_npclient` activates the **250 Hz
> filtered output path** — that path is what applies the **Mapping** bounds/expo/curves and the
> **Accela** filter. If both are off (UDP-only), pose is emitted per detection with **no bounds/expo
> mapping and no Accela** (One-Euro still applies if enabled), so most Mapping/Smoothing settings are
> ignored. For games, keep at least one of `use_ft`/`use_npclient` enabled. 启用 `use_ft` 或
> `use_npclient` 才会走 **250Hz 带滤波输出路径**——**映射**的输入/输出界、指数、曲线与 **Accela** 滤波
> 都在这条路径上生效；若两者都关（仅 UDP），则按检测频率直发、**不做映射与 Accela**（One-Euro 若开启仍
> 生效），映射/平滑设置大多被忽略。游戏请至少开启 `use_ft`/`use_npclient` 之一。

### 3. Fusion & Hotkeys / 融合与热键

Settings → **Fusion** tab. EKF fusion and the re-center hotkey.
设置 → **融合与热键** 标签页。EKF 融合与回中热键。

| Key | Settings UI (EN / 中文) | Default | Description / 说明 |
|---|---|---|---|
| `use_ekf` | EKF fusion / EKF 融合 | `false` | Enable the 13-DOF Extended Kalman Filter (velocity-aware smoothing + prediction). Adds CPU cost. / 启用 13 自由度 EKF（速度感知平滑 + 预测），会增加 CPU 开销。 |
| `use_fsa` | FSA-Net second source / FSA-Net 第二测量源 | `true` | Add FSA-Net as a second EKF measurement. **Only active when `use_ekf` is true**; adds an extra ONNX inference per frame. / 加入 FSA-Net 作为 EKF 第二测量源；**仅在 `use_ekf` 为真时生效**，每帧多一次推理。 |
| `cov_Q_lm` | cov_Q_lm | `0.006` | Measurement noise for the PnP/landmark quaternion. Higher = trust PnP less = smoother but laggier. / PnP/关键点四元数的测量噪声；越大越不信任 PnP，越平滑但越滞后。 |
| `cov_Q_fsa` | cov_Q_fsa | `0.006` | Measurement noise for the FSA-Net quaternion. / FSA-Net 四元数的测量噪声。 |
| `cov_T` | cov_T | `0.01` | Measurement noise for translation. / 平移的测量噪声。 |
| `cov_V` | cov_V | `10.0` | Process noise for linear velocity. Higher = EKF reacts faster to speed changes. / 线速度过程噪声；越大 EKF 对速度变化反应越快。 |
| `cov_W` | cov_W | `2.0` | Process noise for angular velocity. Higher = reacts faster to rotation. / 角速度过程噪声；越大对旋转反应越快。 |
| `ekf_predict_dt` | EKF predict step (s) / EKF 预测步长（秒） | `0.01` | Prediction integration step in seconds. / 预测积分步长（秒）。 |
| `pitch_offset_fsa_pnp` | FSA↔PnP pitch offset (deg) / FSA↔PnP 俯仰偏移（度） | `≈11°` | Pitch offset (stored in radians) that aligns FSA-Net to PnP. Only matters when FSA is active. / 使 FSA-Net 与 PnP 对齐的俯仰偏移（以弧度存储），仅 FSA 生效时有意义。 |
| `recenter_hotkey` | Recenter hotkey / 回中热键 | `Ctrl+X` | Global keyboard hotkey to re-center, handled on its own thread so it survives a CPU-saturating game holding focus. Format: `Ctrl+X`, `Ctrl+Alt+X`, or a bare F-key like `F13`. Change it if it collides with another app; if the game runs as administrator, run HeadTracker as administrator too (UIPI). / 全局回中热键，由独立线程处理，游戏占据前台且榨干 CPU 时依然有效；格式 `Ctrl+X`、`Ctrl+Alt+X` 或单 F 键如 `F13`，冲突时可改；若游戏以管理员身份运行，本程序也需提权（UIPI）。 |

> `hotkey_joystick_name0` / `button0` / `name1` / `button1` have been removed — joystick buttons are
> no longer polled. Slot 1 was the only trigger the legacy `pause()` ever had, so pause is gone too:
> tracking now simply runs or is stopped. An older `config.yaml` may still contain those keys; they
> are ignored on load and disappear on the next save.
> 已移除 `hotkey_joystick_*` 四个键，不再轮询摇杆按钮。slot 1 原本是旧 `pause()` 唯一的触发者，
> 因此暂停功能也一并删除：跟踪现在只有运行与停止两种状态。旧 `config.yaml` 里若仍带有这些键，
> 加载时会被忽略，下次保存时自动消失。

### 4. Mapping / 映射

Settings → **Mapping** tab. Maps your physical head motion to in-game camera motion.
设置 → **映射** 标签页。把真实头部运动映射到游戏内视角运动。

The tab shows three columns. For the **translation** rows they are **X / Y / Z** (meters); for the
**rotation** rows they are **Yaw / Pitch / Roll** (degrees).
标签页有三列：**平移**行对应 **X / Y / Z**（米）；**旋转**行对应 **偏航 / 俯仰 / 滚转**（度）。

| Key(s) | Settings UI (EN / 中文) | Default | Description / 说明 |
|---|---|---|---|
| `inp_bound_x` / `_y` / `_z` | Input bound (m) / 输入界（米） | `0.3 / 0.12 / 0.3` | How far your head moves (m) to reach full output. **Smaller = more sensitive.** / 头部移动多少米达到满输出；**越小越灵敏**。 |
| `out_bound_x` / `_y` / `_z` | Output bound (m) / 输出界（米） | `0.77 / 0.73 / 0.75` | Game-side translation range at full deflection. / 满偏时游戏侧的平移范围。 |
| `expo_trans_x` / `_y` / `_z` | Expo (0..1) / 指数曲线 | `0` | Cubic expo on translation. `0` = linear; higher = finer control near center, faster at the edges. / 平移三次指数曲线；0 为线性，越大中心越细腻、边缘越快。 |
| `inp_bound_yaw` / `_pitch` / `_roll` | Input bound (deg) / 输入界（度） | `26 / 16 / 45` | How far you turn (deg) to reach full output. **Smaller = more sensitive.** / 转头多少度达到满输出；**越小越灵敏**。 |
| `out_bound_yaw` / `_pitch` / `_roll` | Output bound (deg) / 输出界（度） | `120 / 75 / 43.5` | Game-side rotation range at full deflection. Gain = `out_bound` ÷ `inp_bound` (~4.6x for yaw); it multiplies residual jitter too, so bigger is not better. / 满偏时游戏侧的旋转范围。增益 = `out_bound` ÷ `inp_bound`（偏航约 4.6 倍）；它同样放大残余抖动，并非越大越好。 |
| `expo_eul_yaw` / `_pitch` / `_roll` | Expo (0..1) / 指数曲线 | `0` | Cubic expo on rotation (same idea as translation expo). / 旋转三次指数曲线（同平移）。 |
| `curve_trans_x/y/z`, `curve_eul_yaw/pitch/roll` | *(Response curve editor)* / 编辑响应曲线 | `""` | Per-axis response curve that **overrides Expo** when non-empty. Edit visually via the curve editor; serialized as `-1,-1;x,y;…;1,1`. / 每轴响应曲线，非空时**覆盖 Expo**；在曲线编辑器可视化编辑，序列化为 `-1,-1;x,y;…;1,1`。 |

> **Sensitivity vs. range / 灵敏度 vs 幅度**: `inp_bound_*` sets how much *you* move to reach the ends
> (sensitivity); `out_bound_*` sets how far the *game camera* goes (range). Gain ≈ `out_bound / inp_bound`
> — a very high gain (e.g. large `out_bound_pitch` with small `inp_bound_pitch`) amplifies tiny head
> noise, so pair high gain with smoothing. `inp_bound` 决定你需移动多少（灵敏度），`out_bound` 决定游戏
> 视角走多远（幅度）；增益 ≈ `out_bound / inp_bound`，增益过高会放大细微抖动，需配合平滑。

### 5. Smoothing / 平滑

Settings → **Smoothing** tab. The Accela output filter (ported from opentrack).
设置 → **平滑** 标签页。Accela 输出滤波（移植自 opentrack）。

| Key | Settings UI (EN / 中文) | Default | Description / 说明 |
|---|---|---|---|
| `use_accela` | Use Accela filter / 使用 Accela 滤波 | `false` | Enable Accela output smoothing (deadzone + slew limiting at 250 Hz). Shipped config uses `true`. / 启用 Accela 输出平滑（250Hz 死区 + 变化率限制）；发布配置为 true。 |
| `double_accela` | Double filter / 双重滤波 | `false` | Run Accela twice for stronger smoothing (more lag). / 连跑两遍 Accela，平滑更强但更滞后。 |
| `accela_rot_smoothing` | Rotation smoothing / 旋转平滑 | `0.08` | Rotation delta divisor. At the 250 Hz output rate each tick closes about `1/(smoothing × 250)` of the remaining gap to the target — higher smoothing = smaller step = slower, smoother rotation (more lag). / 旋转增量除数。250Hz 输出下每 tick 约闭合到目标剩余差值的 `1/(smoothing×250)`；smoothing 越大步进越小、旋转越慢越平滑（也更滞后）。 |
| `accela_rot_deadzone` | Rotation deadzone (deg) / 旋转死区（度） | `3.0` | Rotation changes below this (deg) are suppressed — kills at-rest jitter. Higher = steadier but a more "steppy" feel. / 低于此角度（度）的旋转变化被抑制，可消除静止抖动；越大越稳但越有“台阶感”。 |
| `accela_pos_smoothing` | Translation smoothing / 平移平滑 | `0.03` | Translation delta divisor (same idea as rotation smoothing). / 平移增量除数（同旋转）。 |
| `accela_pos_deadzone` | Translation deadzone (m) / 平移死区（米） | `0.03` | Translation changes below this (m) are suppressed. / 低于此距离（米）的平移变化被抑制。 |

### 6. Advanced — config.yaml only / 高级 — 仅 config.yaml

These are **not** in the Settings window; edit `config.yaml` directly.
这些**不在**设置窗口中，需直接编辑 `config.yaml`。

**Live / 有效参数**

| Key | Default | Description / 说明 |
|---|---|---|
| `capture_api` | `dshow` | Camera backend: `dshow`, `msmf`, or `any`. `msmf` often fixes green/tiled/garbled frames from virtual cameras. / 采集后端；`msmf` 常能修复虚拟摄像头的绿屏/错位/花屏。 |
| `capture_fourcc` | `""` | Request a pixel format (e.g. `mjpg`, `yuy2`). Empty = let the backend choose. Helps some virtual cameras. / 请求像素格式；空为由后端决定，可修复部分虚拟摄像头问题。 |
| `cervical_face_model` | `-0.088` | Z-offset (m) applied to the 3D face model, shifting the pose pivot (roughly the neck). / 施加到 3D 人脸模型的 z 偏移（米），移动姿态枢轴（约颈部）。 |
| `cervical_face_model_x` | `0.12` | Neck-pivot X offset used by the EKF ground-speed estimate. **Only when `use_ekf` + `enable_face_spd_est`.** / EKF 地速估计用的颈部枢轴 X 偏移；**仅 `use_ekf` + `enable_face_spd_est` 时生效**。 |
| `cervical_face_model_y` | `0.16` | Neck-pivot Y offset (same conditions as above). / 颈部枢轴 Y 偏移（条件同上）。 |
| `enable_face_spd_est` | `true` | Feed tracker-derived ground speed into the EKF. **Only when `use_ekf`.** / 把跟踪器推算的地速喂入 EKF；**仅 `use_ekf` 时生效**。 |

**One-Euro filter (adaptive pose smoothing) / One-Euro 滤波（姿态自适应平滑）** — enabled with
`use_one_euro`; applied to yaw/pitch/roll **and** to X/Y/Z on the raw head pose, ahead of the
bounds/expo gain and of Accela. 用 `use_one_euro` 开启，在映射增益与 Accela **之前**作用于原始头部姿态：
偏航/俯仰/滚转（度）**以及** X/Y/Z 平移（米）。

Running before the gain is what makes the numbers below mean what they say: One-Euro's cutoff grows
with the measured speed, so filtering *after* the gain would multiply that speed — and the cutoff —
by the gain, opening the filter exactly when jitter is worst. 在增益之前滤波，下列参数才名副其实：
One-Euro 的截止频率随速度上升，若在增益**之后**滤波，速度与截止频率会被同倍放大，恰在抖动最严重时
把滤波器打开。

| Key | Default | Description / 说明 |
|---|---|---|
| `use_one_euro` | `false` | Enable the One-Euro adaptive low-pass (angles **and** translation). Shipped config uses `true`. / 启用 One-Euro 自适应低通（角度**与**平移）；发布配置为 true。 |
| `one_euro_min_cutoff` | `1.2` | Rotation cutoff (Hz) at rest. **Lower = smoother / less jitter**, but slightly more lag when still. / 旋转静止截止频率（Hz）；**越低越平滑、抖动越少**，但静止时略有延迟。 |
| `one_euro_beta` | `0.25` | Rotation speed coefficient (per deg/s). **Higher = less lag when moving**, but passes more jitter. / 旋转速度系数（按度/秒）；**越高运动时延迟越小**，但会放过更多抖动。 |
| `one_euro_deriv_cutoff` | `1.0` | Low-pass cutoff (Hz) for the derivative estimate, shared by rotation and translation. Rarely needs changing. / 导数估计的低通截止（Hz），旋转与平移共用；一般无需改动。 |
| `one_euro_pos_min_cutoff` | `1.0` | Translation cutoff (Hz) at rest. Lower it when the view drifts while your head is still. / 平移静止截止频率（Hz）；头不动而视角漂移时调低。 |
| `one_euro_pos_beta` | `0.5` | Translation speed coefficient (per m/s). Do **not** copy the rotation beta here: a deliberate head slide is ~0.5 m/s, a head turn ~100 deg/s. / 平移速度系数（按米/秒）。**不要**照抄旋转 beta：平移约 0.5 米/秒，转头约 100 度/秒。 |

**Reserved / no effect in the .NET version / 保留 — .NET 版暂无作用**

Kept only for `config.yaml` compatibility with the legacy C++ FOXTracker. Changing them does nothing
in this rewrite. 仅为兼容旧版 C++ FOXTracker 的 `config.yaml` 而保留，本重写版中修改它们无任何效果。

| Key | Status / 状态 |
|---|---|
| `enable_gpu` | Reserved — ONNX currently runs on **CPU**; not wired yet. / 保留 —— ONNX 目前跑 **CPU**，尚未接线。 |
| `fsa_pnp_mixture_rate` | Legacy — the EKF fuses PnP + FSA directly; this blend is unused. / 遗留 —— EKF 直接融合 PnP+FSA，此混合率未使用。 |
| `enable_multithread_detect` | Legacy — the .NET pipeline uses a single processing thread. / 遗留 —— .NET 管线为单处理线程。 |
| `retrack_queue_size` | Legacy — no effect. / 遗留 —— 无作用。 |
| `disp_duration`, `disp_max_series_size` | Legacy chart-window settings — no effect in .NET. / 遗留的图表窗口设置 —— .NET 中无作用。 |

Calibration keys (`camera_fx/fy/cx/cy`, `dist_k1/k2/p1/p2/k3`, `calibrated_width/height`,
`calibration_rms`) are written automatically by the calibration wizard — you normally don't edit them
by hand. 标定相关键由标定向导自动写入，通常无需手改。

---

## Suggested tuning workflow / 调参工作流建议

1. **Get a clean signal first / 先拿到干净信号**: pick the camera, set `fps`, disable auto-exposure
   if it hunts, and run **Camera calibration**. 选好摄像头、设 `fps`，自动曝光乱跳就关掉，并做一次**相机标定**。
2. **Verify tracking / 验证跟踪**: start tracking (F9), watch the preview and the re-projection error
   (shown as `rms …px` in the status bar). If the ROI drifts or you get green frames, fix
   `capture_api`/`capture_fourcc` and lower `landmark_detect_method` only if FPS is low. 启动跟踪（F9），
   看预览与重投影误差（状态栏 `rms …px`）；ROI 漂移或绿屏先修 `capture_api`/`capture_fourcc`，帧率低才降
   `landmark_detect_method`。
3. **Set mapping / 设映射**: adjust `out_bound_*` for the in-game range you want and `inp_bound_*` for
   sensitivity; use Expo or the curve editor for a non-linear feel. 用 `out_bound_*` 定游戏内幅度、
   `inp_bound_*` 定灵敏度，用 Expo 或曲线编辑器做非线性手感。
4. **Add smoothing last / 最后加平滑**: enable `use_one_euro` and/or `use_accela`, then raise deadzone /
   lower `one_euro_min_cutoff` until at-rest jitter is gone but turning still feels responsive.
   开启 `use_one_euro` 和/或 `use_accela`，逐步加死区/降 `one_euro_min_cutoff`，直到静止不抖、转头仍跟手。
5. **Bind re-center / 绑定回中**: set `recenter_hotkey` so you can re-center in-game without
   alt-tabbing. 设 `recenter_hotkey`，游戏内即可回中。

Change **one thing at a time** and re-test — most settings interact. 每次只改**一项**并重新测试——多数参数相互影响。

## Troubleshooting / 故障排查

| Symptom / 现象 | Likely fix / 处理 |
|---|---|
| Low FPS in heavy games / 大型游戏帧率低 | `landmark_detect_method: 1`, `detect_duration: 20–30`, hide the main window in-game. / 降关键点档位、升重检测间隔、游戏时隐藏主窗口。 |
| Jitter at rest / 静止抖动 | `use_one_euro: true` + lower `one_euro_min_cutoff`; `use_accela: true` + raise `accela_rot_deadzone`. |
| Position drifts while still / 头不动而平移漂移 | Lower `one_euro_pos_min_cutoff`; raise `accela_pos_deadzone`. |
| Laggy / rubber-bandy view / 视角发飘滞后 | Raise `one_euro_beta` & `one_euro_min_cutoff`; lower Accela smoothing/deadzone; check `out_bound`/`inp_bound` gain isn't extreme. |
| Pitch buzz looking straight / 平视俯仰震动 | Lower `one_euro_min_cutoff`; raise `accela_rot_deadzone`; reduce an extreme `out_bound_pitch`. |
| Green / garbled / tiled frames / 绿屏花屏错位 | `capture_api: msmf` or `capture_fourcc: mjpg`, then **Restart Camera**. |
| Left/right reversed / 左右相反 | Toggle `mirror_camera`. |
| Hotkey dead in one game only (fine in MSFS, dead in DCS) / 热键只在某个游戏里失效（MSFS 正常、DCS 无效） | Read `crash.log`. Start-up writes `hotkey '…': parsed=…, registered=…, error=…, elevated=…`; every press writes `hotkey fired -> recenter` followed by `ui thread latency at hotkey: N ms`. **No `fired` line at all** → the key never reaches the process: check `elevated=` first (if the game runs as administrator and this says False, UIPI is dropping it — run HeadTracker as administrator); if `elevated=True`, something else is swallowing the key (an in-game overlay, or the game's own low-level keyboard hook). **A `fired` line with no visible effect** → the re-center did happen; look downstream in the output instead. / 看 `crash.log`。启动时写入 `hotkey '…': parsed=…, registered=…, error=…, elevated=…`；每次按键写入 `hotkey fired -> recenter`，紧跟一行 `ui thread latency at hotkey: N ms`。**完全没有 `fired` 行** → 按键根本没到达本进程：先看 `elevated=`（若游戏以管理员身份运行而这里是 False，就是 UIPI 丢弃了它——以管理员身份运行 HeadTracker）；若 `elevated=True`，则是有别的东西吞掉了按键（游戏内覆盖层，或游戏自己的低级键盘钩子）。**有 `fired` 行却没有效果** → 回中确实执行了，问题在输出链路的下游。 |
| No movement in game / 游戏里没反应 | Enable `use_ft` (DCS) and/or `use_npclient`; in DCS set Head Tracking to *TrackIR*; re-center with the hotkey. / 开启 `use_ft`/`use_npclient`，DCS 里头部跟踪选 TrackIR，用热键回中。 |
| Tracking lost / ROI drifts / 跟踪丢失 | Lower `detect_duration` (re-detect sooner); improve lighting; the ROI auto-resets on total loss. / 降 `detect_duration`、改善光照；完全丢失时会自动重置 ROI。 |

---

*Part of the [HeadTracker](../README.md) project. Parameters correspond to the Settings window and
`config.yaml`; see the README for build/run instructions.*
*本文档属于 [HeadTracker](../README.md) 项目，参数对应设置窗口与 `config.yaml`；构建/运行见 README。*
