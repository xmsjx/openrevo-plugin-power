// ============================================================================
//  Stubs: 让复用的 PawnIO 层脱离 LM 主程序即可编译。
//  - Logger      : LM 的日志入口；这里输出到审计文件（可被 /probe 忽略）
//  - AppConfig   : LM 的配置读取；这里只返回默认值（本代理不依赖 UI 配置）
// ============================================================================
using System;
using System.IO;

namespace OpenRevoUx
{
    public static class AppConfig
    {
        public static int Get(string key, int fallback) => fallback;
        public static string Get(string key, string fallback) => fallback;
        public static bool Get(string key, bool fallback) => fallback;
    }
}

public static class Logger
{
    private static readonly object Gate = new object();
    private static string? _path;

    public static void SetPath(string path) { _path = path; }

    public static void WriteLine(string message)
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path!, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
    }
}