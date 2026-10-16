namespace RayMod.Depth;

/// <summary>
/// 层数（矿井层数）机制的全部数值与阈值。
///
/// 这个文件是唯一的"配平表"：以后再调数值，只改这里，不要去动逻辑代码。
/// </summary>
public static class DepthConstants
{
    // ===================== 层级边界 =====================
    // 每档的语义：[Min, Max]，两端都含（见各自的注释）。
    //
    // 设计稿原文（机制文档）：
    //   30-60 层：战斗结束时，本场战斗每有一回合，获得 10 金币。
    //   大于 60 层：战斗结束时，50% 概率获得一个遗物。
    // → 分界点就是 **60**，没有缝隙也没有重叠。下面两个常量都按 60 对齐。

    /// <summary>10-19 层：攻击额外伤害。</summary>
    public const int AttackBonusMinDepth = 10;

    /// <summary>20-29 层：首次进入给一瓶药水；无沙地兽时每回合自伤。</summary>
    public const int PotionTierMinDepth = 20;

    /// <summary>30 层起：战斗结束按回合给金币。</summary>
    public const int GoldTierMinDepth = 30;

    /// <summary>
    /// 金币档的上边界（到 60 为止，<b>含</b> 60）。
    ///
    /// <para>⚠️ 2026-10-03 修正：这里原本是 65，导致两个问题 ——
    /// 61~65 层既给金币（因为金币判定只有下界没有上界）又给遗物（因为遗物判定是 &gt;65，
    /// 等价于 66 起，与设计稿的"大于 60"差了 5 层）。
    /// 现在统一按设计稿的 <b>60</b> 对齐：
    /// 30..60 给金币；<b>61 起只给遗物、不再给金币</b>。</para>
    /// </summary>
    public const int GoldTierMaxDepth = 60;

    /// <summary>
    /// 大于该层数：战斗结束 50% 概率给遗物。
    /// 与设计稿"大于60层"对齐（即 61 层起），并接着金币档的上界，没有缝隙也没有重叠。
    /// </summary>
    public const int RelicChanceDepth = 60;

    /// <summary>大于该层数：不能再召唤沙地兽，且每回合额外受伤。</summary>
    public const int NoSummonDepth = 50;

    /// <summary>连续这么多回合超过 <see cref="DeathDepth"/> 层则直接死亡。</summary>
    public const int DeathConsecutiveTurns = 3;

    /// <summary>死亡判定阈值（大于该值才计数）。</summary>
    public const int DeathDepth = 100;

    // ===================== 各档数值 =====================

    /// <summary>10-19 层：攻击额外造成的伤害。</summary>
    public const decimal AttackBonusDamage = 2m;

    /// <summary>
    /// 20 层起：没有沙地兽时，<b>回合结束时</b>受到的伤害（<b>可被格挡</b>）。
    ///
    /// <para>2026-10-04 平衡调整：触发时机由"回合开始"改为"回合结束"，
    /// 性质由不可格挡改为可格挡。详见 <c>DepthPower.AfterSideTurnEnd</c>。</para>
    /// </summary>
    public const decimal NoMinionSelfDamage = 6m;

    /// <summary>
    /// 大于 50 层：每回合结束时<b>额外</b>受到的伤害（<b>可被格挡</b>）。
    ///
    /// <para>与 <see cref="NoMinionSelfDamage"/> <b>叠加</b>：51 层以上若无沙地兽，一回合共 13 点。</para>
    ///
    /// <para>2026-10-04 平衡调整：<b>数值 5 → 7</b>，并且和上面那条一样
    /// 改成"回合结束触发 + 可格挡"。数值提高是对"可格挡化"的补偿 ——
    /// 这条是<b>无条件</b>的（不看有没有沙地兽），不能因为可格挡就变得无痛。</para>
    /// </summary>
    public const decimal HighDepthSelfDamage = 7m;

    /// <summary>30-65 层：战斗结束时每回合获得的金币。</summary>
    public const decimal GoldPerTurn = 10m;

    /// <summary>大于 65 层：战斗结束时获得遗物的概率。</summary>
    /// <remarks>
    /// 2026-10-04：验收「遗物档走奖励界面」时曾**临时改成 1.0**，
    /// 验收通过（5 次掷骰 5 次命中、奖励项全部正常领取）后**已改回 0.5**。
    /// 设计稿的数值就是 50%，别再动它。
    /// </remarks>
    public const double RelicChance = 0.5;

    // ===================== 沙地兽的层数联动 =====================
    //
    // ⚠️ 2026-10-03 用户拍板：这两条是**阈值型（从该层起持续生效）**，不是"仅该档"。
    //    口径与"20 层起无沙地兽每回合 -6HP"一致（那条原本写在 20-29 档里，
    //    用户确认要按"20 层起持续"实现）。设计稿的档位写法有歧义，别再按档位改回去。
    //
    // 沙地兽自己的数值（印记 10%、上限 2/6 的用法等）在 RayMod.Beasts.BeastConstants。

    /// <summary>10 层起：召唤的沙地兽只能持续 <see cref="MinionLifespanAtDepth10"/> 个回合。</summary>
    public const int MinionLifespanMinDepth = 10;

    /// <summary>30 层起：召唤沙地兽时少召唤 <see cref="MinionSummonPenaltyAtDepth30"/> 只。</summary>
    public const int MinionSummonPenaltyMinDepth = 30;

    /// <summary>10 层起：召唤的沙地兽能持续的回合数。</summary>
    public const int MinionLifespanAtDepth10 = 3;

    /// <summary>30 层起：召唤沙地兽时少召唤的数量。</summary>
    public const int MinionSummonPenaltyAtDepth30 = 1;

    /// <summary>沙地兽的初始上限。</summary>
    public const int MinionCapInitial = 2;

    /// <summary>沙地兽的上限硬上限。</summary>
    public const int MinionCapMax = 6;
}
