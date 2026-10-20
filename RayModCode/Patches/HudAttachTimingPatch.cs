using System;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace RayMod.Patches;

/// <summary>
/// ★★ 2026-10-12 <b>让层数 / 弹药计数器【在战斗一开始就出现】</b>（用户反馈："他们总是慢于能量出现"）。
///
/// <para><b>原因</b>：计数器节点是在 <c>ResourceHudCounter.Push()</c> 里创建的
/// （<c>Push → Attach → Build</c>），而 <c>Push</c> 要等数值第一次同步（`DepthCmd` / `AmmoCmd`）——
/// 通常是打出第一张牌、或回合开始结算时。<b>而能量球是角色场景自带的，战斗一开就在</b>，
/// 于是两个计数器永远"迟到"一块。</para>
///
/// <para><b>修法</b>：挂 <c>NCombatRoom._Ready</c> 的 Postfix，
/// <b>延后一帧</b>（等 <c>Ui.EnergyCounterContainer</c> 建好）调 <c>ResourceHud.EnsureAttached()</c>。
/// 此时数值可能还没同步，先显示 <c>0</c> 占位 —— 真实值到来时 <c>Push</c> 会覆盖它。</para>
///
/// <para>⚠️ 为什么要延后一帧：<c>_Ready</c> 里 UI 可能还没建完（本项目在"启动台/角色选择"上踩过同类坑）。</para>
/// </summary>
[HarmonyPatch]
internal static class HudAttachTimingPatch
{
    private const string TargetTypeName = "MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom";
    private const string TargetMethodName = "_Ready";

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

            _target = HarmonyLib.AccessTools.Method(t, TargetMethodName, Type.EmptyTypes);

            return _target != null;
        }
        catch
        {
            return false;
        }
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _target;

    [HarmonyPostfix]
    private static void Postfix(object __instance)
    {
        if (!Enabled || __instance is not Node node)
        {
            return;
        }

        try
        {
            // 延后一帧：_Ready 里 Ui.EnergyCounterContainer 可能还没建好。
            node.CallDeferred(nameof(AttachDeferred), node);

            // 再延一次（有些布局是两帧才稳）
            var t = node.GetTree()?.CreateTimer(0.15, processAlways: false);

            if (t != null)
            {
                t.Timeout += () => { try { Hud.ResourceHud.EnsureAttached(); } catch { } };
            }
        }
        catch
        {
            // 游戏流程里绝不外抛（工程铁律）。
        }
    }

    /// <summary>被 <c>CallDeferred</c> 调到的静态入口。</summary>
    private static void AttachDeferred(Node _)
    {
        try
        {
            Hud.ResourceHud.EnsureAttached();
        }
        catch
        {
            // 静默
        }

        // ★ 2026-10-20：顺手确认常驻心跳还在。它挂在 SceneTree.Root 上，正常不该丢，
        //   但这里每进一次战斗房都便宜地补一次（幂等），省得"某次进房之后再没挂上"。
        try
        {
            Art.CardFieldVfx.EnsurePersistBeat();
        }
        catch
        {
            // 静默
        }
    }
}
