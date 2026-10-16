using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Powers;

namespace RayMod.Depth;

/// <summary>
/// 「有层数限制的牌」的统一标记接口（用户 2026-10-03 拍板 Q16）。
///
/// <para><b>为什么需要它</b>：绝不能拿"变量名"或"某张名单"当分类判据 ——
/// 用户已经踩过一次同类坑（「口径扩大」原来靠变量名认弹药牌，漏了 8 张而且<b>不报错</b>，
/// 见交接文档坑 29）。所以这里用一个<b>类型</b>做判据，漏了编译器/肉眼都能发现。</para>
///
/// <para>实现这个接口的牌应当是 <see cref="DepthGatedCard"/> 的子类
/// （基类会自动把接口实现上），不要手写 <c>IsPlayable</c>。</para>
/// </summary>
public interface IDepthGated
{
}

/// <summary>
/// 层数门槛的<b>唯一入口</b>（「探井人」的挂钩点）。
///
/// <para>所有"层数不满足就不能打出"的牌都必须走
/// <see cref="CanPlay"/> + <see cref="MarkPlayed"/>（或者直接继承
/// <see cref="DepthGatedCard"/>，基类会把这两步做好）。</para>
///
/// <para><b>为什么要分成"查询"和"消费"两步</b>：
/// <c>IsPlayable</c> 会被 UI <b>反复调用</b>（决定牌是否灰掉）。
/// 如果在那里就把豁免吃掉，界面刷新一次豁免就没了 —— 玩家根本没打出牌。
/// 所以查询只"看一眼"，真正的消费发生在 <b>OnPlay</b> 里。</para>
///
/// <para>副作用（有意为之）：豁免还在时，<b>所有</b>层数门槛牌都会显亮；
/// 玩家打出其中<b>第一张</b>时豁免被吃掉，其余的立刻变灰。这正好是设计要的语义。</para>
/// </summary>
public static class DepthGate
{
    /// <summary>
    /// 供 <c>IsPlayable</c> 用：<b>只查询，绝不消耗豁免</b>。
    /// </summary>
    /// <param name="conditionMet">这张牌自己的层数条件是否满足。</param>
    public static bool CanPlay(Player? player, bool conditionMet)
    {
        if (conditionMet)
        {
            return true;
        }

        // ★ 2026-10-16：判据从"本回合用没用过"（bool）改成"还剩没用完"（读 Amount）。
        //   「探井人」写的是"前 2 张"，用 bool 会永远只放行 1 张（玩家实机反馈的真 bug）。
        return GetWaiver(player) is { HasWaiverLeft: true };
    }

    /// <summary>
    /// 真正打出时调用：只有"条件本来不满足、靠豁免才进来的"才消耗一次配额。
    /// </summary>
    /// <param name="conditionMet">这张牌自己的层数条件是否满足。</param>
    public static void MarkPlayed(Player? player, bool conditionMet)
    {
        if (conditionMet)
        {
            return;
        }

        if (GetWaiver(player) is { HasWaiverLeft: true } waiver)
        {
            waiver.MarkUsed();
        }
    }

    /// <summary>玩家身上的「探井人」豁免（没有该 Power 时为 null）。</summary>
    private static ExploratoryWellDrillerPower? GetWaiver(Player? player) =>
        player?.Creature.GetPower<ExploratoryWellDrillerPower>();
}
