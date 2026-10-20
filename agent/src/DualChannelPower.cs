// ============================================================================
//  DualChannelPower — 双通道功耗墙写入（MP1 + PSMU 都发）
//
//  背景：Phoenix / Strix 等 APU 上，只发 MP1 的功耗墙命令，固件**不采纳**
//        （实测：返回 OK，但 PM 表 SPL/fPPT/sPPT 不变）。
//        参考实现的做法是：同一个逻辑命令，MP1 与 PSMU 两个邮箱**都发一次**。
//
//  命令对照（Phoenix/Strix 家族）：
//      STAPM limit : MP1 0x14 / PSMU 0x31
//      Fast  limit : MP1 0x15 / PSMU 0x32
//      Slow  limit : MP1 0x16 / PSMU 0x33
//      STAPM time  : MP1 0x18 / PSMU 0x36
//      Slow  time  : MP1 0x17 / PSMU 0x35
//      Tctl  temp  : MP1 0x19（仅 MP1）
//
//  ⚠️ 只在 Mobile / StrixPoint / StrixHalo 家族启用双通道；
//     Raven / Renoir / Raphael 等家族保持原有单通道行为（避免改变已验证的通路）。
// ============================================================================
using System;
using PawnIO;

internal static class DualChannelPower
{
    /// <summary>该家族是否适用"MP1 + PSMU 双发"。</summary>
    public static bool Applies(CpuFamily family) =>
        family == CpuFamily.Mobile ||
        family == CpuFamily.StrixPoint ||
        family == CpuFamily.StrixHalo;

    public sealed record Result(SmuStatus Mp1, SmuStatus Psmu)
    {
        public bool Ok => Mp1 == SmuStatus.OK && Psmu == SmuStatus.OK;
        public string Text => $"MP1={Mp1}, PSMU={Psmu}";
    }

    private static Result Send(RyzenSmuService smu, uint mp1Cmd, uint psmuCmd, int watts)
    {
        // 单位：mW（LM 的 SetStapm/SetFast/SetSlow 都是 watts * 1000）
        uint arg = (uint)(watts * 1000);
        var a = smu.SendMp1(mp1Cmd, arg);
        var b = smu.SendPsmu(psmuCmd, arg);
        return new Result(a, b);
    }

    public static Result SetStapm(RyzenSmuService smu, int watts) => Send(smu, 0x14, 0x31, watts);
    public static Result SetFast(RyzenSmuService smu, int watts) => Send(smu, 0x15, 0x32, watts);
    public static Result SetSlow(RyzenSmuService smu, int watts) => Send(smu, 0x16, 0x33, watts);

    private static Result SendSeconds(RyzenSmuService smu, uint mp1Cmd, uint psmuCmd, int seconds)
    {
        uint arg = (uint)seconds;
        var a = smu.SendMp1(mp1Cmd, arg);
        var b = smu.SendPsmu(psmuCmd, arg);
        return new Result(a, b);
    }

    public static Result SetStapmTime(RyzenSmuService smu, int seconds) => SendSeconds(smu, 0x18, 0x36, seconds);
    public static Result SetSlowTime(RyzenSmuService smu, int seconds) => SendSeconds(smu, 0x17, 0x35, seconds);
}
