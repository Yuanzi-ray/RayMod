using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace RayMod.Hud;

/// <summary>
/// <b>战斗 HUD 上两个资源计数器的登记处</b>（2026-10-07）：层数与弹药各一个，并排挂在能量球右侧。
///
/// <para>加第三个资源时只要在这里加一行 + 在它自己的数值汇聚点里 <c>Push</c> 一次即可。</para>
/// </summary>
internal static class ResourceHud
{
    private static readonly Logger Log = new("RayMod.Hud", LogType.Generic);

    /// <summary>层数（<c>DepthCmd.SyncToPower</c> 里推）。<b>没有上限</b>，所以只显示数字。</summary>
    internal static readonly ResourceHudCounter Depth = new(
        "RayModDepthCounter",
        $"{Entry.ResPath}/images/characters/RayMod_depth_icon.png",
        slot: 0,
        Log);

    /// <summary>弹药（<c>AmmoCmd.SyncToPower</c> 里推）。显示「当前/上限」。</summary>
    internal static readonly ResourceHudCounter Ammo = new(
        "RayModAmmoCounter",
        $"{Entry.ResPath}/images/characters/RayMod_ammo_icon.png",
        slot: 1,
        Log);

    /// <summary>
    /// ★ 2026-10-12：<b>战斗一开始就把两个计数器挂上</b>（用户反馈"他们总是慢于能量出现"）。
    ///
    /// <para>计数器原本只在 <c>Push()</c> 里创建，而 <c>Push</c> 要等数值第一次同步 ——
    /// 于是永远比能量球晚出现。这个方法由 <c>Patches\HudAttachTimingPatch</c>
    /// （挂 <c>NCombatRoom._Ready</c>）调用，让它们<b>和能量球一起出现</b>。</para>
    ///
    /// <para>先用 <c>0</c> 占位：真实数值到来时 <c>Push</c> 会覆盖。战斗外调用是安全的。</para>
    /// </summary>
    internal static void EnsureAttached()
    {
        // ★★ 2026-10-13 联机适配：**本地玩家不是莱伊就一个都不挂**。
        //
        //   `EnsureAttached` 是**无条件**挂的（为了让它们和能量球一起出现），
        //   而所有 `Push` 都在 `if (LocalContext.IsMe(player))` 里面
        //   （`Depth\DepthCmd.cs` / `Ammo\AmmoState.cs`）⇒ **非莱伊玩家**屏幕上从此永久挂着
        //   一块「层数 0 / 弹药 0/1」的牌子，**永远不会有数据源**。
        //   联机里这是常态（队友是铁甲战士之类），单机选了别的角色也一样。
        //   判据本身是纯表现层的（只决定画不画牌子），**碰不到任何游戏状态**。
        if (!LocalPlayerIsRay())
        {
            if (!_loggedNotRay)
            {
                _loggedNotRay = true;
                Log.Info("[HUD] 本地玩家不是莱伊 ⇒ 不挂层数/弹药计数器" +
                         "（否则会留一块永远没有数据源的牌子）");
            }

            return;
        }

        try
        {
            Depth.EnsureAttached("0");
            Ammo.EnsureAttached("0/1");
        }
        catch (System.Exception e)
        {
            Log.Warn($"[HUD] 提前挂载失败（不影响数值同步）：{e.GetType().Name}: {e.Message}");
        }
    }

    private static bool _loggedNotRay;

    // ===================== 本地玩家是不是莱伊（联机门控用）=====================
    //
    //  本文件刻意走反射：与 `Art\CardFieldVfx.cs` 的 `IsLocalPlayer` 同一套策略 ——
    //  这些类型只在游戏程序集里，找不到时**只是失去门控**（回到旧行为 = 照挂），
    //  不会让整个 mod 起不来。`RunManager.State` 是 **private** 属性，
    //  但 `AccessTools.Property` 本来就找非公开成员（`Patches\RichPresencePatch.cs:138`
    //  用的正是这一写法，已在实机跑通）。
    //
    //  ⚠️ 判不了（反射失败 / 不在 run 里）一律返回 **true = 照旧挂** —— 宁可多一块牌子，
    //     也不能因为一个判据让莱伊的两块牌子消失。

    private static MethodInfo? _getMeMethod;
    private static bool _getMeLookupDone;

    private static bool LocalPlayerIsRay()
    {
        try
        {
            object? state = RunStateOf();

            if (state == null)
            {
                return true;
            }

            object? me = GetMe(state);

            if (me == null)
            {
                return true;
            }

            object? character = me.GetType().GetProperty("Character")?.GetValue(me);

            if (character == null)
            {
                return true;
            }

            return character.GetType().Name == "RayModCharacter";
        }
        catch
        {
            return true;
        }
    }

    private static object? RunStateOf()
    {
        Type? runManager = HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");

        object? instance = runManager?
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);

        return instance == null
            ? null
            : HarmonyLib.AccessTools.Property(instance.GetType(), "State")?.GetValue(instance);
    }

    /// <summary>
    /// 取本地玩家：<c>LocalContext.GetMe(IPlayerCollection)</c>。
    /// ⚠️ 必须按**参数类型名**挑重载 —— `LocalContext` 有 6 个同名 `GetMe`。
    /// </summary>
    private static object? GetMe(object state)
    {
        if (!_getMeLookupDone)
        {
            _getMeLookupDone = true;

            try
            {
                Type? localContext = HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext");

                if (localContext != null)
                {
                    foreach (MethodInfo m in HarmonyLib.AccessTools.GetDeclaredMethods(localContext))
                    {
                        ParameterInfo[] ps = m.GetParameters();

                        if (m.IsStatic && m.Name == "GetMe" && ps.Length == 1
                            && ps[0].ParameterType.Name == "IPlayerCollection")
                        {
                            _getMeMethod = m;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // 保持 null ⇒ 上层放行。
            }
        }

        return _getMeMethod?.Invoke(null, new[] { state });
    }

    /// <summary>
    /// 设置页改了位置/大小之后调用：两个计数器卸掉重建，下次刷新就落到新位置。
    /// （数值本身不动，只是重排 —— 所以随便调，调坏了也不会丢数据。）
    /// </summary>
    internal static void InvalidateLayout()
    {
        Depth.Invalidate();
        Ammo.Invalidate();
    }
}
