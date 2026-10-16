using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Powers;

namespace RayMod.Depth;

/// <summary>
/// 「本回合是否豁免层数负面效果」的统一判定（「探照灯」）。
///
/// <para><b>用户 2026-10-03 拍板（Q8）：5 条层数负面效果全免</b>，也就是
/// 下面这五处全部要问一句 <see cref="IsSuppressed"/>：</para>
/// <list type="number">
/// <item>10 层起：沙地兽只活 3 回合（<c>SandBeast.AfterPlayerTurnStart</c>）</item>
/// <item>20 层起：无沙地兽时，<b>回合结束时</b>受到 6 点伤害（<c>DepthPower.AfterSideTurnEnd</c>）</item>
/// <item>30 层起：召唤沙地兽少 1 只（<c>BeastCmd.Summon</c>）</item>
/// <item>51 层起：<b>回合结束时</b>额外受到 7 点伤害 <b>且不能再召唤</b>
/// （<c>DepthPower.AfterSideTurnEnd</c> / <c>BeastCmd.Summon</c>）</item>
/// <item>101 层起连续 3 回合：直接死亡（<c>DepthPower.AfterPlayerTurnStart</c>）</item>
/// </list>
///
/// <para>想加"新的层数负面效果"时，<b>必须同时在这里落下判定</b>，
/// 否则探照灯会静默漏掉它（不报错 —— 和坑 29 同一类问题）。</para>
///
/// <para><b>2026-10-04 平衡调整</b>：第 2、4 条（两条掉血）从"回合开始 + 不可格挡"
/// 改成了"回合结束 + 可格挡"。</para>
///
/// <para>⚠️ <b>锁存约定（改这两条的人必读）</b>：<c>DepthPower</c> <b>不在回合结束现场调用
/// <see cref="IsSuppressed"/></b>，而是在<b>回合开始时</b>把结果锁存进
/// <c>DepthPower.Data.SuppressedThisTurn</c>、回合结束读那个快照。
/// 原因是「探照灯」（<c>SearchlightPower</c>）<b>也在回合结束</b>消耗并可能移除自己，
/// 两者是同一时机上的两个钩子、执行顺序不受控制 —— 现场查会读到"已被摘掉"的状态，
/// 于是受保护的回合照样掉血，且<b>静默不报错</b>。
/// 所以本类的语义在这里是"每回合开局问一次、整回合沿用"，不是"每次用时现问"。</para>
/// </summary>
public static class DepthPenalty
{
    /// <summary>本回合是否豁免层数负面效果。</summary>
    public static bool IsSuppressed(Player? player) =>
        player?.Creature.GetPower<SearchlightPower>() is { IsArmed: true };
}
