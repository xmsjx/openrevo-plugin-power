// ============================================================================
//  IntelPath — Intel 平台的 OC 邮箱通道（MSR 0x150）
//
//  与 AMD 的 RyzenSmuService 完全独立：Intel 分支使用单独的 PawnIO 句柄，
//  并加载 IntelMSR 模块（PawnIO.Modules，LGPL-2.1-or-later，原样内嵌）。
//
//  能力（与 LM 的实现一致）：
//    domain 0 = core  : 电压偏移（-150..0 mV）+ 倍频（非混合架构）
//    domain 2 = cache : 电压偏移
//  每次写入都会回读校验。
// ============================================================================
using System;
using System.IO;
using System.Reflection;
using PawnIO;

internal static class IntelPath
{
    private static readonly object Gate = new();
    private static PawnIOWrapper? _io;
    private static IntelOcMailbox? _mailbox;
    private static string? _lastError;

    public static string? LastError => _lastError;

    private static bool Ensure()
    {
        lock (Gate)
        {
            if (_mailbox != null) return true;
            try
            {
                _io = new PawnIOWrapper();
                if (_io.Connect() != PawnIOWrapper.ConnectResult.OK)
                {
                    _lastError = "PawnIO 驱动未就绪（Intel 分支需要 PawnIO 服务）";
                    return false;
                }

                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("OpenRevoUx.IntelMSR.bin");
                if (stream == null)
                {
                    _lastError = "内嵌的 IntelMSR.bin 缺失";
                    return false;
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (!_io.LoadModule(ms.ToArray()))
                {
                    _lastError = "IntelMSR 模块被拒绝加载（驱动/权限问题）";
                    return false;
                }

                _mailbox = new IntelOcMailbox(_io.Execute);
                return true;
            }
            catch (Exception ex)
            {
                _lastError = "Intel 通道初始化异常: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>只读探测 domain 0 / 2。</summary>
    public static object Probe()
    {
        lock (Gate)
        {
            if (!Ensure())
                return new { ok = false, vendor = "intel", reason = _lastError };

            object Dom(int id)
            {
                var d = _mailbox!.Probe(id);
                return new
                {
                    domain = d.Id,
                    offsetMv = (double)d.OffsetMv,
                    ratio = d.Ratio,
                    maxRatio = d.MaximumRatio,
                    voltageSupported = d.VoltageSupported,
                    ratioSupported = d.RatioSupported,
                    rawValue = $"0x{d.Value:X8}",
                    rawCaps = $"0x{d.Capabilities:X8}"
                };
            }

            try
            {
                return new
                {
                    ok = true,
                    vendor = "intel",
                    core = Dom(0),
                    cache = Dom(2),
                    range = new { offsetMv = new[] { -150, 0 }, ratio = new[] { 8, 60 } }
                };
            }
            catch (Exception ex)
            {
                return new { ok = false, vendor = "intel", reason = "Intel 邮箱读取失败: " + ex.Message };
            }
        }
    }

    /// <summary>写入 Intel OC：core/cache 电压偏移（mV，负值降压）与 core 倍频。</summary>
    public static object Apply(int? coreMv, int? cacheMv, int? ratio)
    {
        lock (Gate)
        {
            if (!Ensure())
                return new { ok = false, vendor = "intel", reason = _lastError };

            var results = new System.Collections.Generic.List<object>();

            if (coreMv.HasValue || ratio.HasValue)
            {
                try
                {
                    var d = _mailbox!.Apply(0, coreMv.HasValue ? coreMv.Value : (decimal?)null, ratio);
                    results.Add(new
                    {
                        item = coreMv.HasValue ? $"intel-core={coreMv}mV" : $"intel-ratio={ratio}x",
                        domain = d.Id,
                        offsetMv = (double)d.OffsetMv,
                        ratio = d.Ratio,
                        readback = $"0x{d.Value:X8}",
                        match = (!coreMv.HasValue || Math.Abs((double)d.OffsetMv - coreMv.Value) < 1.0)
                                && (!ratio.HasValue || d.Ratio == ratio.Value)
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { item = "intel-core/ratio", error = ex.Message });
                }
            }

            if (cacheMv.HasValue)
            {
                try
                {
                    var d = _mailbox!.Apply(2, cacheMv.Value, null);
                    results.Add(new
                    {
                        item = $"intel-cache={cacheMv}mV",
                        domain = d.Id,
                        offsetMv = (double)d.OffsetMv,
                        readback = $"0x{d.Value:X8}",
                        match = Math.Abs((double)d.OffsetMv - cacheMv.Value) < 1.0
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new { item = "intel-cache", error = ex.Message });
                }
            }

            return new { ok = true, vendor = "intel", applied = results };
        }
    }
}
