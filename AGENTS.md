# AGENTS.md · 项目规则

> 📌 **文档基线**：2026-11-06（commit `e1e57e7`）完成四件套
> **更新文档/代码后，请更新此行**（日期 + 新 commit hash），并在 CHANGELOG 追加版本

## 技术栈

- C# / .NET 8 + WPF（`net8.0-windows`，`UseWPF=true`，`PlatformTarget=x64`）
- 应用清单：`asInvoker` + `PerMonitorV2` + `longPathAware`；**默认不提权**
- Win32 P/Invoke：`EnumWindows` / `GetWindowRect` / `DwmGetWindowAttribute` / `SetWindowPos` / `RegisterHotKey` / `RegisterDragDrop`
- 发布形态：单 exe、托盘常驻；不注入目标进程

## 关键坑（全部来自真机实测，不要凭文档改）

1. **`EnumWindows` 结果绝大多数不是候选**：本机 483 个顶层窗口，按 Alt+Tab 规则过滤后只剩 17 个。过滤链必须包含 `IsWindowVisible` / `DWMWA_CLOAKED` / `WS_EX_TOOLWINDOW` / 空标题 / 零尺寸 / 自身进程。
2. **`GetWindowRect` ≠ 视觉边界**：实测最大外扩 8 px（左右各 7~8，上下各 5~8），且**同类窗口里存在差值为 0 的**（Chromium 8 个里 3 个全 0）。命中判定用 `DWMWA_EXTENDED_FRAME_BOUNDS`，公差按 ±8 px 设计，禁止写死固定补偿。
3. **最小化窗口的 `GetWindowRect` 是哨兵值**：实测 5 个里 4 个为 `-32000,-32000,...`。快照必须用 `WINDOWPLACEMENT` 的 `rcNormalPosition` + `showCmd`。
4. **`DWM_CLOAKED_SHELL`(0x2) 不等于「在别的虚拟桌面」**：实测 5 个 0x2 窗口 `IsWindowOnCurrentVirtualDesktop` 全为 true。桌面归属要单独查 `IVirtualDesktopManager`，不能靠 cloaked 值推断。
5. **热键默认组合必须避让**：FancyZones 默认占 `Win+Shift+``，AltWindowCycle 占 `Alt+``，Workspaces 占 `Win+Ctrl+``。注册失败要读 `GetLastWin32Error()`，1409 表示已被占用。
6. **前台激活有锁**：`SetForegroundWindow` 不满足前台条件时只闪任务栏。兜底顺序（待 P1 实测确认成功率）先 `SW_RESTORE` → `SetForegroundWindow` → 失败 `FlashWindowEx`。
7. **提权是单向门**：默认 `asInvoker`。提权后自身收不到普通 Explorer 的 OLE 拖放（UIPI），且无法操作提权目标窗口。

## 约定

- 所有窗口坐标、矩形、快照一律按**物理像素**建模；DIP 仅用于 UI 布局。
- 不修改目标窗口的 `WS_EX_*`、不模拟标题栏拖动、不 `SetParent` 跨进程。
- 探针默认不运行，必须显式 `--readonly` 或 `--interactive`。
- 文档用中文；文件名中文命名，不带空格。

## 常用命令

```powershell
# 构建探针
cd probe; dotnet build Probe.csproj

# 只读采集（不创建/移动/激活任何窗口）
.\bin\Debug\net8.0-windows\ProbeHost.exe --readonly

# 交互探针（会自建靶窗口，需人工挑时机）
.\bin\Debug\net8.0-windows\ProbeHost.exe --interactive
```

## 详细规则（按需读取）

- @docs/探针结果与实施计划.md · 实测数据与 P0/P1 验收点
- @docs/调研与方案.md · 同类方案对比与机制可行性
- @probe/README.md · 每项探针的跑法与通过标准
