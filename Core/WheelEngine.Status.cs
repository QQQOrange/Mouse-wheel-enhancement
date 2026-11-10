// WheelEngine 的“观测/诊断”状态（v1.9.11 从主文件拆出）
namespace MouseWheelEnhancement.Core;

public sealed partial class WheelEngine
{
    /// <summary>一个"最近识别到的程序"（健康页 / 诊断用）。</summary>
    public sealed record RecentTarget(string Exe, string Class, string Semantics, long Events, long Boosted, long LastTicks);

    /// <summary>
    /// 最近目标的活动计数。用**可变条目 + 值元组键**：钩子热路径每来一个事件
    /// 只改几个整数，不再拼 `exe|class` 字符串、不再分配 record。
    /// 快照给界面时才生成不可变的 <see cref="RecentTarget"/>。
    /// </summary>
    private sealed class RecentEntry
    {
        public string Exe = "";
        public string Class = "";
        public string Semantics = "";
        public long Events;
        public long Boosted;
        public long LastTicks;
    }

    private static readonly object RecentLock = new();
    private static readonly Dictionary<(string Exe, string Class), RecentEntry> RecentMap = new();

    /// <summary>最近识别到的程序（按最后出现时间倒序，最多 24 条）。</summary>
    public static IReadOnlyList<RecentTarget> RecentTargets()
    {
        lock (RecentLock)
        {
            return RecentMap.Values
                .OrderByDescending(t => t.LastTicks)
                .Take(24)
                .Select(t => new RecentTarget(t.Exe, t.Class, t.Semantics, t.Events, t.Boosted, t.LastTicks))
                .ToList();
        }
    }

    private static void TrackRecent(TargetInfo target, bool boosted)
    {        if (!target.IsKnown) return;
        var key = (target.Exe, target.Class);
        var now = Environment.TickCount64;

        lock (RecentLock)
        {
            if (RecentMap.TryGetValue(key, out var e))
            {
                e.Events++;
                if (boosted) e.Boosted++;
                e.LastTicks = now;
            }
            else
            {
                e = new RecentEntry
                {
                    Exe = target.Exe,
                    Class = target.Class,
                    Semantics = target.ScrollsOncePerEvent
                        ? (target.IgnoresSystemLines ? "按事件·不读行数" : "按事件")
                        : "按像素",
                    Events = 1,
                    Boosted = boosted ? 1 : 0,
                    LastTicks = now,
                };
                RecentMap[key] = e;

                if (RecentMap.Count > 48)
                {
                    var stale = RecentMap.OrderBy(t => t.Value.LastTicks).Take(24).Select(t => t.Key).ToList();
                    foreach (var k in stale) RecentMap.Remove(k);
                }
            }
        }
    }

    /// <summary>自检/基准用：直接走一次最近目标记录，不经过钩子。</summary>
    internal static void TrackRecentForBenchmark(TargetInfo target, bool boosted)
        => TrackRecent(target, boosted);

    /// <summary>
    /// 最近一次滚轮事件的判定快照（给 <c>--targettrace</c> 用）。
    /// 钩子线程写、别的线程读，只存一个字符串引用，原子性足够。
    /// <para>
    /// v1.9.11：只有诊断模式把 <see cref="TraceEnabled"/> 打开时才拼字符串。
    /// 常驻时每个事件都插值一次是白白的分配，还会给钩子线程添 GC 压力。
    /// </para>
    /// </summary>
    public static volatile string TraceLine = "";

    /// <summary>诊断开关：只有 <c>--targettrace</c> 会打开。</summary>
    public static volatile bool TraceEnabled;
}
