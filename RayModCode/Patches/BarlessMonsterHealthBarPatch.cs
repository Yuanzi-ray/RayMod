using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace RayMod.Patches;

/// <summary>
/// <b>把「声明自己不显示血条」的生物的血条重新藏起来</b>（沙地兽用）。
///
/// <para><b>为什么需要这个补丁</b>（2026-10-04 调查结论，逐行对得上）：</para>
/// <list type="number">
/// <item><see cref="RayMod.Beasts.SandBeast"/> 写了 <c>IsHealthBarVisible =&gt; false</c>，
/// 但整个游戏里这个属性的消费点<b>只有一处</b>：<c>NCreature.cs:196</c> 的
/// <c>ToggleIsInteractable(Entity.Monster.IsHealthBarVisible)</c>，
/// 而它内部是 <c>_stateDisplay.Visible = !NCombatUi.IsDebugHidingHpBar &amp;&amp; on</c>（<c>NCreature.cs:562</c>）。</item>
/// <item><b>MinionLib 0.6.3</b> 的 <c>MinionInteractablePatch2</c> 在同一个方法上做 Prefix，
/// 只要 <c>Entity.Monster is MinionModel</c> 且宠物主人是自己，就把参数 <c>on</c> 强写成 <c>true</c>
/// —— 于是上一步算出的是 <c>Visible = true</c>，血条照旧显示。
/// （它的本意是"让召唤物保持可点选/可悬停"，顺手把这一次隐藏调用反转了。）</item>
/// </list>
///
/// <para><b>为什么用 Postfix</b>：Harmony 的顺序是"所有 Prefix → 原方法体 → 所有 Postfix"，
/// 所以 Postfix <b>必然</b>在 MinionLib 那个 Prefix 之后跑，不依赖 <c>HarmonyPriority</c> 这种隐式契约。
/// 而且它覆盖<b>全部</b>调用点：<c>NCreature._Ready</c>、<c>NCombatRoom.AddCreature</c>（召唤新宠物时
/// 会把旧的重新点亮，<c>NCombatRoom.cs:570-575</c>）、<c>TransitionToActiveCombat</c> 都算。</para>
///
/// <para>⚠️ <b>判据只用 <c>IsHealthBarVisible</c></b>：对敌人和原版宠物零影响
/// （它们要么返回 true，要么根本不是 MinionModel），语义与游戏原生完全一致 ——
/// 也就是让 <c>SandBeast.cs</c> 那行 override 恢复它本来该有的作用。
/// <c>IsInteractable</c> 仍然为 true，所以沙地兽照样可以点选/悬停，MinionLib 的意图不被破坏。</para>
///
/// <para>⚠️ 不要用 <c>IsNodeReady()</c> 做守卫：最关键的那次调用（<c>NCreature.cs:196</c>）就发生在
/// <c>_Ready</c> <b>内部</b>，那时 <c>IsNodeReady()</c> 还是 false，加了判断反而会漏掉主因。</para>
/// </summary>
[HarmonyPatch(typeof(NCreature), nameof(NCreature.ToggleIsInteractable))]
internal static class BarlessMonsterHealthBarPatch
{
    private static bool _loggedFailure;

    [HarmonyPostfix]
    private static void Postfix(NCreature __instance)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(__instance))
            {
                return;
            }

            var monster = __instance.Entity?.Monster;

            // 只关"显式声明自己不显示血条"的生物；敌人与 Osty/Byrdpip 那些不受影响。
            if (monster == null || monster.IsHealthBarVisible)
            {
                return;
            }

            var display = __instance.GetNodeOrNull<NCreatureStateDisplay>("%HealthBar");

            if (display != null && display.Visible)
            {
                display.Visible = false;
            }
        }
        catch (Exception e)
        {
            // 补丁里抛异常会污染整个调用点：只记一次日志，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Entry.Logger.Error($"[血条] BarlessMonsterHealthBarPatch 失败（只记这一次）：{e}");
            }
        }
    }
}
