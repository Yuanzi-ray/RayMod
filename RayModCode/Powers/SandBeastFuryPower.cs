using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见技能「沙地兽之怒」的载体：<b>本回合每召唤一只沙地兽，+1 点力量</b>（回合结束自动还回去）。
///
/// <para><b>为什么不用 <c>TemporaryStrengthPower</c> 基类</b>（原版"临时力量"的标准做法）：
/// 那个基类把<b>自己的 <c>Amount</c></b> 当作"要还回去的力量总量"，
/// 而本牌在打出的瞬间<b>一点力量都不给</b>（用户 2026-10-03 拍板：只算打出<b>之后</b>的召唤）。
/// 用基类就必须以 <c>Amount = 1</c> 起步（凭空多给 1 点），或者把 Amount 改成 0
/// —— 而 <c>Amount</c> 归 0 会被 <c>PowerCmd.ModifyAmount</c> 判定为"该移除"，Power 直接消失。
/// 所以这里自己记 <c>_bonus</c>，Amount 恒为 1（只为让 Power 存在），力量收支自己做。</para>
///
/// <para>力量的加减照抄 <c>TemporaryStrengthPower</c> 的做法：加的时候 <c>silent: true</c>
/// （不要让力量牌再弹一次动画），结束时<b>按记下的 <c>_bonus</c> 原样扣回</b>
/// —— 用记账值而不是重新计算，这样中途被别的效果改过力量也不会算错。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastFuryPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SandBeastFuryPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示本回合已经加出去多少点力量。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>角标 = 已累计的力量（不是 <c>Amount</c>）。</summary>
    public override int DisplayAmount => _bonus;

    /// <summary>本 Power 已经加出去多少点力量（回合结束要按这个数扣回）。</summary>
    private int _bonus;

    private bool _subscribed;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        if (!_subscribed)
        {
            DepthEvents.RegisterSummoned(OnSummoned);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 一定要在这里注销：订阅是挂在<b>静态</b>总线上的，
    /// 不注销的话这张牌的效果会在 Power 退场后继续生效（而且不报错）。
    /// </summary>
    public override Task AfterRemoved(Creature oldOwner)
    {
        DepthEvents.UnregisterSummoned(OnSummoned);
        _subscribed = false;

        return Task.CompletedTask;
    }

    private void OnSummoned(Player player, int count, CardModel? cardSource)
    {
        if (count <= 0)
        {
            return;
        }

        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        // 事件总线是同步的，改力量是异步的 —— 按 DepthEvents 的约定自己派发。
        _ = TaskHelper.RunSafely(Grow(count));
    }

    private async Task Grow(int count)
    {
        if (base.Owner == null)
        {
            return;
        }

        _bonus += count;

        await PowerCmd.Apply<StrengthPower>(
            new ThrowingPlayerChoiceContext(), base.Owner, count, base.Owner, null, silent: true);

        InvokeDisplayAmountChanged();
    }

    public override async Task AfterSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        if (base.Owner == null || !participants.Contains(base.Owner))
        {
            return;
        }

        if (_bonus > 0)
        {
            await PowerCmd.Apply<StrengthPower>(
                choiceContext, base.Owner, -_bonus, base.Owner, null);
        }

        await PowerCmd.Remove(this);
    }
}
