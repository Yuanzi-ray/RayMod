using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Art;
using RayMod.Audio;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Patches;

// ─────────────────────────────────────────────────────────────────────────────
//  命中特效补丁（2026-10-08）
//
//  游戏自带的接口：`AttackCommand.WithHitFx(...)` 决定"打中时放什么特效"，
//  而 `AttackCommand.WithHitVfxNode(Func<Creature, Node2D?>)` 允许**挂我们自己的节点**
//  （`AttackCommand.cs:468`，节点会被加到目标生物的 VfxContainer 上，
//   见 `:624-633` 的 `singleTarget.GetVfxContainer()?.AddChildSafely(...)`）。
//
//  ★ 为什么需要**两个**补丁（2026-10-08 血泪）：
//    `AttackCommand.Execute` 是"**先挂我们的节点、再放 HitVfx**"，两个节点在同一个
//    CombatVfxContainer 里、**后加的在上层** —— 卡给的 `vfx/vfx_attack_blunt`（钝击）
//    会把我们的特效整个盖住。用户实测"开关没区别"，判断是"特效被游戏原生特效覆盖了"，
//    完全正确。
//    → 所以我们接管时必须**把 vfx 参数改写成 null**。
//
//  ⚠️⚠️ **绝对不要在 Postfix 里调 `__instance.WithHitFx(...)` 来清它** ——
//    `WithHitFx` 自己就是被补丁的方法，这么写会重进补丁、无限递归、**栈溢出直接把游戏搞崩**
//    （2026-10-08 实机：打出「得见光芒」瞬间崩溃，日志停在 playing card 那一行）。
//    正确姿势是 Prefix 用 `ref` 改写参数（见下面），根本不调用那个方法。
//
//  ⚠️ 全程 try/catch：特效绝不能影响战斗结算。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 给配了特效的卡，在命中时挂上莱伊的专属特效节点。
/// </summary>
[HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.WithHitFx))]
internal static class SkillHitVfxPatch
{
    private static readonly Logger Log = new("RayMod.Vfx", LogType.Generic);

    /// <summary>
    /// <b>Prefix：把游戏自带的命中特效让位给我们。</b>
    /// 当前正在结算的牌在 <see cref="HitVfxCatalog"/> 里有配特效时，把 <c>vfx</c> 参数改写成
    /// <c>null</c> —— 原方法照样会执行，于是 <c>HitVfx=null</c>（钝击不放了），
    /// 而 <c>sfx</c> / <c>tmpSfx</c> 原样保留（**只动 vfx 这一个参数，绝不碰音效**）。
    /// </summary>
    private static void Prefix(ref string? vfx)
    {
        try
        {
            if (vfx == null)
            {
                return;
            }

            var card = SpineAnimDispatch.CurrentCard;

            // ★★ 2026-10-12：**自绘炸开**的卡（Profiles 里 CustomBurst=true，例如「得见光芒」）
            //    必须**无条件**让出游戏自带的命中特效 —— 用户实测"游戏原特效还覆盖在上面"。
            //    ⚠️ 这一段要在 HitVfxEnabled 判断**之前**：那是老那条烘焙特效线的开关，
            //       自绘的卡不管它开没开，游戏那层钝击都得让位。
            if (card != null && CardFieldVfx.IsCustomBurstCard(card))
            {
                Log.Info($"[特效] 让位游戏自带的命中特效（自绘炸开）：{vfx} → null");
                vfx = null;
                return;
            }

            if (!VoiceState.HitVfxEnabled)
            {
                return;
            }

            if (card == null)
            {
                return;
            }

            // ★★ 2026-10-15 撞名门：按**简单类名**查表前必须先确认是本 mod 的卡
            //    （原版有同名卡：铁甲战士 `Feed` / 静默猎手 `Darkness`）。
            string? mapped = Cards.CardIdentity.NameForMapLookup(card);

            if (mapped == null || HitVfxCatalog.ForCardClass(mapped) == SkillHitVfx.None)
            {
                return;
            }

            Log.Info($"[特效] 让位游戏自带的命中特效：{vfx} → 改由莱伊特效接管");
            vfx = null;
        }
        catch
        {
            // 改写失败就保持游戏原本的命中特效，不抛。
        }
    }

    /// <summary>
    /// Postfix：挂上我们自己的特效节点。
    ///
    /// <para><b>⚠️ 这里禁止调用任何被补丁的方法</b>（尤其 `WithHitFx` 自己）—— 会无限递归崩溃。</para>
    /// </summary>
    private static void Postfix(AttackCommand __instance)
    {
        if (!VoiceState.HitVfxEnabled)
        {
            return;
        }

        try
        {
            // SpineAnimDispatch 在"出牌开始～出牌结束"这段窗口里记着正在结算的牌
            //（它本来就是为动画分派做的，这里顺手复用）。
            var card = SpineAnimDispatch.CurrentCard;

            if (card == null)
            {
                return;
            }

            // ★ 自绘炸开的卡不挂老那条烘焙特效（新特效已接管，两层叠在一起会糊）
            if (CardFieldVfx.IsCustomBurstCard(card))
            {
                return;
            }

            // ★★ 2026-10-15 撞名门（同上）。
            string? mapped = Cards.CardIdentity.NameForMapLookup(card);

            if (mapped == null)
            {
                return;
            }

            SkillHitVfx kind = HitVfxCatalog.ForCardClass(mapped);

            if (kind == SkillHitVfx.None)
            {
                return;
            }

            // ⚠️ 一定要把 target 传进去：WithHitVfxNode 只负责 AddChild，
            //    **不负责定位**（节点自己在 _Ready 里按 target 的 VfxSpawnPosition 摆位）。
            __instance.WithHitVfxNode(target => SkillHitVfxNode.Create(kind, target));
        }
        catch (Exception e)
        {
            // 挂不上就保持游戏原本的命中特效，不抛（特效绝不能影响结算）。
            Log.Warn($"[特效] 挂特效失败（本次保持游戏原特效）：{e.Message}");
        }
    }
}
