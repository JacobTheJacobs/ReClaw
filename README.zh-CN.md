<div align="center">

<br/>

# ReClaw

### 网关挂了？配置损坏？备份丢失？<br/>一键修复你的 [OpenClaw](https://github.com/nicepkg/openclaw)。

<br/>

<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/下载-Windows_x64-0078D4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows" /></a>&nbsp;&nbsp;
<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/下载-macOS_arm64-000000?style=for-the-badge&logo=apple&logoColor=white" alt="macOS" /></a>&nbsp;&nbsp;
<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><img src="https://img.shields.io/badge/下载-Linux_x64-FCC624?style=for-the-badge&logo=linux&logoColor=black" alt="Linux" /></a>

<br/><br/>

<a href="README.md">English</a> | <strong>简体中文</strong>

<br/><br/>

<img width="2740" height="1568" alt="ReClaw" src="https://github.com/user-attachments/assets/6898ee02-5a5c-4e5c-bd15-cb4a6543ddbc" />

<br/>

</div>

---

<br/>

<div align="center">

### 问题

OpenClaw 总会出问题。网关掉线、配置损坏、备份靠手动、恢复靠猜。浪费几个小时。

### 解决方案

ReClaw。一个工具，**91 个操作**。备份、恢复、修复、监控、定时任务。<br/>
桌面 GUI 或命令行，全平台支持。把 Agent Prompt 粘贴到任何 AI 编程助手，<br/>
它就能自动管理你的 OpenClaw。

</div>

<br/>

---

<br/>

<div align="center">
<table>
<tr>
<td width="50%">

<img width="559" alt="主界面" src="https://github.com/user-attachments/assets/665a12c4-bad7-4b31-bd88-cb2ad2d6dcac" />

</td>
<td width="50%">

<img width="552" alt="操作列表" src="https://github.com/user-attachments/assets/03846f2f-769c-469d-8fe6-b751c5cd0d19" />

</td>
</tr>
</table>
</div>

<br/>

---

## 功能一览

| 问题 | ReClaw 修复 | 命令 |
|------|-----------|------|
| 网关离线 | 9 步自动升级修复 | `gateway repair` |
| 配置损坏 | 从最近备份恢复 | `recover` |
| 需要备份 | 加密快照，一条命令 | `create -p <密码>` |
| 不确定哪里坏了 | 健康检查 + 自动修复 | `doctor --repair` |
| 完全崩溃 | 全套恢复流程 | `recover` |
| 定时备份 | 每天、每周或 cron | `backup schedule create` |
| 需要回滚 | 恢复任意快照 | `rollback -s <路径>` |
| 重新安装 | 从零重建 | `openclaw rebuild` |

---

## 快速开始

从 [Releases](https://github.com/JacobTheJacobs/ReClaw/releases) 下载运行。或从源码构建：

```bash
git clone https://github.com/JacobTheJacobs/ReClaw.git && cd ReClaw
dotnet build ReClaw.slnx -c Release
dotnet run --project src/ReClaw.Desktop/ReClaw.Desktop.csproj
```

需要 [.NET 10+](https://dotnet.microsoft.com/download)。

---

## AI Agent 提示词

> 粘贴到**任何 AI 编程助手** — Claude Code、Copilot、Cursor、Codex、Windsurf — 即可自动管理 OpenClaw。91 个操作，已对照源码验证。

```bash
# ReClaw — OpenClaw 管理
# 粘贴到任何 AI Agent。需要 .NET 10+ 和 Git。

# 安装
git clone https://github.com/JacobTheJacobs/ReClaw.git /tmp/reclaw
cd /tmp/reclaw && dotnet build ReClaw.slnx -c Release
CLI="dotnet run --project /tmp/reclaw/src/ReClaw.Cli/ReClaw.Cli.csproj --"

# 重要：子命令用空格分隔，不要用连字符
$CLI gateway status       # 不是: gateway-status
$CLI backup create        # 不是: backup-create
$CLI create               # 根命令别名
$CLI restore -s <路径>    # 不是: restore-archive

# 安全规则：修复前后必须 gateway status，破坏性操作前必须 create 备份
# 升级顺序：doctor → fix → gateway repair → recover → reset

# 决策树
# 1. OpenClaw 已安装？→ 否 → $CLI openclaw rebuild --confirm-destructive
# 2. $CLI gateway status → "Runtime: running" + "RPC probe: ok" = 健康
# 3. 异常 → $CLI create → $CLI gateway repair → $CLI gateway status
# 4. 配置损坏 → $CLI create → $CLI recover → $CLI gateway status

# 核心命令
$CLI create [-p 密码]                   # 备份（可选加密）
$CLI restore -s <路径> [--preview]      # 从备份恢复
$CLI doctor [--repair] [--deep]         # 健康检查
$CLI fix [--force]                      # 深度修复 + 快照保护
$CLI recover                            # 升级式恢复流程
$CLI gateway status                     # 检查健康（必须先运行）
$CLI gateway start                      # 启动网关
$CLI gateway stop                       # 停止网关
$CLI gateway repair                     # 9 步修复梯
$CLI gateway logs                       # 查看日志（Ctrl+C 停止）
$CLI dashboard                          # 打开 Web 仪表板
$CLI openclaw rebuild                   # 重建 OpenClaw（破坏性）
$CLI reset --preview / --confirm        # 重置本地数据（破坏性）
$CLI diagnostics export                 # 导出调试包
$CLI backup list                        # 列出备份
$CLI backup prune [--apply]             # 清理旧备份
$CLI backup schedule create             # 定时备份（每天/每周/cron）
$CLI rollback -s <路径>                 # 从快照回滚
$CLI gateway token show [--reveal]      # 显示网关令牌
$CLI gateway token generate             # 生成新令牌
$CLI action-list                        # 列出全部 91 个操作
$CLI action-schema --id <操作ID>        # 导出操作 JSON Schema

# 输出：gateway status → "Runtime: running" + "RPC probe: ok" = 健康
# 重置模式：preserve-backups（默认）、preserve-config、preserve-cli、full
```

<details>
<summary><strong>完整命令参考（91 个操作）</strong></summary>
<br/>

```bash
# 根命令
create, verify, restore, doctor, fix, recover, rollback,
reset, status, dashboard, cleanup-related, version,
diagnostics export, action-list, action-schema

# 备份
backup create/verify/list/restore/diff/export/prune
backup schedule create/list/remove

# 网关
gateway status/start/stop/repair/logs/url/browser-diagnostics
gateway token show/generate

# OpenClaw
openclaw rebuild, openclaw cleanup-related

# 关键选项
--preview      预览，不执行（restore, reset, rollback, prune）
--confirm      确认重置
--confirm-destructive  确认重建
--confirm-rollback     确认回滚
--json         JSON 输出
-p <密码>      加密/解密备份
--repair       Doctor 带修复
--force        强制修复
--deep         深度健康检查
--reset-mode   preserve-backups|preserve-config|preserve-cli|full
--scope        config|config+creds+sessions|full
--keep-last N  清理：保留最近 N 个（默认：5）
--older-than   清理：超过时间阈值（默认：30d）
--kind         定时：daily|weekly|monthly|cron
```

</details>

---

## 工作原理

```
你                             ReClaw                         OpenClaw
 │                               │                              │
 ├─ "网关挂了" ─────────────────►│                              │
 │                               ├─ create（备份）─────────────►│
 │                               ├─ gateway repair ────────────►│
 │                               │  ├─ 模式修复                 │
 │                               │  ├─ doctor 检查              │
 │                               │  ├─ 会话清理                 │
 │                               │  ├─ 重装服务                 │
 │                               │  ├─ 启动 + 轮询              │
 │                               │  ├─ 稳定性检查               │
 │                               │  ├─ 锁文件清理               │
 │                               │  ├─ 进程清理                 │
 │                               │  └─ 后台启动兜底             │
 │                               ├─ gateway status ────────────►│
 │◄─ "已修复，网关正常" ─────────┤                              │
```

---

<br/>

<div align="center">

<a href="https://github.com/JacobTheJacobs/ReClaw/releases"><strong>下载</strong></a>&nbsp;&nbsp;&nbsp;&middot;&nbsp;&nbsp;&nbsp;<a href="https://github.com/JacobTheJacobs/ReClaw/issues"><strong>问题反馈</strong></a>&nbsp;&nbsp;&nbsp;&middot;&nbsp;&nbsp;&nbsp;<a href="https://github.com/nicepkg/openclaw"><strong>OpenClaw</strong></a>

<br/>

MIT &copy; [JacobTheJacobs](https://github.com/JacobTheJacobs)

<br/>

</div>
