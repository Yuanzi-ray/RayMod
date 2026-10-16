namespace RayMod.Beasts;

/// <summary>
/// 沙地兽机制的数值配平表。
///
/// <para><b>与层数有关的阈值/数值不在这里</b> —— 它们在
/// <see cref="Depth.DepthConstants"/>（那里是层数的唯一来源，本项目的老规矩是
/// "一个机制的数值只写一处"）。本文件只放沙地兽自己的数值。</para>
///
/// <para>用户 2026-10-03 拍板的规则见 <see cref="SandBeast"/> / <see cref="BeastCmd"/> 的注释。</para>
/// </summary>
public static class BeastConstants
{
    /// <summary>印记每层让敌人受到的攻击伤害提高的比例（10%）。</summary>
    public const decimal MarkBonusPerStack = 0.10m;

    /// <summary>
    /// 「沙地兽与她」：印记效果 +20%。
    ///
    /// <para>作用在<b>每层</b>上（10% → 12%），不是作用在总倍率上
    /// （用户 2026-10-03 确认："把每层印记从 10% 提到 12%"）。</para>
    /// </summary>
    public const decimal MarkBonusAbilityMultiplier = 1.2m;

    /// <summary>
    /// 沙地兽的血量。
    ///
    /// <para><b>必须是 1，不是大数</b> —— 2026-10-03 实测：<b>这个游戏用血量驱动模型大小</b>，
    /// 原先照抄 Byrdpip 写的占位值 9999 会把沙地兽放大成一个巨型怪物。</para>
    ///
    /// <para>"不会受伤"不靠血量，靠 <see cref="SandBeast.ShouldAllowHitting"/> 返回 false
    /// （原版 <c>Creature.IsHittable</c> 与 <c>CanReceivePowers</c> 都查那个钩子，
    /// 见 <c>Creature.cs:286,309</c>）。用户已实测：敌人不会对沙地兽造成伤害
    /// （宠物本来就不在敌人 move 的目标池里，见 <see cref="SandBeast"/> 的注释），
    /// 所以 1 点血是安全的。</para>
    /// </summary>
    public const int Hp = 1;

    /// <summary>
    /// 沙地兽的占位视觉：直接用原版自带的<b>兜底 creature 场景</b>。
    ///
    /// <para>原版 <c>MonsterModel.CreateVisuals()</c> 在加载自己的视觉失败时，
    /// 就是回退到这个场景（<c>MonsterModel.cs:30,292</c>），所以它<b>一定存在</b>，
    /// 是最安全的占位。等有正式美术时改成
    /// <c>creature_visuals/sand_beast</c> 并在 Godot 里正常导入。</para>
    /// </summary>
    public const string PlaceholderVisualsPath = "creature_visuals/fallback";
}
