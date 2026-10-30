# 自定义功耗插件· OpenRevo 插件

给 [OpenRevo](https://github.com/faintonce/open-revo) 增加一个标签页：CPU 功耗墙、温度墙、AMD Curve Optimizer（全核 / 每核负压）、FMax（PBO 上限）。

> 本仓库是 **OpenRevo 的第三方插件（非官方）**，与任何硬件厂商无隶属、无授权关系。
>
> **宿主要求：OpenRevo 0.9.4 或更新版本。** 插件页通过宿主注入的 `window.OpenRevo`
> 桥（`invoke` / `listen`）与宿主通信，0.9.3 及更早版本没有这个桥，不兼容。

## 特性

- **只读探测**：CPU 名称 / 代号 / 家族 / SMU 版本 / PawnIO 状态 / 物理核数 / FMax 当前值 /
  功耗墙当前值（STAPM、快速、慢速、时间窗、结温）
- **功耗墙**：没有勾选框——输入框默认填入回读值，**只下发与回读不同的项**，没改动就不写
- **Curve Optimizer（全核）**：滑块与输入框双向同步，范围 -50 ~ +25
- **Curve Optimizer（每核）**：按物理核数逐核生成输入框并按 CCD 排布（8 核一排 / 12→6+6 / 16→8+8）；
  框内默认显示**逐核回读值**；只有改动过的核心才写入
- **全核 ↔ 每核联动**：拖动全核滑块会同步填充所有每核输入框（灰色标记），应用时只下发 `coAll`，
  不重复写每核；手动改某一核则该核脱离联动、单独下发
- **FMax**：框内默认显示回读的当前上限，改动才写入，写完自动回读对比
- **宿主模式跟随**：订阅宿主 `physical-mode-switched` / `hardware-state-updated`，
  宿主处于「自定义」模式时自动锁定 PL 项，避免与 OpenRevo 本体互相覆盖
- **安全**：只写改动过的项；写后回读；不做持久化、不自动应用

## 安装

1. 确认宿主是 **OpenRevo 0.9.4+**（0.9.3 及更早没有 `window.OpenRevo` 桥，插件会提示不兼容）
2. 复制整个目录到 `%APPDATA%\OpenRevo\plugins\openrevo-plugin-ux\`
3. OpenRevo →【插件】→【刷新】
4. 顶栏出现「自定义功耗插件」（`manifest` 的 `order: 66` 会让它排在「插件」与「支持作者」之间）

## 启动本地代理

代理是**本地提权进程**（读 SMU/MSR 需要管理员）。两种方式任选：

**A. 宿主代拉（推荐，OpenRevo 0.9.4+）**

manifest 已声明 `service`，宿主会：

- 在标签页顶栏生成「服务」指示灯与【启动/停止】按钮；
- 以管理员权限静默拉起 `agent/bin/ux-agent.exe`（无 UAC、无黑框，因为 OpenRevo 本身以管理员运行）；
- 打开标签页时按 `auto_start` 自动启动；
- 宿主退出时按 `auto_stop` 回收进程树。

插件页「诊断」区也有【启动服务（宿主代拉）】【停止服务】按钮，走 `window.OpenRevo.invoke`
（`start_plugin_service` / `stop_plugin_service`）。

**B. 手动启动（宿主未拉起或调试时）**

1. 双击 `启动代理.bat`（或 `Start-Agent.bat`）→ UAC 点【是】
2. 回到插件页点【刷新状态】

代理监听 `127.0.0.1:18946`，仅本机可访问。

## 架构

```
index.html                   标签页（单文件，fetch 调用代理）
启动代理.bat                  以管理员启动代理
agent/bin/ux-agent.exe       本地代理（.NET 自包含，HTTP/JSON）
agent/src/                   SMU/PawnIO 源码（复用 LM 已实现层）
```

### HTTP 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/ping` | 存活探测 |
| GET | `/probe` | CPU/代号/家族/SMU/PawnIO/物理核数/FMax 当前值/功耗墙当前值/**逐核 CO 回读**（`coPerCore`）/范围 |
| POST | `/apply` | `{coAll?, coPerCore?:{core:margin}, fmaxMhz?, thmCelsius?, tctlCelsius?, stapmW?, fastW?, slowW?, stapmTimeSec?, slowTimeSec?}`，只写出现的项 |

`/probe` 关键字段：

```json
{
  "ok": true, "cpu": "AMD Ryzen 7 8745H...", "codeName": "Phoenix", "family": "Mobile",
  "smuVersion": "0x4C6500", "initialized": true, "cores": 8,
  "current": {
    "fmaxMhz": 4950,
    "power": { "stapmW": 34, "fastW": 100, "slowW": 65, "tctlCelsius": 95,
               "stapmTimeSec": 41, "slowTimeSec": 47, "tableVersion": "0x4C0009" },
    "coPerCore": [0, 0, 0, 0, 0, 0, 0, 0],
    "coReadbackNote": null
  }
}
```

`coPerCore` 为按核顺序的 CO 步数（通过各代号的 CO 回读命令逐核读取）；平台没有回读命令时
为 `null` 且 `coReadbackNote` 说明原因（不会假装读到值）。

## 前置条件

- **PawnIO 驱动**（LM 安装目录自带）
- 代理需**管理员**运行（SMU 访问）

## 更新日志

### 0.5.3
- **每核负压**：按物理核数逐核生成输入框，按 CCD 排布（8 核一排；12 核 6+6；16 核 8+8）
- **全核 ↔ 每核联动**：拖动全核滑块同步填充所有每核框（灰色标记），应用时只下发 `coAll`；
  手动改某核则该核脱离联动单独下发
- **功耗墙去掉勾选框**：输入框预填回读值，只下发与回读不同的项
- **FMax** 框内预填回读上限，改动才写
- CO 范围调整为 **-50 ~ +25**；全核 / 每核 / 功耗墙 / FMax 均有回读基线
- 代理 `/probe` 新增 `cores` 与逐核 `coPerCore` 回读（按代号选 CO 读命令）

### 0.4.0
- 改用 OpenRevo **0.9.4+** 的 `window.OpenRevo` 插件桥（`invoke` / `listen`），不再兼容 0.9.3
- 订阅宿主事件：`hardware-state-updated`、`physical-mode-switched`、`gpu-clock-offsets-changed`
- 宿主「自定义」模式下自动锁定 PL 项，避免与 OpenRevo 本体互相覆盖
- manifest 增加 `min_host_version: 0.9.4`、`order: 66`（排在「插件」与「支持作者」之间）

### 0.2.0
- 声明宿主伴随服务（`service`），由宿主以管理员权限自动拉起本地代理

## 许可

[GPL-3.0](LICENSE)。

本插件的 SMU 命令表与邮箱地址表，主要参考自 **UXTU（Universal x86 Tuning Utility，
JamesCJ60/Universal-x86-Tuning-Utility，GPL-3.0）** 的 `RyzenSmu.cs`：各插槽的 MP1/PSMU
邮箱地址组、逻辑命令到命令 ID 的映射、以及"同一逻辑命令向两个邮箱各下发一次"的做法。

交叉印证另参考 irusanov/ZenStates-Core（GPL-3.0）、irusanov/SMUDebugTool（GPL-3.0）、
FlyGoat/RyzenAdj（LGPL-3.0）。本项目不含上述项目的源码副本，仅按命令语义重写，
因此整体按 GPL-3.0 发布。
## 附：读取 Intel PL1 / PL2 / PL4

插件根目录带一个**只读**独立脚本 `Get-IntelPl.ps1`，用于在 Intel 平台上读取 CPU 的
PL1 / PL2 / PL4（MSR `0x610`，Intel PKG_POWER_LIMIT）：

```powershell
# 管理员 PowerShell
cd "$env:APPDATA\OpenRevo\plugins\openrevo-plugin-ux"
powershell -NoProfile -ExecutionPolicy Bypass -File .\Get-IntelPl.ps1
```

- 依赖：PawnIO 驱动 + 管理员权限；`IntelMSR.bin` 已随插件携带
- 只读：不写任何 MSR
- 参数：`-ModulePath <path>`、`-Msr 0x610`、`-Raw`
## 从源码构建（本仓库不含 exe）

本仓库只发布**源码**。首次使用需自行构建一次代理程序：

```powershell
# 需要 .NET 10 SDK
cd agent
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o bin
```

生成的 `agent\bin\ux-agent.exe` 就是本地代理（约 36 MB，自包含，目标机无需装 .NET）。

> 也可以直接用 `dotnet build` 做开发调试，输出在 `bin\Release\net10.0-windows\`。

构建完成后，安装步骤与下方「安装」一节相同。