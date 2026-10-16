using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见技能「探照灯」的载体：<b>下个回合不受层数负面效果</b>（五条全免，见
/// <see cref="RayMod.Depth.DepthPenalty"/>）。<c>Amount</c> = 还剩几个受保护的回合。
///
/// <para><b>为什么不在打出当回合就生效</b>：设计稿写的是"打出后<b>下回合</b>不受影响"。
/// 打出当回合的层数结算在<b>回合开始时</b>就已经做完了（卡是之后才打出的），
/// 唯一还活着的当回合效果是"30 层起少召唤一只"—— 当回合就生效会白送这个豁免。</para>
///
/// <para><b>怎么做到"只保护下回合"而不用赌钩子顺序</b>：本 Power <b>不修改</b>任何层数逻辑，
/// 只在打出的那一回合<b>结束</b>时把自己"武装"起来（<c>IsArmed = true</c>），
/// 于是整个下一回合里 <c>DepthPenalty.IsSuppressed</c> 都是 true；
/// 受保护的那个回合结束时再消耗一个 <c>Amount</c>，归零就摘掉自己。
/// 全程只依赖"回合结束"这一个时机，和 <c>DepthPower</c> 的"回合开始"不冲突。</para>
///
/// <para>多次打出：<c>Amount</c> 相加（Counter 叠加），也就是攒几个回合的保护。</para>
/// </summary>
[RegisterPower]
public sealed class SearchlightPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SearchlightPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示还剩几个受保护的回合。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>是否已经进入"受保护的那个回合"（打出的当回合为 false）。</summary>
    public bool IsArmed { get; private set; }

    public override async Task AfterSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (base.Owner == null || !participants.Contains(base.Owner))
        {
            return;
        }

        if (!IsArmed)
        {
            // 打出当回合结束：武装。下一回合的层数负面效果全部豁免。
            IsArmed = true;
            return;
        }

        // 受保护的回合结束：消耗掉一个保护回合。
        await PowerCmd.ModifyAmount(choiceContext, this, -1m, applier: null, cardSource: null);

        if (Amount <= 0)
        {
            await PowerCmd.Remove(this);
        }
    }
}
