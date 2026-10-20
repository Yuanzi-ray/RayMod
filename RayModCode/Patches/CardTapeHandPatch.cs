using System;
using System.Reflection;
using HarmonyLib;
using Godot;

namespace RayMod.Patches;

/// <summary>
/// 「灵异录像带」手牌表现的反射目标（<c>NHandCardHolder</c>）。
/// <para>⚠️ 一个 <c>[HarmonyPatch]</c> 类<b>只能有一个</b> <c>[HarmonyTargetMethod]</c>
/// —— 两个方法想挂两个不同的目标时，必须拆成两个类（这里就是拆开的）。</para>
/// </summary>
internal static class CardTapeTargets
{
    internal static Type? HolderType;
    internal static MethodInfo? SetTargetAngle;
    internal static MethodInfo? Hover;

    internal static bool Resolve()
    {
        if (HolderType != null)
        {
            return SetTargetAngle != null && Hover != null;
        }

        HolderType = AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder");

        if (HolderType == null)
        {
            return false;
        }

        SetTargetAngle = HolderType.GetMethod("SetTargetAngle", new[] { typeof(float) });

        Hover = HolderType.GetMethod(
            "DoCardHoverEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(bool) },
            null);

        try
        {
            RayMod.Art.CardFieldVfx.LogTapeProbe(HolderType != null, SetTargetAngle != null, Hover != null);
        }
        catch
        {
            // 忽略
        }

        return SetTargetAngle != null && Hover != null;
    }
}

/// <summary>
/// <b>给手牌的目标角叠上「东倒西歪」的偏移。</b>
///
/// <para>目标：<c>NHandCardHolder.SetTargetAngle(float)</c>（游戏每次要摆牌时调）。</para>
///
/// <para><b>为什么改目标角而不是改 <c>RotationDegrees</c></b>：<c>AnimAngle</c> 是每帧
/// <c>RotationDegrees = Lerp(RotationDegrees, _targetAngle, dt*10)</c>，
/// 并且贴近目标 0.1° 就 <c>return</c> 停止追击 —— 直接写实际角度会被下一帧抹掉，
/// 改目标角才是"正门"：游戏自己的补间会把牌平滑地转过去。</para>
/// </summary>
[HarmonyPatch]
internal static class CardTapeAnglePatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardTapeTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardTapeTargets.SetTargetAngle;

    [HarmonyPostfix]
    private static void Postfix(object __instance, float __0)
    {
        try
        {
            if (__instance is Control holder)
            {
                RayMod.Art.CardFieldVfx.NoteTapeHolder(holder, __0);
            }
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }
}

/// <summary>
/// <b>鼠标掠过时晃一下。</b>
///
/// <para>目标：<c>DoCardHoverEffects(bool isHovered)</c>。用它当"掠过"信号，
/// 之后 1.2 秒内目标角叠衰减正弦（±10°，约 3Hz）。</para>
/// </summary>
[HarmonyPatch]
internal static class CardTapeHoverPatch
{
    [HarmonyPrepare]
    private static bool Prepare() => CardTapeTargets.Resolve();

    [HarmonyTargetMethod]
    private static MethodBase? Target() => CardTapeTargets.Hover;

    [HarmonyPostfix]
    private static void Postfix(object __instance, bool __0)
    {
        try
        {
            if (__instance is Control holder)
            {
                RayMod.Art.CardFieldVfx.NoteTapeHover(holder, __0);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
