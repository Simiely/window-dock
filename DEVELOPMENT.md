# DEVELOPMENT.md · 开发说明

> 结构固定三块：项目概览 → 架构说明 → 关键问题与方案。
> 关键问题一坑一篇，格式与 knowledge-base 一致，方便后续提炼。超过 200 行时拆到 `docs/架构.md`、`docs/问题记录/`。

## 一、项目概览

**要做什么**：一条常驻侧栏，把已在运行的窗口收进来；支持单个热键调取、成组调取；抓取模式下矩形（P1 加套索）框选成组，之后整组释放回原位。

**非目标**：

- 不做跨进程 `SetParent` 容器化（会破坏目标窗口消息循环、输入焦点、DPI 感知与 DWM 合成）。
- 不模拟标题栏拖动、不改目标窗口扩展样式（避免与 FancyZones / Groupy / WindowTinter 抢状态）。
- 不做跨虚拟桌面切换（只检测并提示）。
- 不默认提权。

**当前状态**：调研完成；只读探针已在真机跑完；交互探针工程就位但未运行；主体程序未开始。

**环境基线（实测）**：Windows 11 build 26100 · x64 · 双屏 2560×1440 均 100% · 非管理员 · UAC 开启 · PowerToys 常驻。

## 二、架构说明

### 模块划分

| 模块 | 职责 | 边界 |
|---|---|---|
| **M1 WindowIndex** | `EnumWindows` + Alt+Tab 过滤 + cloaked 判定 + AppKey | 只读枚举；增量靠 `SetWinEventHook`（节流 ≥100 ms） |
| **M2 Geometry** | 视觉矩形、位置快照、还原换算 | 全部物理像素；不碰目标窗口样式 |
| **M3 SessionStore** | 组 / 窗口 / 布局的 JSON 持久化 | 只存快照，不存 HWND 作为唯一身份 |
| **M4 DockWindow** | 置顶无激活侧栏（WPF 分层窗口） | `WS_EX_TOPMOST｜TOOLWINDOW｜NOACTIVATE`，`SW_SHOWNOACTIVATE` |
| **M5 DropIn** | `.lnk` / `.exe` 拖入（OLE `IDropTarget` + `CF_HDROP`） | 依赖 P6 结论；不提权 |
| **M6 HotkeyMgr** | 注册 / 注销 / 冲突检测 | 冲突必须读 `GetLastWin32Error()`，不静默 |
| **M7 Activator** | 单窗口与成组调取 | 兜底策略待 P1 数据决定 |

### 位置快照数据模型

```json
{
  "hwnd": 123456,
  "pid": 4321,
  "processStartTime": "2026-11-06T10:00:00Z",
  "appKey": "exe:notepad.exe",
  "showCmd": 1,
  "rcNormalPosition": [114, 83, 1794, 1222],
  "visualRect": [107, 78, 1801, 1229],
  "monitorDevice": "\\\\.\\DISPLAY1",
  "dpi": 96
}
```

- `hwnd` **只用于会话内**；恢复时先 `IsWindow` + PID + 进程启动时间二次校验。
- 最小化窗口只信 `rcNormalPosition`，`visualRect` 留空。
- 显示器找不到（拔屏）→ 落到主屏工作区居中。

### 线程模型

- UI：WPF 单线程（Dock、覆盖层、设置）。
- 窗口事件：`SetWinEventHook` 回调在非 UI 线程，`Dispatcher.BeginInvoke` 回主线程，`LOCATIONCHANGE` 合并 ≥100 ms。
- 拖放：OLE 需要 STA 线程，`RegisterDragDrop` 在 UI 线程完成。

## 三、关键问题与方案

### 问题：`EnumWindows` 返回 483 个窗口，无法直接当候选

**TL;DR**：必须按 Alt+Tab 规则过滤，本机 483 → 17。

- 问题：直接把 `EnumWindows` 结果当「可管理窗口」，列表里全是 shell / 消息 / 挂起窗口。
- 根因：顶层窗口包含大量不可见、无标题、工具窗口与 cloaked 窗口。
- 解决：过滤链 `IsWindowVisible && !IsCloaked && !WS_EX_TOOLWINDOW && pid != self && title.Length>0 && width>0 && height>0`。
- 预防：把「483/17」作为回归基线，改动过滤逻辑后必须复测。

### 问题：`GetWindowRect` 与视觉边界不一致（最多 8 px）

**TL;DR**：命中判定用 `DWMWA_EXTENDED_FRAME_BOUNDS`，公差 ±8 px，禁止固定补偿。

- 问题：按 `GetWindowRect` 做框选命中，小窗口会被系统性放大命中范围。
- 根因：DWM 阴影 / 圆角使窗口矩形比可见内容更外扩，且不同窗口风格外扩量不同（Chromium 8 个里 3 个为 0）。
- 解决：逐窗口取 `EXTENDED_FRAME_BOUNDS`；命中阈值「交集面积 ≥50% 或中心点在选区内」。
- 预防：任何「窗口矩形」相关逻辑先问一句：这是视觉矩形还是窗口矩形。

### 问题：最小化窗口的 `GetWindowRect` 是 `-32000` 哨兵

**TL;DR**：快照只读 `WINDOWPLACEMENT`，不要读 `GetWindowRect`。

- 问题：保存最小化窗口位置后还原到屏幕外。
- 根因：实测 5 个最小化窗口中 4 个 `GetWindowRect` 为 `-32000,-32000,...`，真实位置在 `rcNormalPosition`。
- 解决：快照 = `WINDOWPLACEMENT`（`showCmd` + `rcNormalPosition`）+ DPI + 显示器名；还原前先 `SW_RESTORE`。
- 预防：新增任何「位置读取」入口都走同一封装，禁止散落调用 `GetWindowRect`。

### 问题：`DWM_CLOAKED_SHELL` 被误当作「其他虚拟桌面」

**TL;DR**：cloaked 只用于过滤，桌面归属要单独查 `IVirtualDesktopManager`。

- 问题：原调研文档把 `DWM_CLOAKED_SHELL(0x2)` 等同于「在别的虚拟桌面」。
- 根因：实测 5 个 0x2 窗口 `IsWindowOnCurrentVirtualDesktop` 全部为 true，shell cloaking 还有其他原因。
- 解决：过滤只看「是否 cloaked」；需要归属时调 `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop`，只提示不切换。
- 预防：任何 DWM 枚举值都要以实测为准，不凭命名推断语义。

### 问题：默认热键与 PowerToys 冲突

**TL;DR**：避开 `Win+Shift+``、`Alt+``、`Win+Ctrl+``；注册失败必须报错。

- 问题：选了一个「看起来没人用」的组合，注册静默失效。
- 根因：FancyZones 默认占 `Win+Shift+``，AltWindowCycle 占 `Alt+``，Workspaces 占 `Win+Ctrl+``；配置能读到，但注册状态无法只读确认。
- 解决：启动时 `RegisterHotKey` 检查返回值，`GetLastWin32Error()==1409` 时提示换键；默认候选避开上述组合。
- 预防：热键清单以只读解析 PowerToys 配置为基线，新增组合前先跑一遍 `--readonly`。

### 问题（待实测）：前台激活成功率与 UIPI 边界

**TL;DR**：目前只有文档推断，没有实测数字；P1 探针跑完才能定方案。

- 已知：`SetForegroundWindow` 受前台锁限制，失败只闪任务栏；UAC 已开启，桌面有 3 个提权窗口。
- 未验证：4 种做法（直接 / `AllowSetForegroundWindow` / `AttachThreadInput` / Alt 模拟）的成功率；非提权进程对提权窗口的实际结果；OLE 拖放在提权目标上的表现。
- 下一步：跑 `--interactive`，以「≥18/20 成功」为通过标准；未达标就把兜底策略降级为「只闪任务栏 + 提示」。

## 四、验收基线（已确认，可直接用于回归）

| 项 | 基线 |
|---|---|
| 可用窗口数 | 17 ± 2（本机当前桌面） |
| 视觉边界差值 | 绝对值 ≤ 8 px |
| 最小化窗口哨兵比例 | 期望 100% 的 `GetWindowRect` 落在 `-32000`，快照必须不受影响 |
| 坐标换算 | 96 DPI 时 DIP = px；跨屏只差平移量 |
| 热键冲突 | 已占用组合注册必须返回 false + 1409 |
