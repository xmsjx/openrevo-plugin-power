# 自定义功耗插件· OpenRevo 插件

给 [OpenRevo](https://github.com/faintonce/open-revo) 增加一个标签页：AMD Curve Optimizer（全核 / 每核负压）、FMax（PBO 上限）、温度墙。

> 本仓库是 **OpenRevo 的第三方插件（非官方）**，与任何硬件厂商无隶属、无授权关系。

## 特性

- **只读探测**：CPU 名称 / 代号 / 家族 / SMU 版本 / PawnIO 状态 / FMax 当前值
- **Curve Optimizer**：全核 margin、每核 margin（`core=margin`）
- **FMax**：写入后自动回读对比
- **温度墙**：目标结温
- **安全**：只写填了值的项；写后回读；不做持久化、不自动应用

## 安装

1. 复制整个目录到 `%APPDATA%\OpenRevo\plugins\openrevo-plugin-ux\`
2. OpenRevo →【插件】→【刷新】
3. 顶栏出现「自定义功耗插件」

## 首次使用

网页无法自行提权，需手动启动一次代理：

1. 双击 `启动代理.bat` → UAC 点【是】
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
| GET | `/probe` | CPU/代号/家族/SMU/PawnIO/FMax 当前值/范围 |
| POST | `/apply` | `{coAll?, coPerCore?:{core:margin}, fmaxMhz?, thmCelsius?}`，只写出现的项 |

## 前置条件

- **PawnIO 驱动**（LM 安装目录自带）
- 代理需**管理员**运行（SMU 访问）

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