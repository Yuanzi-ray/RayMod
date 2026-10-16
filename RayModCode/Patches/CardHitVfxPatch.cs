using System;
using System.Reflection;
using HarmonyLib;

namespace RayMod.Patches;

/// <summary>
/// ★★ 2026-10-12 <b>「命中特效」挂到【真正造成伤害】的那一刻</b>（用户指出"特效和卡对不齐"之后的正解）。
///
/// <para><b>为什么不是挂在出牌上</b>：
/// 出牌（<c>CardPlayStarted</c>）只是"玩家打出这张牌"，<b>伤害发生在若干帧之后</b> ——
/// 中间还隔着动画、目标选择、多段结算。开火的时间点放在出牌上、而伤害在别处，
/// 就必然"对不齐"。<b>把命中层挂在伤害上，它就永远跟着伤害走。</b></para>
///
/// <para><b>挂在哪</b>：<c>Hook.AfterDamageGiven(PlayerChoiceContext, ICombatState, Creature? dealer,
/// DamageResult results, ValueProp props, Creature target, CardModel? cardSource)</c>
/// —— 由 <c>CreatureCmd.cs:411</c> 在<b>每次实际造成伤害</b>时调入。
/// 关键是它同时给了 <c>target</c>（打到谁）和 <c>cardSource</c>（哪张卡造成的）。</para>
///
/// <para><b>安全策略</b>：与其它补丁一致 —— 全程反射、参数用 <c>__args</c> 取，
/// <b>不引用游戏程序集类型</b>。游戏改版时 <c>HarmonyPrepare</c> 返回 false ⇒ 静默失效。</para>
/// </summary>
[HarmonyPatch]
internal static class CardHitVfxPatch
{
    // ⚠️ 命名空间是 Hooks（不是 Models）—— 2026-10-12 实测：写成 Models 会让 TypeByName 返回 null，
    //    HarmonyPrepare 返回 false，整个补丁【静默不挂载】，症状是"命中层永远不触发"。
    //    文件位置：_decomp\sts2_full\MegaCrit.Sts2.Core.Hooks\Hook.cs
    private const string TargetTypeName = "MegaCrit.Sts2.Core.Hooks.Hook";
    private const string TargetMethodName = "AfterDamageGiven";

    /// <summary>出问题时的总开关。false ⇒ 直接早退。</summary>
    internal const bool Enabled = true;

    private static MethodInfo? _target;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        try
        {
            Type? t = HarmonyLib.AccessTools.TypeByName(TargetTypeName);

            if (t == null)
            {
                return false;
            }

            // 按"参数个数 + 名字"挑重载（不硬编码参数类型名，少一个会漂移的字符串）
            foreach (MethodInfo m in HarmonyLib.AccessTools.GetDeclaredMethods(t))
            {
                if (m.Name == TargetMethodName && m.GetParameters().Length == 7)
                {
                    _target = m;
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _target;

    /// <summary>
    /// <c>__args</c> 顺序（见 <c>Hook.AfterDamageGiven</c> 签名）：
    /// <c>[0]=choiceContext [1]=combatState [2]=dealer [3]=results [4]=props [5]=target [6]=cardSource</c>。
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(object[] __args)
    {
        if (!Enabled || __args == null || __args.Length < 7)
        {
            return;
        }

        try
        {
            object? dealer = __args[2];
            object? target = __args[5];
            object? cardSource = __args[6];

            // ★ 诊断（用户 2026-10-12 实测："敌人身上没有东西"）：先证明钩子到底有没有被调到、
            //   cardSource 到底是什么。没有这行日志，就只能猜"钩子没挂上"还是"cardSource 不对"。
            //   代价：每次造成伤害一行日志（只在战斗里，可接受；定位完就删）。
            string sourceName = cardSource?.GetType().Name ?? "<null>";

            Art.CardFieldVfx.NoteDamageHook(sourceName);

            if (target == null || cardSource == null)
            {
                return;
            }

            Art.CardFieldVfx.OnDamageGiven(cardSource, dealer, target);
        }
        catch
        {
            // 游戏流程里绝不外抛（工程铁律）。
        }
    }
}
