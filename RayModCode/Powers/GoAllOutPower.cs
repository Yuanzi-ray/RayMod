using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见技能「全力以赴」的载体：<b>本回合从"其他牌"获得的格挡 -80%</b>（升级 -70%）。
/// <c>Amount</c> = 削减的百分比（80 / 70）。
///
/// <para><b>口径</b>（用户 2026-10-03 拍板 Q9）：只减<b>牌</b>给的格挡 ——
/// 能力/遗物给的格挡不带 <c>cardSource</c>（为 null），一律不减。
/// 并且是<b>本回合</b>：回合结束时自己摘掉。</para>
///
/// <para>钩子是 <c>AbstractModel.ModifyBlockMultiplicative</c>（原版 <c>FrailPower</c> ×0.75、
/// <c>UnmovablePower</c> ×2 的同一套）。⚠️ 三种修改是<b>相乘</b>叠加的（外加/乘法/封顶的顺序见
/// <c>Hook.cs</c>），所以这里返回的是"倍率"不是"扣除量"。</para>
///
/// <para>「全力以赴」<b>自己</b>那笔格挡（等于层数）不减 —— 靠 <c>cardSource</c> 认出自己。
/// 一旦哪天把这张牌改名/换成别的卡来给格挡，这个判断就要跟着改。</para>
/// </summary>
[RegisterPower]
public sealed class GoAllOutPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(GoAllOutPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示削减的百分比。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override decimal ModifyBlockMultiplicative(
        Creature target, decimal block, ValueProp props, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (base.Owner != target)
        {
            return 1m;
        }

        // 不是"牌"给的格挡（遗物 / 能力 / 层数）不减。
        if (cardSource == null)
        {
            return 1m;
        }

        // 这张牌自己的格挡不减。
        if (cardSource is RayMod.Cards.GoAllOut)
        {
            return 1m;
        }

        // 只对"牌给的格挡"生效（照抄 FrailPower 的守卫）。
        if (!props.IsPoweredCardOrMonsterMoveBlock())
        {
            return 1m;
        }

        decimal percent = Math.Clamp(base.Amount, 0m, 100m);

        return 1m - percent / 100m;
    }

    public override async Task AfterSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (base.Owner != null && participants.Contains(base.Owner))
        {
            await PowerCmd.Remove(this);
        }
    }
}
