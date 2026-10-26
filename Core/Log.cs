using System.Collections.Concurrent;
using System.Text;

namespace MouseWheelEnhancement.Core;

/// <summary>
/// 极简滚动日志。写入放到后台线程，**调用方永远不会因为写日志而阻塞**，
/// 这一点对钩子线程尤其重要。
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const int KeepFiles = 3;

    private static readonly BlockingCollection<string> Queue = new(2048);
    private static readonly object FileLock = new();
    private static string? _dir;
    private static Thread? _worker;

    /// <summary>日志文件的完整路径（`&lt;数据目录&gt;\logs\app.log`）。</summary>
    public static string FilePath => Path.Combine(_dir ?? AppPaths.LogDir, "app.log");

    /// <summary>日志目录跟着数据目录走，见 <see cref="AppPaths"/>。</summary>
    public static string DefaultDir => AppPaths.LogDir;

    public static void Init(string? dir = null)
    {
        _dir = dir ?? AppPaths.LogDir;
        try { Directory.CreateDirectory(_dir); } catch { /* 写不了日志也不能影响启动 */ }

        if (_worker != null) return;
        _worker = new Thread(() =>
        {
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try { AppendToFile(line); } catch { /* 忽略 */ }
            }
        })
        { IsBackground = true, Name = "MouseWheelEnhancement-Log" };
        _worker.Start();

        Info($"==== 启动 v{Version} ====");
    }

    public static void Info(string msg) => Enqueue("INFO ", msg);
    public static void Warn(string msg) => Enqueue("WARN ", msg);
    public static void Error(string msg, Exception? ex = null)
        => Enqueue("ERROR", ex == null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>当前程序版本（取自程序集，不手写，避免和发版号对不上）。</summary>
    public static string Version =>
        typeof(Log).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    private static void Enqueue(string level, string msg)
    {
        // ⚠ 任何一条日志都可能包含路径、URL；**令牌绝不会进这里**（调用方负责）
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        Queue.TryAdd(line, 0);
    }

    private static void AppendToFile(string line)
    {
        lock (FileLock)
        {
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes) Rotate(path);

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
            fs.Write(bytes, 0, bytes.Length);
        }
    }

    private static void Rotate(string path)
    {
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var from = $"{path}.{i}";
            var to = $"{path}.{i + 1}";
            if (File.Exists(from)) { try { File.Move(from, to, true); } catch { } }
        }
        try { File.Move(path, path + ".1", true); } catch { }
    }

    /// <summary>
    /// 等后台线程把手里的日志写完。退出前必须调 —— 后台线程是 background，
    /// 进程一退就被掐掉，最后几行（往往正是"为什么退出"）会丢。
    /// </summary>
    public static void Flush(int timeoutMs = 500)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(5);
        Thread.Sleep(20); // 让最后一条真正落盘
    }

}
