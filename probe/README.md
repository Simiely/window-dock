# 探针工程运行说明（ProbeHost）

一个独立的小工程，用来验证「窗口 Dock」方案里 6 个需要交互的技术点。
默认**不运行任何探针**；只有显式加 `--interactive` 才会启动测试窗口。

## 0. 安全边界

| 模式 | 会做什么 | 不会做什么 |
|------|----------|------------|
| 无参数 | 只打印用法 | 什么都不做 |
| `--readonly` | 只读 `EnumWindows` / `GetWindowRect` / `DwmGetWindowAttribute` / `GetDpiForWindow` / 注册表 / PowerToys 配置 | 不创建、不移动、不激活任何窗口；不注册热键；不发送输入 |
| `--interactive` | 启动**自己创建的** 2 个记事本、1 个画图、2 个 WPF 测试窗口，作为靶子 | 不碰用户已有窗口；热键用完即注销；结束时关闭所有靶窗口 |

交互模式会移动/激活它自己创建的靶窗口，这是设计所需。**运行前请先保存你在其他窗口里的工作**：探针会短暂抢占前台，但不会修改其他程序。

## 1. 构建

```powershell
cd probe
dotnet build Probe.csproj
dotnet build dpi-child\DpiChild.csproj      # P3 需要，可选
```

产物：`probe\bin\Debug\net8.0-windows\ProbeHost.exe`。

## 2. 只读模式（随时可跑）

```powershell
cd probe
.\bin\Debug\net8.0-windows\ProbeHost.exe --readonly
```

输出到控制台，并写 `out\readonly-console.txt`、`out\readonly.json`。内容包括：

- 显示器列表、每块屏的 DPI 与缩放（`GetDpiForMonitor` + 用真实窗口 `GetDpiForWindow` 交叉验证）
- 顶层窗口枚举统计：total / usable / hidden / noTitle / toolwindow / cloaked / 自身进程 / 零尺寸 / 带 owner
- `GetWindowRect` 与 `DWMWA_EXTENDED_FRAME_BOUNDS` 的四边差，按窗口风格分类统计
- 最小化窗口的 `GetWindowRect` 哨兵值与 `WINDOWPLACEMENT.rcNormalPosition` 对比
- 自身是否提权、`EnableLUA` / `ConsentPromptBehaviorAdmin` / `PromptOnSecureDesktop`、当前桌面上的提权窗口数
- cloaked 0x2 的窗口是否在别的虚拟桌面（`IVirtualDesktopManager` 只读查询）
- PowerToys 已配置的全局热键清单（从 `%LOCALAPPDATA%\Microsoft\PowerToys` 的 json 解析，**不注册**）

## 3. 交互模式（需要你决定何时跑）

```powershell
cd probe
.\bin\Debug\net8.0-windows\ProbeHost.exe --interactive
```

运行后立即开始执行 P1–P8，没有二次确认，窗口会动、会闪、会抢前台。全程约 1–2 分钟。
结束时写 `out\probe-results.json` 和 `out\probe-console.txt`，然后关闭测试窗口。

> 因为没有任何确认步骤，请自己挑一个不忙的时机运行；运行期间不要操作它创建的靶窗口。

### P1 前台激活（4 种做法 × 20 次）

- **怎么做**：每次都由新启动的子进程去激活一个靶窗口，避免父进程已经拿到前台权限造成假阳性。
- **看什么**：`ok` 次数（调用后 250 ms 内 `GetForegroundWindow()` 等于目标）、`apiTrue`（API 返回值）、`baselineFail`（前台没变）。
- **通过标准**：至少一种做法 ≥ 18/20 成功，且失败的那 2 次可以解释（例如前台锁超时）。
- **注意**：提权靶窗口本程序不构造（需要 UAC 交互）。若你手动开一个管理员记事本并把它作为靶子，需要另加参数；默认只记录“当前桌面有 N 个提权窗口”。

### P2 命中判定（矩形差）

- **看什么**：每个靶窗口的 `L/T/R/B` 差值（`GetWindowRect - EXTENDED_FRAME_BOUNDS`），以及最大化状态下的值。
- **通过标准**：差值绝对值 ≤ 10 px；同一类窗口的差值稳定（不随位置变化）。

### P3 混合 DPI

- **看什么**：每块屏的 `GetDpiForWindow` 读数、`PhysicalToLogicalPointForPerMonitorDPI` 换算结果、无感知子进程读到的矩形。
- **通过标准**：跨屏移动再读回，四边误差 ≤ 1 px；无感知子进程的坐标与物理像素有一致的比例偏差（证明必须按物理像素建模）。
- **条件**：需要两块缩放不同的屏幕，或允许临时改缩放。只有单屏且不能改缩放时，本项只能验证公式，不能验证跨屏。

### P4 窗口枚举与过滤

- **看什么**：`总 N / 可用 M`，各过滤项的计数；`ApplicationFrameWindow` 的识别结果；最小化窗口 `SW_RESTORE` 后能否激活。
- **通过标准**：可用数与肉眼可见的任务栏/Alt+Tab 窗口数相差 ≤ 2；UWP 应用能正确识别；最小化窗口恢复后能到前台。

### P5 原位还原

- **看什么**：5 个窗口「记录 → 位移+缩放 → 还原」的四边最大误差；最小化/最大化还原；有最小尺寸约束的窗口的表现。
- **通过标准**：普通窗口误差 ≤ 1 px；最大化窗口用 `WINDOWPLACEMENT.rcNormalPosition` 还原后误差 ≤ 1 px；有约束的窗口记录被 clamp 后的实际值（不算失败）。

### P6 拖入（OLE `IDropTarget`）

- **看什么**：`RegisterDragDrop` 返回值、收到的路径数、`.lnk` 解析出的目标 exe、图标是否取到、拖拽前后前台窗口是否变化。
- **通过标准**：`register=0`、`count≥1`、`.lnk` 能解析出目标路径、`focus_unchanged=True`。
- **说明**：探针用构造的 `CF_HDROP` 数据直接调用 `Drop`，避免真的操控鼠标。若要看真实拖拽，可手动从资源管理器拖 `.lnk` 到探针提示的窗口上。

### P7 覆盖层框选

- **看什么**：覆盖层出现时前台是否不变、`WindowFromPoint` 是否命中覆盖层、加 `WS_EX_TRANSPARENT` 后是否穿透、矩形命中列表。
- **通过标准**：`fg_unchanged=True`、`clickthrough=True`、命中列表与目视摆放一致（允许差 1 个）。
- **注意**：会短暂全屏显示一个半透明层，约 1 秒后自动关闭。框选矩形由代码注入，不做真实鼠标拖拽。

### P8 全局热键

- **看什么**：3 个组合的注册返回值与 `GetLastWin32Error`；子进程抢同一组合是否返回 1409；注销后能否重新注册。
- **通过标准**：3 个组合都注册成功；冲突子进程返回 1409（而不是静默失败）；注销后重注册成功。
- **注意**：只注册探针自己的组合，并在结束时全部 `UnregisterHotKey`。不会改动 PowerToys 的配置。

## 4. 失败时的恢复

- 靶窗口没关掉：直接手动关闭那几个记事本/画图/WPF 窗口即可，探针不写注册表、不装服务。
- 热键残留：探针退出时注销；若异常退出，重启该进程或注销登录即可释放。
- 覆盖层残留：覆盖层是探针自己的窗口，随进程退出消失；必要时在任务管理器结束 `ProbeHost.exe`。
