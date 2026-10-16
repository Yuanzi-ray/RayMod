using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Depth;

/// <summary>
/// 层数变化事件（Power 内部总线）。
///
/// Power 和卡牌走的是同一套程序集，卡牌有 <c>Owner</c> 句柄，所以不需要全局单例，
/// 但"层数变化时触发"这件事必须由 Power 广播出去（资金、源矿都靠它），
/// 否则那些卡就得去 Harmony 拦截层数写入 —— 那是最不该干的事。
///
/// 约定：处理器是同步的。需要异步效果请自己 <c>TaskHelper.RunSafely(...)</c> 派发。
/// </summary>
public static class DepthEvents
{
    private static readonly List<DepthChangedHandler> Handlers = new();
    private static readonly List<SummonedHandler> SummonHandlers = new();

    private static readonly Logger Log = new("RayMod.Depth", LogType.Generic);

    private static bool _summonBroadcasting;

    public delegate void DepthChangedHandler(Player player, int oldDepth, int newDepth);

    /// <summary>
    /// 召唤广播的处理器。
    ///
    /// <para><b><paramref name="cardSource"/> 是 2026-10-04 加「哺育」时补的</b>：
    /// 那张牌要排除"打出「沙地兽」卡触发的召唤"（用户拍板），
    /// 而"这次召唤是不是那张卡引起的"只有 <c>BeastCmd.Summon</c> 知道 ——
    /// 与其在 Power 里另开一条旁路判据（坑 31/52 的同源原则），不如让总线一次带全参数。</para>
    /// </summary>
    public delegate void SummonedHandler(Player player, int count, CardModel? cardSource);

    public static void Register(DepthChangedHandler handler)
    {
        if (!Handlers.Contains(handler))
        {
            Handlers.Add(handler);
        }
    }

    public static void Unregister(DepthChangedHandler handler)
    {
        Handlers.Remove(handler);
    }

    public static void RegisterSummoned(SummonedHandler handler)
    {
        if (!SummonHandlers.Contains(handler))
        {
            SummonHandlers.Add(handler);
        }
    }

    public static void UnregisterSummoned(SummonedHandler handler)
    {
        SummonHandlers.Remove(handler);
    }

    public static void RaiseDepthChanged(Player player, int oldDepth, int newDepth)
    {
        if (oldDepth == newDepth)
        {
            return;
        }

        // 复制一份再遍历：处理器里可能反过来改层数（源矿 → 再次变化），
        // 直接遍历原列表会有"集合被修改"的问题。
        var snapshot = Handlers.ToArray();
        foreach (var handler in snapshot)
        {
            try
            {
                handler(player, oldDepth, newDepth);
            }
            catch (Exception e)
            {
                // 一个卡出错不能让整次层数变化崩掉。
                Log.Warn($"[DepthEvents] depth-changed handler failed: {e}");
            }
        }
    }

    /// <summary>
    /// 沙地兽召唤时广播。放在这里而不是沙地兽代码里，
    /// 是为了让"沙地兽"和"层数"两块互不依赖（沙地兽之怒、沙地兽的祝福都要用）。
    /// </summary>
    /// <param name="cardSource">引起这次召唤的卡牌（不是卡牌引起的召唤传 null）。</param>
    public static void RaiseMinionSummoned(Player player, int count, CardModel? cardSource = null)
    {
        if (_summonBroadcasting)
        {
            // 防止"召唤 → 触发效果 → 又召唤"造成的无限递归。
            return;
        }

        _summonBroadcasting = true;
        try
        {
            var snapshot = SummonHandlers.ToArray();
            foreach (var handler in snapshot)
            {
                try
                {
                    handler(player, count, cardSource);
                }
                catch (Exception e)
                {
                    Log.Warn($"[DepthEvents] summon handler failed: {e}");
                }
            }
        }
        finally
        {
            _summonBroadcasting = false;
        }
    }

    /// <summary>战斗/跑团结束时清空注册，避免上一个存档的卡残留。</summary>
    public static void ClearAll()
    {
        Handlers.Clear();
        SummonHandlers.Clear();
        _summonBroadcasting = false;
    }
}
