using MegaCrit.Sts2.Core.Entities.Players;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Ammo;

/// <summary>
/// 弹药变化事件（与 <see cref="RayMod.Depth.DepthEvents"/> 同构）。
///
/// <para><b>为什么需要它</b>：弹药的"真实值"存在跑团级存档 <see cref="AmmoState"/> 里，
/// 战斗内的 <see cref="AmmoPower"/> 只是显示镜像，而且它<b>刻意走 <c>SetAmount</c></b>
/// （不走 <c>PowerCmd.ModifyAmount</c>）—— 因为弹药为 0 时必须留在场上，
/// 而 <c>ModifyAmount</c> 判定"数值归零就移除 Power"。
/// 代价就是：**原版的 <c>AfterPowerAmountChanged</c> 钩子收不到弹药变化**。</para>
///
/// <para>所以需要一个自己的总线，让"每当弹药变动时"这类效果有地方挂。
/// 层数那边早就有 <see cref="RayMod.Depth.DepthEvents"/> 了（用户 2026-10-03 备好的），
/// 这是补上弹药那一半。</para>
///
/// <para>约定与 DepthEvents 一致：处理器是同步的。需要异步效果请自己
/// <c>TaskHelper.RunSafely(...)</c> 派发。</para>
/// </summary>
public static class AmmoEvents
{
    private static readonly List<AmmoChangedHandler> Handlers = new();

    private static readonly Logger Log = new("RayMod.Ammo", MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public delegate void AmmoChangedHandler(Player player, int oldValue, int newValue);

    public static void Register(AmmoChangedHandler handler)
    {
        if (!Handlers.Contains(handler))
        {
            Handlers.Add(handler);
        }
    }

    public static void Unregister(AmmoChangedHandler handler)
    {
        Handlers.Remove(handler);
    }

    /// <summary>
    /// 弹药（当前值或上限）真的变了才广播 —— 值没变就不发，免得"空刷新"也触发效果。
    /// </summary>
    public static void RaiseAmmoChanged(Player player, int oldValue, int newValue)
    {
        if (oldValue == newValue)
        {
            return;
        }

        // 复制一份再遍历：处理器里可能反过来改弹药（例如某个效果连锁）。
        var snapshot = Handlers.ToArray();

        foreach (var handler in snapshot)
        {
            try
            {
                handler(player, oldValue, newValue);
            }
            catch (Exception e)
            {
                // 一个效果出错不能让整次弹药变化崩掉。
                Log.Warn($"[AmmoEvents] ammo-changed handler failed: {e}");
            }
        }
    }

    /// <summary>战斗/跑团结束时清空注册，避免上一个存档的 Power 残留。</summary>
    public static void ClearAll()
    {
        Handlers.Clear();
    }
}
