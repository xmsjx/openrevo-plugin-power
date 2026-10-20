// ============================================================================
//  PowerTrace — 写入后高频回读 PM 表，用于判断"写入被接受后又被覆盖"
//
//  用途：调用方写完功耗墙后，立刻连续采样 PM 表若干次，
//        把每次的 SPL/sPPT/fPPT 记录下来。
//        - 若某次采样出现"接近目标值"，随后又回到原值 ⇒ 被固件/服务覆盖
//        - 若从头到尾都是原值 ⇒ 命令未生效
//
//  ⚠️ 纯只读（除调用方自己的写入外不写任何寄存器）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using PawnIO;

internal static class PowerTrace
{
    public sealed record Sample(int Ms, float Stapm, float Slow, float Fast, float Tctl);

    /// <summary>在 durationMs 内每 intervalMs 采一次 PM 表快照。</summary>
    public static List<Sample> Collect(RyzenSmuService smu, int durationMs = 5000, int intervalMs = 200)
    {
        var list = new List<Sample>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < durationMs)
        {
            try
            {
                var snap = smu.GetPowerSnapshot();
                if (snap != null)
                    list.Add(new Sample((int)sw.ElapsedMilliseconds, snap.Stapm, snap.Slow, snap.Fast, snap.TctlTemp));
            }
            catch (Exception ex)
            {
                Logger.WriteLine("[trace] " + ex.Message);
            }
            Thread.Sleep(intervalMs);
        }
        return list;
    }

    /// <summary>把采样压缩成一行的字符串（便于写日志/返回）。</summary>
    public static string Summarize(List<Sample> samples)
    {
        if (samples == null || samples.Count == 0) return "(无采样)";
        var sb = new System.Text.StringBuilder();
        foreach (var s in samples)
            sb.Append($"{s.Ms}ms:{s.Stapm:F0}/{s.Slow:F0}/{s.Fast:F0} ");
        return sb.ToString().TrimEnd();
    }
}
