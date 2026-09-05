# 性能测量手册 / Performance measurement runbook

目的：任何"变快了/变慢了"的说法都必须由本手册的数字支撑。仪表只测量、不优化；
优化提交必须附同一矩阵的前后对照。
Purpose: every "faster/slower" claim must be backed by the numbers below. The instrumentation
only measures; an optimization commit must attach a before/after of the same matrix.

## 1. 读数来源 / Where the numbers live

运行中每 10 秒一行落 `crash.log`（运行目录），状态栏 `[DIAG ...]` 行同步显示紧凑版：
While running, one line lands in `crash.log` (next to the exe) every 10 s; the status bar
`[DIAG ...]` line shows the compact form at the same time:

```
note: perf scrfd=0.35 track=2.90 lm=4.80 pnp=0.30 fsa=0.00 preview=1.60 proc=10.20 out_wakes=612/s out_pub=250/s cap=59.7fps read=16.2ms prevpub=2.10ms
```

| 键 / key | 含义 / meaning |
|---|---|
| `scrfd` | SCRFD 人脸检测，**摊到每处理帧**的 ms（它每 `detect_duration` 帧才跑一次）/ ms per processed frame, amortized (runs once per `detect_duration` frames) |
| `track` | CSRT ROI 跟踪 / CSRT ROI update |
| `lm` | OpenSeeFace landmark 推理 / landmark inference |
| `pnp` | solvePnP + 重投影误差 / solvePnP + reprojection RMS |
| `fsa` | FSA-Net（仅 EKF 融合开启时非 0）/ FSA-Net (non-zero only with EKF fusion) |
| `preview` | 管线内 DrawPreview（clone + 画框点）；窗口收托盘时应读 ~0，以此验证门控 / in-pipeline DrawPreview; reads ~0 while the window is trayed, which verifies the gating |
| `proc` | 整帧处理均值，应 ≈ 各阶段之和 / whole-frame mean; should equal the sum of the stages |
| `out_wakes` / `out_pub` | 250 Hz 输出循环每秒唤醒次数 / 每秒实际 tick 数；差值即 `Sleep(1)` 轮询浪费 / output-loop wakes per second vs scheduled ticks; the gap is the `Sleep(1)` poll waste |
| `cap` / `read` | 采集层帧率与单次 `cap.Read` 阻塞 ms / capture rate and blocking read time |
| `prevpub` | UI 线程一次预览刷新（clone + 像素拷贝 + BitmapSource）的 ms EMA / UI-thread cost of one preview refresh |

阶段值均为 500 ms 窗口内"每处理帧"均值，只由处理线程写，无锁。
Stage values are 500 ms-window means per processed frame, written only by the process thread.

## 2. A/B 隔离矩阵 / Isolation matrix

每行 60 秒稳态、脸在框内；用第 3 节脚本记进程 CPU（core 数，1.00 = 一个满核）。
Each row 60 s steady state with a face in frame; record process CPU with the script in §3
(in cores, 1.00 = one full core).

| 行 / row | 状态 / state | 差值含义 / difference isolates |
|---|---|---|
| A | 预览开 + 跟踪开（正常使用）/ preview on + tracking on | 总量 / total |
| B | 窗口收托盘（预览门控关）+ 跟踪开 / window trayed (preview gated off) + tracking on | A−B = 预览/WPF 路径 / preview & WPF path |
| C | F9 停跟踪（相机释放）/ tracking stopped (camera released) | B−C = 采集+推理管线 / capture + inference pipeline |

交叉验证 / cross-check：B 行的 `preview` 与 `prevpub` 应读 ~0；C 行只剩 UI 基线。
再加一行 fps 30（设置里改）对照 A，得到推理成本对帧率的线性度。
Row B must read `preview`/`prevpub` ≈ 0; row C leaves only the UI baseline. Add an fps-30
repeat of row A to check inference scales linearly with frame rate.

## 3. CPU 采样脚本 / CPU sampler

```powershell
# cores used by the process, one sample per second; 1.00 = one full core
param($Name = 'HeadTracker', $Seconds = 60)
$p = Get-Process $Name | Select-Object -First 1
Start-Sleep 2; $p.Refresh()          # skip startup
$prev = $p.TotalProcessorTime; $wall = [Diagnostics.Stopwatch]::StartNew()
$samples = @()
while ($wall.Elapsed.TotalSeconds -lt $Seconds) {
    Start-Sleep 1; $p.Refresh()
    $now = $p.TotalProcessorTime
    $samples += ($now - $prev).TotalMilliseconds / 1000.0
    $prev = $now
}
$mid = $samples | Select-Object -Skip 5           # drop the ramp
"cores avg = {0:F2}  max = {1:F2}" -f (($mid | Measure-Object -Average).Average), ($mid | Measure-Object -Maximum).Maximum
```

## 4. 与 foxtracker 对照的口径 / Comparing against foxtracker

- 同机、同相机、同分辨率、同 landmark 档位、**同 fps**：两边各跑 30 与 60 两档。
  Same machine, camera, resolution, landmark tier and **same fps**: run both at 30 and 60.
- 比 **CPU ÷ fps（每帧成本）**，不比 CPU 总量：出厂配置 fps=60，若参考程序跑 30，
  总量差的一半只是帧数。Compare **CPU ÷ fps (per-frame cost)**, never totals: the shipped
  config runs 60 fps, so half of any total-CPU gap vs a 30 fps reference is just frame count.
- 两边都开预览各测一次、都关预览各测一次（预览实现不同，混比会污染推理对照）。
  Measure preview-on and preview-off on both sides; the preview stacks differ and mixing
  them pollutes the inference comparison.

## 5. 决策规则 / Decision rule

只优化占比 >30% 或绝对值 >0.30 core 的项；每项一个提交，提交信息附本矩阵前后两行。
Optimize only items above 30% share or 0.30 core absolute; one commit per item, with the
matrix rows before and after in the commit message. 先验嫌疑排序不作数，测量作数。
