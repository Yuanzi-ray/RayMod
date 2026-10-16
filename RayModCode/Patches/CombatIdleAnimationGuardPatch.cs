using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using RayMod.Art;
using RayMod.Characters;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Patches;

/// <summary>
/// <b>战斗开场后确认莱伊真的在播 Idle；如果被谁重置了就补回来。</b>
///
/// <para><b>为什么需要它</b>：我们的动画器是在 <c>NCreature._Ready</c> <b>内部</b>建的
/// （<c>NCreature.cs:511</c> → <c>GenerateAnimator</c> → <c>SetupCustomCreatureAnimator</c>），
/// 而 <c>CreatureAnimator</c> 的构造函数<b>当场就把 Idle 播上了</b>
/// （<c>CreatureAnimator.cs:43</c> → <c>SetNextState</c> → <c>SetAnimation("Idle", true)</c>）。
/// 只要在 <c>_Ready</c> 跑完之后还有谁动一下骨架
/// （<c>MegaSprite.SetSkeletonDataRes</c>，或任何重置 animation state 的操作），
/// 这次播放就被抹掉，人物定格在绑定姿势 —— 用户看到的"<i>人物不动</i>"就是这个。</para>
///
/// <para><b>已确诊的那一次</b>：RitsuLib 的 <c>CharacterCombatSpineOverridePatch</c> 是
/// <c>NCreature._Ready</c> 的 Postfix，会在我们建完动画器之后换骨架。
/// 根因已从源头修掉（<see cref="RayModCharacter.CustomCombatSpineSkeletonDataPath"/> 现在返回 null），
/// 这个补丁留下当<b>安全网</b>：将来做换肤 UI 必然要在运行时换骨架，
/// 那时它会自动把 Idle 补回来，同时也把"补播过"这件事写进日志。</para>
///
/// <para><b>判据只要"当前动画不是 Idle"</b>，所以对原版角色/敌人零影响 ——
/// 本补丁只认 <see cref="RayModCharacter"/> 的持有者。</para>
///
/// <para>⚠️ <b>用 <c>Priority.Last</c></b>：Harmony 的 Postfix 按优先级从高到低执行，
/// <c>Last</c>(=0) 让本补丁排在 RitsuLib 那些默认优先级(400)的 Postfix <b>之后</b>，
/// 这样读到的才是"骨架换完之后"的真实状态。</para>
///
/// <para>⚠️ <b>不能直接用 <c>NCreature.SpineAnimation.GetCurrentAnimationName()</c></b>：
/// 它内部走 <c>MegaSprite.GetAnimationState()</c>，那个方法在骨架还没建好时会<b>直接抛异常</b>。
/// 必须先用 <c>IsAnimationStateReady()</c> 探路，再用 <c>TryGetAnimationState()</c>。</para>
/// </summary>
[HarmonyPatch(typeof(NCreature), "_Ready")]
internal static class CombatIdleAnimationGuardPatch
{
    private static readonly Logger AnimLog = new("RayMod.Characters", LogType.Generic);

    private static bool _loggedFailure;
    private static bool _loggedHealthyOnce;
    private static bool _loggedNoSpine;
    private static bool _loggedNotReady;

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(NCreature __instance)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(__instance))
            {
                return;
            }

            // 只管莱伊本人：敌人、原版角色、别的 mod 的角色一律不碰。
            if (__instance.Entity?.Player?.Character is not RayModCharacter)
            {
                return;
            }

            MegaSprite? spine = __instance.Visuals?.SpineBody;

            if (spine == null || !GodotObject.IsInstanceValid(spine.BoundObject))
            {
                // 没有 Spine 骨架 = 场景里的 skeleton_data_res 没加载出来（症状同样是"人物不动"），
                // 这种情况必须留痕，否则日志里会什么都看不到。
                if (!_loggedNoSpine)
                {
                    _loggedNoSpine = true;
                    AnimLog.Warn("[动画] 战斗里拿不到 SpineBody（骨架没加载）—— 人物会是静止的，检查场景的 skeleton_data_res");
                }

                return;
            }

            // 骨架还在异步加载中 → 既判断不了也补不了，交给下一次 _Ready（换场/复活会再走）。
            if (!spine.IsAnimationStateReady())
            {
                if (!_loggedNotReady)
                {
                    _loggedNotReady = true;
                    AnimLog.Warn("[动画] 战斗就绪后 animation state 仍未就绪 —— 无法确认 Idle 是否在播");
                }

                return;
            }

            string? current = spine.TryGetAnimationState()?.GetCurrentAnimationName();

            if (current == SpineAnims.Idle)
            {
                // 一条就够：确认"骨架没被重置"，同时给验收留一个可 grep 的证据。
                if (!_loggedHealthyOnce)
                {
                    _loggedHealthyOnce = true;
                    AnimLog.Info($"[动画] 战斗就绪后当前动画 = {current} ✅（骨架未被重置，人物应当在动）");
                }

                return;
            }

            // 走到这里 = 动画被抹了。补播一次，并把前后状态都记下来。
            __instance.SetAnimationTrigger(CreatureAnimator.idleTrigger);

            string? after = spine.TryGetAnimationState()?.GetCurrentAnimationName();

            AnimLog.Warn($"[动画] 战斗就绪后当前动画 = {current ?? "<null>"}（被重置了）" +
                         $" → 已补播 Idle，补播后 = {after ?? "<null>"}");
        }
        catch (Exception e)
        {
            // 补丁里抛异常会污染整个 _Ready：只记一次日志，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Entry.Logger.Error($"[动画] CombatIdleAnimationGuardPatch 失败（只记这一次）：{e}");
            }
        }
    }
}
