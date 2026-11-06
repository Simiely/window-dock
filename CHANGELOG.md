# CHANGELOG.md · 版本记录

> 按版本从新到旧。关键问题与方案见 [DEVELOPMENT.md](./DEVELOPMENT.md)。

## v0.1.0 — 2026-11-06（调研 + 只读探针完成）

### 新增

- **调研与方案**（[docs/调研与方案.md](./docs/调研与方案.md)）：需求拆解 R1–R8、10 个同类方案对比、逐项机制可行性、技术选型（C# / .NET 8 + WPF）、三阶段计划与风险清单。
- **只读探针**（`probe/ --readonly`）：枚举过滤统计、`GetWindowRect` 与 `DWMWA_EXTENDED_FRAME_BOUNDS` 四边差、显示器与 DPI、UAC / 提权窗口、虚拟桌面归属、PowerToys 热键占用。全程未创建、移动、激活任何窗口，未注册热键。
- **交互探针工程**（P1–P8）：前台激活、命中判定、跨 DPI、枚举与 UWP、原位还原、`.lnk` / `.exe` 拖入、覆盖层框选、全局热键。**已就位但未运行**，默认无参数只打印用法。
- **单项目文档骨架**：README / AGENTS / DEVELOPMENT / CHANGELOG 四件套 + `docs/`。

### 实测结论（修正原调研的推断）

1. **枚举必须先过滤**：本机 `EnumWindows` 483 个 → 按 Alt+Tab 规则过滤后 17 个。
2. **命中判定不能用 `GetWindowRect`**：与视觉边界最大差 8 px，且同类窗口中存在差值为 0 的情况，不能用固定补偿。
3. **最小化窗口不能用 `GetWindowRect` 做快照**：实测 5 个中 4 个为 `-32000` 哨兵，必须读 `WINDOWPLACEMENT.rcNormalPosition`。
4. **`DWM_CLOAKED_SHELL` 不等于「在别的虚拟桌面」**：实测 5 个 0x2 窗口全部在当前桌面。

### 未能验证（本机条件不足）

- **混合 DPI**：两块屏均为 96 DPI / 100%，需一块 125% 或 150% 的屏幕复测。
- **UWP 识别**：当前桌面没有 `ApplicationFrameWindow` 窗口，需先打开「计算器」或「设置」。
- **前台激活 / 提权拖放 / 覆盖层 / 热键注册**：属于交互探针范围，尚未运行。
