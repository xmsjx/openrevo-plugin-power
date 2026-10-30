// ============================================================================
//  ux-agent — OpenRevo "CPU 调优（UXTU 风格）" 本地代理
//
//  职责：把 LM 已实现的 PawnIO / SMU 能力，以 HTTP JSON 暴露给 OpenRevo 标签页。
//        - 只读探测：CPU 代号 / 家族 / SMU 版本 / PawnIO 状态 / FMax 当前值
//        - 应用：只写请求里出现的项（CO 全核 / 每核 / FMax / 温度墙），写后回读
//        - 不持久化、不自动应用（MVP 范围）
//
//  许可：GPL-3.0-only（复用的 PawnIO 层与 SMU 命令语义来自 GPL-3.0 参考实现）
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PawnIO;

internal static class Program
{
    private static int _port = 18946;
    private static readonly object Gate = new();
    private static RyzenSmuService? _smu;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 前端发 camelCase（stapmW），属性是 PascalCase（StapmW）——必须忽略大小写
        PropertyNameCaseInsensitive = true,
        // 序列化也输出 camelCase，前端才能读到 item/status/readback
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task<int> Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) _port = p;

        var logDir = Path.Combine(Path.GetTempPath(), "ux-agent");
        Directory.CreateDirectory(logDir);
        Logger.SetPath(Path.Combine(logDir, "agent.log"));

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        try { listener.Start(); }
        catch (Exception ex) { Console.Error.WriteLine($"bind failed: {ex.Message}"); return 2; }
        Console.WriteLine($"ux-agent listening on http://127.0.0.1:{_port}/");

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
        return 0;
    }

    private static async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        res.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        try
        {
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
            object body = path switch
            {
                "/ping" => new { ok = true, pid = Environment.ProcessId, port = _port },
                "/probe" => Probe(),
                "/apply" => await Apply(req),
                _ => new { ok = false, reason = "unknown endpoint" }
            };
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, JsonOpts));
            res.StatusCode = 200; res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes); res.Close();
        }
        catch (Exception ex)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, reason = ex.Message }, JsonOpts));
                res.StatusCode = 500; res.ContentType = "application/json; charset=utf-8";
                res.ContentLength64 = bytes.Length; res.OutputStream.Write(bytes); res.Close();
            }
            catch { }
        }
    }

    /// <summary>创建并初始化 SMU（需要 PawnIO 驱动 + 管理员权限）。初始化失败时返回原因。</summary>
    private static bool EnsureSmu(out string? reason)
    {
        reason = null;
        _smu ??= new RyzenSmuService();
        if (_smu.IsInitialized) return true;
        try
        {
            if (!_smu.Initialize(Assembly.GetExecutingAssembly()))
            {
                reason = "SMU 初始化失败：请确认 PawnIO 服务正在运行，并以管理员权限启动本代理";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "SMU 初始化异常: " + ex.Message;
            return false;
        }
    }
    private sealed class ApplyRequest
    {
        public int? CoAll { get; set; }
        public Dictionary<string, int>? CoPerCore { get; set; }
        public int? FmaxMhz { get; set; }
        public int? ThmCelsius { get; set; }
        // 功耗墙（PL1/PL2/PL3 的 AMD 对应量）
        public int? StapmW { get; set; }
        public int? FastW { get; set; }
        public int? SlowW { get; set; }
        // 功耗时间窗（秒）
        public int? StapmTimeSec { get; set; }
        public int? SlowTimeSec { get; set; }
        // 结温目标（℃）
        public int? TctlCelsius { get; set; }
        // Intel：core/cache 电压偏移（mV，负值降压）与 core 倍频
        public int? IntelCoreMv { get; set; }
        public int? IntelCacheMv { get; set; }
        public int? IntelRatio { get; set; }
    }

    private sealed class ItemResult
    {
        public string Item { get; set; } = "";
        public string Status { get; set; } = "";
        public int? Readback { get; set; }
        public bool? Match { get; set; }
        public string? Note { get; set; }
    }

    private static object Probe()
    {
        // Intel 平台走独立通道（MSR OC 邮箱），与 AMD 的 SMU 完全分离
        if (!CpuInfo.IsAMD)
            return IntelPath.Probe();

        lock (Gate)
        {
            var cpu = CpuInfo.Name;
            var codeName = "";
            var family = "";
            uint smuVersion = 0;
            bool initialized = false;
            var pawnInstalled = RyzenSmuService.IsPawnInstalled();

            string? initReason;
            EnsureSmu(out initReason);
            try
            {
                codeName = _smu!.CpuCodeName.ToString();
                family = _smu.Family.ToString();
                smuVersion = _smu.SmuVersion;
                initialized = _smu.IsInitialized;
            }
            catch (Exception ex) { Logger.WriteLine($"[probe] {ex.Message}"); }

            int? fmaxCurrent = null;
            string? fmaxNote = null;
            object? power = null;
            string? powerNote = null;
            if (initialized)
            {
                try
                {
                    var st = _smu!.GetBoostLimitFrequency(out var resp);
                    if (st == SmuStatus.OK && resp != null && resp.Length > 0)
                        fmaxCurrent = (int)(resp[0] & 0xFFFFF);
                    else fmaxNote = st.ToString();
                }
                catch (Exception ex) { fmaxNote = ex.Message; }

                try
                {
                    var snap = _smu!.GetPowerSnapshot();
                    if (snap != null)
                    {
                        power = new
                        {
                            stapmW = snap.Stapm,
                            fastW = snap.Fast,
                            slowW = snap.Slow,
                            tctlCelsius = snap.TctlTemp,
                            stapmTimeSec = snap.StapmTimeSeconds,
                            slowTimeSec = snap.SlowTimeSeconds,
                            tableVersion = $"0x{snap.TableVersion:X6}"
                        };
                    }
                    else powerNote = "PM 表读取失败（该代号可能不支持）";
                }
                catch (Exception ex) { powerNote = "PM 表异常: " + ex.Message; }
            }

            // CO 每核回读（纯读：每核一条命令，只在 probe 时读一次，不轮询）
            int[]? coPerCore = null;
            string? coReadbackNote = null;
            int cores = 1;
            try { cores = Math.Max(1, CpuInfo.PhysicalCoreCount); }
            catch { cores = Math.Max(1, Environment.ProcessorCount); }

            if (initialized && _smu != null)
            {
                if (RyzenSmuService.SupportsPerCoreReadback(_smu.CpuCodeName))
                {
                    try
                    {
                        var arr = new int[cores];
                        bool allOk = true;
                        for (int i = 0; i < cores; i++)
                        {
                            var st = _smu.ReadCurveMargin(i, out var resp);
                            if (st != SmuStatus.OK || resp == null || resp.Length == 0)
                            {
                                allOk = false;
                                coReadbackNote = $"Core {i} 读取失败: {st}";
                                break;
                            }
                            var mv = RyzenSmuService.DecodeCurveMargin(resp[0]);
                            if (!mv.HasValue)
                            {
                                allOk = false;
                                coReadbackNote = $"Core {i} 解码失败";
                                break;
                            }
                            arr[i] = mv.Value;
                        }
                        if (allOk) coPerCore = arr;
                    }
                    catch (Exception ex) { coReadbackNote = "CO 回读异常: " + ex.Message; }
                }
                else coReadbackNote = "该代号没有 CO 回读命令";
            }

            return new
            {
                ok = true,
                cpu,
                codeName = initialized ? codeName : (string?)null,
                family = initialized ? family : (string?)null,
                smuVersion = smuVersion == 0 ? null : $"0x{smuVersion:X6}",
                pawnInstalled,
                initReason = initialized ? null : (initReason ?? "SMU not initialized"),
                initialized,
                current = new
                {
                    fmaxMhz = fmaxCurrent,
                    fmaxNote,
                    power,
                    powerNote,
                    coPerCore,
                    coReadbackNote
                },
                cores,
                range = new
                {
                    coAll = new[] { -50, 0 },
                    thmCelsius = new[] { 60, 105 },
                    fmaxMhz = new[] { 1000, 6000 },
                    stapmW = new[] { 5, 120 },
                    fastW = new[] { 5, 160 },
                    slowW = new[] { 5, 160 },
                    timeSeconds = new[] { 1, 512 }
                }
            };
        }
    }

    private static async Task<object> Apply(HttpListenerRequest req)
    {
        string raw;
        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
            raw = await sr.ReadToEndAsync();

        ApplyRequest? body;
        try { body = JsonSerializer.Deserialize<ApplyRequest>(raw, JsonOpts); }
        catch (Exception ex) { return new { ok = false, reason = "invalid json: " + ex.Message }; }
        if (body == null) return new { ok = false, reason = "empty body" };

        // Intel 平台：只走 MSR OC 邮箱（core/cache 偏移 + ratio），不碰 AMD SMU
        if (!CpuInfo.IsAMD)
        {
            if (!body.IntelCoreMv.HasValue && !body.IntelCacheMv.HasValue && !body.IntelRatio.HasValue)
                return new { ok = false, vendor = "intel", reason = "Intel 平台只支持 intelCoreMv / intelCacheMv / intelRatio" };
            return IntelPath.Apply(body.IntelCoreMv, body.IntelCacheMv, body.IntelRatio);
        }

        lock (Gate)
        {
            string? why;
            if (!EnsureSmu(out why))
                return new { ok = false, reason = why, codeName = _smu?.CpuCodeName.ToString() };

            var results = new List<ItemResult>();

            if (body.CoAll.HasValue)
            {
                var v = body.CoAll.Value;
                var st = _smu.SetCoAll(v);
                results.Add(new ItemResult { Item = $"coAll={v}", Status = st.ToString(), Note = st == SmuStatus.OK ? null : "写入未被接受" });
            }

            if (body.CoPerCore != null)
            {
                foreach (var kv in body.CoPerCore)
                {
                    if (!int.TryParse(kv.Key, out var core)) { results.Add(new ItemResult { Item = $"core[{kv.Key}]", Status = "BadCoreIndex" }); continue; }
                    var st = _smu.SetCoPerCore(core, kv.Value);
                    results.Add(new ItemResult { Item = $"core[{core}]={kv.Value}", Status = st.ToString() });
                }
            }

            if (body.FmaxMhz.HasValue)
            {
                var mhz = (uint)body.FmaxMhz.Value;
                var st = _smu.SetBoostLimitFrequencyAllCores(mhz);
                int? readback = null; bool? match = null; string? note = null;
                if (st == SmuStatus.OK)
                {
                    try
                    {
                        var rst = _smu.GetBoostLimitFrequency(out var resp);
                        if (rst == SmuStatus.OK && resp != null && resp.Length > 0)
                        {
                            readback = (int)(resp[0] & 0xFFFFF);
                            match = readback == (int)mhz;
                        }
                        else note = "回读失败: " + rst;
                    }
                    catch (Exception ex) { note = "回读异常: " + ex.Message; }
                }
                results.Add(new ItemResult { Item = $"fmax={mhz}", Status = st.ToString(), Readback = readback, Match = match, Note = note });
            }

            if (body.ThmCelsius.HasValue)
            {
                var st = _smu.SetThm(body.ThmCelsius.Value);
                results.Add(new ItemResult { Item = $"thm={body.ThmCelsius.Value}", Status = st.ToString() });
            }

            // ===== 顺序与单位对齐参考实现（RyzenAdj 参数顺序，每条间隔 50ms）=====
            //   tctl-temp → stapm-limit → fast-limit → stapm-time → slow-limit → slow-time
            var dual = DualChannelPower.Applies(_smu.Family);

            if (body.TctlCelsius.HasValue)
            {
                var st = _smu.SetTctlTemp(body.TctlCelsius.Value);
                results.Add(new ItemResult { Item = $"tctl={body.TctlCelsius.Value}", Status = st.ToString() });
                System.Threading.Thread.Sleep(50);
            }

            if (body.StapmW.HasValue)
            {
                if (dual)
                {
                    var r = DualChannelPower.SetStapm(_smu, body.StapmW.Value);
                    results.Add(new ItemResult { Item = $"stapm(PL1)={body.StapmW.Value}W", Status = r.Ok ? "OK" : "Failed", Note = r.Text });
                }
                else
                {
                    var st = _smu.SetStapmLimit(body.StapmW.Value);
                    results.Add(new ItemResult { Item = $"stapm(PL1)={body.StapmW.Value}W", Status = st.ToString() });
                }
                System.Threading.Thread.Sleep(50);
            }

            if (body.FastW.HasValue)
            {
                if (dual)
                {
                    var r = DualChannelPower.SetFast(_smu, body.FastW.Value);
                    results.Add(new ItemResult { Item = $"fast(PL2)={body.FastW.Value}W", Status = r.Ok ? "OK" : "Failed", Note = r.Text });
                }
                else
                {
                    var st = _smu.SetFastLimit(body.FastW.Value);
                    results.Add(new ItemResult { Item = $"fast(PL2)={body.FastW.Value}W", Status = st.ToString() });
                }
                System.Threading.Thread.Sleep(50);
            }

            if (body.StapmTimeSec.HasValue)
            {
                if (dual)
                {
                    var r = DualChannelPower.SetStapmTime(_smu, body.StapmTimeSec.Value);
                    results.Add(new ItemResult { Item = $"stapm-time={body.StapmTimeSec.Value}s", Status = r.Ok ? "OK" : "Failed", Note = r.Text });
                }
                else
                {
                    var st = _smu.SetStapmTimeSeconds(body.StapmTimeSec.Value);
                    results.Add(new ItemResult { Item = $"stapm-time={body.StapmTimeSec.Value}s", Status = st.ToString() });
                }
                System.Threading.Thread.Sleep(50);
            }

            if (body.SlowW.HasValue)
            {
                if (dual)
                {
                    var r = DualChannelPower.SetSlow(_smu, body.SlowW.Value);
                    results.Add(new ItemResult { Item = $"slow={body.SlowW.Value}W", Status = r.Ok ? "OK" : "Failed", Note = r.Text });
                }
                else
                {
                    var st = _smu.SetSlowLimit(body.SlowW.Value);
                    results.Add(new ItemResult { Item = $"slow={body.SlowW.Value}W", Status = st.ToString() });
                }
                System.Threading.Thread.Sleep(50);
            }

            if (body.SlowTimeSec.HasValue)
            {
                if (dual)
                {
                    var r = DualChannelPower.SetSlowTime(_smu, body.SlowTimeSec.Value);
                    results.Add(new ItemResult { Item = $"slow-time={body.SlowTimeSec.Value}s", Status = r.Ok ? "OK" : "Failed", Note = r.Text });
                }
                else
                {
                    var st = _smu.SetSlowTimeSeconds(body.SlowTimeSec.Value);
                    results.Add(new ItemResult { Item = $"slow-time={body.SlowTimeSec.Value}s", Status = st.ToString() });
                }
                System.Threading.Thread.Sleep(50);
            }
            // 写完后读一次 PM 表快照，作为“写后回读”的证据（固件可能延迟）
            object? after = null;
            string? afterNote = null;
            try
            {
                var snap = _smu.GetPowerSnapshot();
                if (snap != null)
                {
                    after = new
                    {
                        stapmW = snap.Stapm,
                        fastW = snap.Fast,
                        slowW = snap.Slow,
                        tctlCelsius = snap.TctlTemp,
                        stapmTimeSec = snap.StapmTimeSeconds,
                        slowTimeSec = snap.SlowTimeSeconds
                    };
                }
                else afterNote = "写后 PM 表读取失败";
            }
            catch (Exception ex) { afterNote = "写后 PM 表异常: " + ex.Message; }

            // 写后高频回读（2 秒 / 200ms 一次）：用于判断是否"写了又被覆盖"
            string? trace = null;
            bool wrotePower = body.StapmW.HasValue || body.FastW.HasValue || body.SlowW.HasValue
                              || body.StapmTimeSec.HasValue || body.SlowTimeSec.HasValue;
            if (wrotePower)
            {
                try
                {
                    var samples = PowerTrace.Collect(_smu, 2000, 200);
                    trace = PowerTrace.Summarize(samples);
                    Logger.WriteLine("[trace] " + trace);
                }
                catch (Exception ex) { trace = "trace 异常: " + ex.Message; }
            }

            // 没有任何实际写入项 ⇒ 不算成功（避免"假成功"）
            var allOk = results.Count > 0 && results.TrueForAll(r => r.Status == "OK" && r.Match != false);
            Logger.WriteLine($"[apply] {raw} => ok={allOk}");
            return new { ok = allOk, applied = results, after, afterNote, trace };
        }
    }
}