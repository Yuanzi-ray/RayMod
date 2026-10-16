namespace RayMod.Ammo;

/// <summary>
/// <b>「给弹药上限加成」的遗物接口</b>（2026-10-12 新增，为遗物「弩」而设）。
///
/// <para>弹药上限的唯一真值在 <see cref="AmmoState"/>：每场战斗由
/// <see cref="AmmoCmd.EnsurePower"/> 调 <c>ResetForCombat</c> 复位。而"遗物要给上限加成"
/// 这件事<b>不能写死在弹药层里</b>（那等于让 Ammo 认识某一块具体遗物）——
/// 所以留一个标记接口：<b>凡是实现了它的遗物，其 <see cref="AmmoCapBonus"/> 会被
/// <see cref="AmmoState.BaseCap"/> 累加</b>。</para>
///
/// <para>与 <c>IDepthCard</c> / <c>IAmmoCard</c> / <c>ISandBeastCard</c> 同一套思路
/// （判据只写一处，靠运行时类型而不是手工表）。</para>
///
/// <para>⚠️ 加成的语义是"<b>基础上限</b>+N"，不是"本场上限+N"：
/// 基础上限每场战斗复位都会重新算一遍，所以它天然是<b>永久</b>的；
/// 而战斗内被卡牌改过的 <c>Cap</c>（「扩张」/「无限」/「重大升级」）仍然只活一场。</para>
/// </summary>
public interface IAmmoCapModifier
{
    /// <summary>该遗物给弹药<b>基础上限</b>的加成（可以是 0）。</summary>
    int AmmoCapBonus { get; }
}
