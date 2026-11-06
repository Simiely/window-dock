# window-dock · Windows 窗口工作台

把任意程序的窗口收进一条常驻侧栏，支持单个 / 成组快速调取；进入抓取模式后框选若干窗口打成组，之后一键释放回原位。

> 与 macOS Dock 克隆的差别：Dock 克隆只负责「启动应用」。本项目管的是**已经在运行的窗口**——要能识别、能记住位置、能整组还原。

## 当前阶段

| 阶段 | 状态 | 交付 |
|---|---|---|
| 调研与方案 | ✅ 完成 | [docs/调研与方案.md](./docs/调研与方案.md) |
| 只读探针（枚举 / 视觉边界 / DPI / 权限 / 热键占用） | ✅ 已完成，真机实测 | [docs/探针结果与实施计划.md](./docs/探针结果与实施计划.md) |
| 交互探针（前台激活 / 覆盖层 / 还原 / 拖入 / 热键 / 跨 DPI） | ⏸ 工程就位，**尚未运行** | [probe/README.md](./probe/README.md) |
| 主体实现 | 待交互探针结论后开工 | P0 模块与验收点见结果文档第 6 节 |

## 文档索引

| 文档 | 内容 |
|---|---|
| [docs/调研与方案.md](./docs/调研与方案.md) | 需求拆解 R1–R8、同类方案对比（FancyZones / Groupy / WindowTabs / AquaSnap 等）、逐项机制可行性、技术选型、三阶段计划、风险 |
| [docs/探针结果与实施计划.md](./docs/探针结果与实施计划.md) | 本机只读实测数据、**被实测推翻的 4 条假设**、待实测清单、P0/P1 模块与验收点 |
| [docs/工作台卡片.md](./docs/工作台卡片.md) | 供「Agent 工作台」批量导入的任务卡与同步卡（内部流转用） |
| [probe/README.md](./probe/README.md) | 探针怎么跑、每项看什么数据、通过标准、失败恢复 |
| [DEVELOPMENT.md](./DEVELOPMENT.md) | 项目概览、架构说明、关键问题与方案（一坑一篇） |
| [AGENTS.md](./AGENTS.md) | 给 AI / 未来的你：技术栈、关键坑、约定、常用命令 |
| [CHANGELOG.md](./CHANGELOG.md) | 版本记录 |

## 目录结构

```
window-dock/
├── README.md            # 本文件：门面 + 索引
├── AGENTS.md            # 项目规则（含文档基线）
├── DEVELOPMENT.md       # 架构 + 关键问题与方案
├── CHANGELOG.md         # 版本记录
├── .gitignore
├── docs/                # 长文（调研、探针结果）
└── probe/               # 探针工程源码（不含 bin/obj/out）
    ├── ProbeHost（net8.0-windows + WPF）
    ├── dpi-child/       # 无 DPI 感知的控制台子进程
    └── README.md
```

`docs/` 与 `probe/` 是按内容量自然长出来的，不是开局预设计。

## 快速开始（只有探针，主体程序尚未开始）

```powershell
cd probe
dotnet build Probe.csproj

# 只读：不动任何窗口，随时可跑
.\bin\Debug\net8.0-windows\ProbeHost.exe --readonly

# 交互：会自建靶窗口、会抢前台，请自己挑时机
.\bin\Debug\net8.0-windows\ProbeHost.exe --interactive
```

无参数运行时只打印用法，**不会自动跑任何探针**。安全边界与每项通过标准见 [probe/README.md](./probe/README.md)。

## 设计要点（基于实测，不是文档推断）

- **不把「停靠」实现成跨进程 `SetParent`**：Dock 只是控制器，摆位与记账由自己负责，不改目标窗口样式。
- **枚举必须重过滤**：本机 `EnumWindows` 483 个 → 按 Alt+Tab 规则过滤后 17 个。
- **命中判定用 `DWMWA_EXTENDED_FRAME_BOUNDS`**：`GetWindowRect` 比视觉边界最多外扩 8 px，且同类窗口中存在差值为 0 的情况，不能用固定补偿。
- **位置快照用 `WINDOWPLACEMENT`**：最小化窗口的 `GetWindowRect` 实测为 `-32000` 哨兵，必须读 `rcNormalPosition`。
- **默认不提权**：提权后 OLE 拖放会被 UIPI 拦；提权目标也只能提示、不能接管。

## 与个人经验库的关系

文档按 [knowledge-base 的「单项目规范」](https://github.com/Simiely/knowledge-base/blob/main/%E6%A8%A1%E6%9D%BF%E5%BA%93/%E5%8D%95%E9%A1%B9%E7%9B%AE%E8%A7%84%E8%8C%83/README.md) 组织：根目录四件套（README / AGENTS / DEVELOPMENT / CHANGELOG）+ 超阈值后生长出的 `docs/`。
踩坑记录留在 [DEVELOPMENT.md](./DEVELOPMENT.md)，跨项目通用的部分再提炼进 knowledge-base。

## 边界

- 不改系统设置、不安装服务、不注册开机项（探针同样不写注册表）。
- 不模拟鼠标拖动、不修改目标窗口的 `WS_EX_*` 状态。
- 公开仓库：不提交任何 token、凭据、本地绝对路径或二进制构建产物。
