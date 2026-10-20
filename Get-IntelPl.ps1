# =============================================================================
#  Get-IntelPl.ps1 — 只读读取 Intel CPU 的 PL1 / PL2 / PL4（MSR 0x610）
#
#  纯 PowerShell + P/Invoke，直接和 PawnIO 驱动通信：
#    1) 打开 \\.\GLOBALROOT\Device\PawnIO
#    2) Load 内嵌的 IntelMSR 模块（PawnIO.Modules，LGPL-2.1-or-later）
#    3) Execute("ioctl_read_msr", [0x610], out) 读 PKG_POWER_LIMIT
#    4) 按 Intel SDM 解析：
#         bits  0..14  PL1 (1/8 W)      bit 15  PL1 使能
#         bits 16..30  PL2 (1/8 W)      bit 31  PL2 使能
#         bits 32..46  PL4 (1/8 W)      bit 47  PL4 使能
#         bits 48..55  PL4 时间窗（秒）
#
#  ⚠️ 只读：本脚本**不写任何 MSR**。
#  ⚠️ 需要：管理员权限（PawnIO 设备）+ PawnIO 驱动 + IntelMSR 模块。
#
#  用法：
#    powershell -NoProfile -ExecutionPolicy Bypass -File Get-IntelPl.ps1
#    ... -ModulePath "C:\path\IntelMSR.bin"
#    ... -Msr 0x610 -Raw
# =============================================================================
param(
    [string]$ModulePath = '',
    [uint32]$Msr = 0x610,
    [switch]$Raw
)

$ErrorActionPreference = 'Stop'

function Write-Head([string]$t) { Write-Host ''; Write-Host "=== $t ===" -ForegroundColor Cyan }
function Write-OK([string]$t)   { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Write-Warn2([string]$t){ Write-Host "  [WARN] $t" -ForegroundColor Yellow }
function Write-Err2([string]$t) { Write-Host "  [ERR]  $t" -ForegroundColor Red }

# ---------------------------------------------------------------- P/Invoke
$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class PawnIOBridge
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle dev, uint code, byte[] inBuf, uint inSize, byte[] outBuf, uint outSize, out uint ret, IntPtr ov);

    const uint CtlLoad    = 2712805508u;
    const uint CtlExecute = 2712805636u;

    public static SafeFileHandle Open()
    {
        var h = CreateFile("\\\\?\\GLOBALROOT\\Device\\PawnIO", 3221225472u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (h == null || h.IsInvalid) throw new InvalidOperationException("CreateFile(PawnIO) failed, win32=" + Marshal.GetLastWin32Error());
        return h;
    }

    public static void Load(SafeFileHandle h, byte[] module)
    {
        uint ret;
        if (!DeviceIoControl(h, CtlLoad, module, (uint)module.Length, null, 0u, out ret, IntPtr.Zero))
            throw new InvalidOperationException("LoadModule failed, win32=" + Marshal.GetLastWin32Error());
    }

    public static bool Execute(SafeFileHandle h, string fn, ulong[] input, out ulong[] output)
    {
        output = null;
        byte[] name = Encoding.ASCII.GetBytes(fn);
        int inN = input == null ? 0 : input.Length;
        byte[] buf = new byte[32 + inN * 8];
        Buffer.BlockCopy(name, 0, buf, 0, Math.Min(name.Length, 31));
        for (int i = 0; i < inN; i++)
            BitConverter.GetBytes(input[i]).CopyTo(buf, 32 + i * 8);

        uint ret;
        bool ok = DeviceIoControl(h, CtlExecute, buf, (uint)buf.Length, null, 0u, out ret, IntPtr.Zero);
        if (!ok) return false;
        output = new ulong[0];
        return true;
    }

    // 带输出的版本
    public static bool ExecuteOut(SafeFileHandle h, string fn, ulong[] input, int outCount, out ulong[] output)
    {
        byte[] name = Encoding.ASCII.GetBytes(fn);
        int inN = input == null ? 0 : input.Length;
        byte[] buf = new byte[32 + inN * 8];
        Buffer.BlockCopy(name, 0, buf, 0, Math.Min(name.Length, 31));
        for (int i = 0; i < inN; i++)
            BitConverter.GetBytes(input[i]).CopyTo(buf, 32 + i * 8);

        byte[] outBuf = new byte[outCount * 8];
        uint ret;
        if (!DeviceIoControl(h, CtlExecute, buf, (uint)buf.Length, outBuf, (uint)outBuf.Length, out ret, IntPtr.Zero))
        { output = null; return false; }
        if (ret != (uint)outBuf.Length) { output = null; return false; }

        output = new ulong[outCount];
        for (int i = 0; i < outCount; i++) output[i] = BitConverter.ToUInt64(outBuf, i * 8);
        return true;
    }
}
'@

Write-Head '环境检查'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "  管理员: $isAdmin"
$svc = Get-Service PawnIO -ErrorAction SilentlyContinue
Write-Host "  PawnIO 服务: $(if($svc){$svc.Status}else{'未安装'})"
if (-not $isAdmin) { Write-Err2 '需要管理员权限：请右键“以管理员身份运行” PowerShell 后再执行'; exit 5 }
if (-not $svc -or $svc.Status -ne 'Running') { Write-Err2 'PawnIO 服务未运行：请先安装/启动 PawnIO'; exit 6 }

# ---------------------------------------------------------------- 找 IntelMSR 模块
Write-Head '定位 IntelMSR 模块'
$candidates = @()
if ($ModulePath) { $candidates += $ModulePath }
$candidates += @(
    (Join-Path $PSScriptRoot 'IntelMSR.bin')
    (Join-Path $PSScriptRoot 'agent\IntelMSR.bin')
    (Join-Path $PSScriptRoot '..\IntelMSR.bin')
)
$module = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $module) {
    Write-Err2 '找不到 IntelMSR 模块（IntelMSR.bin）。用 -ModulePath 指定路径。'
    $candidates | ForEach-Object { Write-Host "      尝试过: $_" }
    exit 7
}
Write-OK ("模块: " + $module + "  (" + (Get-Item $module).Length + " B)")

# ---------------------------------------------------------------- 打开设备
Write-Head '连接 PawnIO'
try {
    Add-Type -TypeDefinition $src -Language CSharp -ErrorAction Stop | Out-Null
} catch {
    if ($_.Exception.Message -notmatch 'already exists') { Write-Err2 ('编译 P/Invoke 失败: ' + $_.Exception.Message); exit 8 }
}
try {
    $h = [PawnIOBridge]::Open()
    Write-OK 'CreateFile(PawnIO) 成功'
    [PawnIOBridge]::Load($h, [IO.File]::ReadAllBytes($module))
    Write-OK 'IntelMSR 模块已加载'
} catch {
    Write-Err2 ('PawnIO 通信失败: ' + $_.Exception.Message)
    Write-Warn2 '常见原因：未以管理员运行 / 驱动未安装 / 模块被拒绝加载'
    exit 9
}

# ---------------------------------------------------------------- 读 MSR
Write-Head ("读取 MSR 0x{0:X}" -f $Msr)
$out = $null
$ok = [PawnIOBridge]::ExecuteOut($h, 'ioctl_read_msr', [uint64[]]@([uint64]$Msr), 1, [ref]$out)
if (-not $ok -or -not $out -or $out.Count -lt 1) {
    Write-Err2 ("ioctl_read_msr 失败（该模块可能不允许读 0x{0:X}）" -f $Msr)
    exit 10
}
$v = [uint64]$out[0]
Write-OK ("原始值 0x{0:X16}" -f $v)
if ($Raw) { exit 0 }

# ---------------------------------------------------------------- 解析 Intel PKG_POWER_LIMIT
function Decode-Pl([uint64]$val) {
    $pl1raw = $val -band 0x7FFF
    $pl1en  = ($val -shr 15) -band 1
    $pl2raw = ($val -shr 16) -band 0x7FFF
    $pl2en  = ($val -shr 31) -band 1
    $pl4raw = ($val -shr 32) -band 0x7FFF
    $pl4en  = ($val -shr 47) -band 1
    $pl4win = ($val -shr 48) -band 0xFF
    [pscustomobject]@{
        PL1_W   = [math]::Round($pl1raw / 8.0, 1)
        PL1_Enabled = [bool]$pl1en
        PL2_W   = [math]::Round($pl2raw / 8.0, 1)
        PL2_Enabled = [bool]$pl2en
        PL4_W   = [math]::Round($pl4raw / 8.0, 1)
        PL4_Enabled = [bool]$pl4en
        PL4_TimeWindow_s = [int]$pl4win
    }
}

Write-Head 'Intel PL1 / PL2 / PL4（MSR 0x610）'
$r = Decode-Pl $v
$r | Format-List | Out-String | ForEach-Object { $_.TrimEnd() } | ForEach-Object { Write-Host "  $_" }

Write-Head '按 MSR 位域对照'
Write-Host ("  bits  0..14  PL1 = {0} W   (使能={1})" -f $r.PL1_W, $r.PL1_Enabled)
Write-Host ("  bits 16..30  PL2 = {0} W   (使能={1})" -f $r.PL2_W, $r.PL2_Enabled)
Write-Host ("  bits 32..46  PL4 = {0} W   (使能={1})" -f $r.PL4_W, $r.PL4_Enabled)
Write-Host ("  bits 48..55  PL4 时间窗 = {0} s" -f $r.PL4_TimeWindow_s)

Write-Host ''
Write-Host '  说明：这三项是 CPU 封装的功耗限制（Intel 术语）。' -ForegroundColor DarkGray
Write-Host '        PL1 = 长时持续，PL2 = 短时爆发，PL4 = 瞬时峰值。' -ForegroundColor DarkGray
Write-Host '        本脚本只读，不修改任何 MSR。' -ForegroundColor DarkGray
Write-Host ''

try { $h.Close() } catch { }
exit 0