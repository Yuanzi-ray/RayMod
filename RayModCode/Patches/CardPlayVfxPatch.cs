using System;
using System.Reflection;
using HarmonyLib;

namespace RayMod.Patches;

/// <summary>
/// ★ 2026-10-12 <b>「出牌」钩子（专属特效的挂载点）</b>。
///
/// <para><b>挂在哪里</b>：<c>CombatHistory.CardPlayStarted(ICombatState, CardPlay)</c>
/// （<c>CombatHistory.cs:38</c>，由 <c>CardModel.cs:1929</c> 在 <b><c>OnPlay</c> 之前</b>调入）。
/// 此刻 <c>CardPlay</c> 已经造好（含 <c>Card</c> / <c>Player</c> / <c>Target</c>），
/// 所以这是"这次出牌"信息最全、且出得最早的一个点。</para>
///
/// <para><b>为什么用反射、参数声明成 <c>object</c></b>：与 <c>RichPresencePatch</c> 同一套安全策略 ——
/// 本文件<b>不引用游戏程序集的任何类型</b>，游戏改版时 <c>HarmonyPrepare</c> 返回 false
/// ⇒ 补丁静默跳过 ⇒ <b>模组照常能跑</b>，不会因为一个特效把整个模组带崩。</para>
///
/// <para><b>为什么不用 <c>CardPlayFinished</c></b>：它在 <c>OnPlay</c> <b>之后</b>
/// （<c>CardModel.cs:1961</c>），那时伤害都结算完了、卡已经飞走了 —— 特效会"迟到"。
/// <b>特效要跟动作同时发生，就挂 Started。</b></para>
///
/// <para>⚠️ 本补丁<b>只做分发</b>：判"是哪张卡、该播什么"全部在
/// <see cref="RayMod.Art.CardFieldVfx"/> 里。<b>加新卡的特效不要改这里。</b></para>
/// </summary>
[HarmonyPatch]
internal static class CardPlayVfxPatch
{
    private const string TargetTypeName = "MegaCrit.Sts2.Core.Combat.History.CombatHistory";
    private const string TargetMethodName = "CardPlayStarted";

    /// <summary>出问题时的总开关。false ⇒ 直接早退，行为回到打补丁之前。</summary>
    internal const bool Enabled = true;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        try
        {
            Type? t = AccessTools.TypeByName(TargetTypeName);

            // 按"参数个数"挑重载（不硬编码参数类型名 —— 少一个会漂移的字符串）。
            MethodInfo? m = null;

            if (t != null)
            {
                foreach (MethodInfo candidate in AccessTools.GetDeclaredMethods(t))
                {
                    if (candidate.Name == TargetMethodName && candidate.GetParameters().Length == 2)
                    {
                        m = candidate;
                        break;
                    }
                }
            }

            if (m == null)
            {
                // ★ 返回 false 是 Harmony 的"跳过本类"语义，不是异常（游戏改版时静默失效）。
                return false;
            }

            _target = m;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo? _target;

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _target;

    /// <summary>
    /// <c>__args[0]</c> = <c>ICombatState</c>（不用）· <c>__args[1]</c> = <c>CardPlay</c>（要的就是它）。
    /// 用 <c>__args</c> 而不是带类型的参数，是为了不引入游戏类型。
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(object[] __args)
    {
        if (!Enabled || __args == null || __args.Length < 2)
        {
            return;
        }

        try
        {
            object? cardPlay = __args[1];

            if (cardPlay == null)
            {
                return;
            }

            object? card = cardPlay.GetType().GetProperty("Card")?.GetValue(cardPlay);

            // ★ 当前生效：场上层（Art/CardFieldVfx.cs）—— 游戏官方 VfxCmd + 现成特效场景。
            //   两个参考 mod（SilentFX 3817850774 / RegentFX 3747497501）都走这条路；
            //   RegentFX 反编译里 SubViewport/CanvasCullMask/CanvasTransform 命中数全为 0。
            Art.CardFieldVfx.Play(card, cardPlay, __args[0]);

            // ★ 2026-10-15 发布版清理：上面这两行曾经指向另外两个实验件，**两个都已整个删除**，
            //   文件不在了、开关也不在了（别再照着旧文档去找 `CardBurnVfx` / `CardMuzzleFlash`）：
            //   ① 卡面溶解（把卡渲染进 SubViewport 再烧）：6 轮未通，坐标系那层没打穿；
            //   ② 角色身上撒火星的枪口特效：用户判定「廉价」，且只为「霰弹枪」服务（该卡特效已按用户要求删除）。
        }
        catch
        {
            // 游戏流程里绝不外抛（工程铁律：回合开始类钩子抛异常 = 整个游戏卡死）。
        }
    }
}
