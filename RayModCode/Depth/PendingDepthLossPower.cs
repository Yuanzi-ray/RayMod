using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Depth;

/// <summary>
/// 「深入」留下的欠债：接下来 N 个回合开始时，层数 -每回合扣掉的层数。
///
/// <para>之所以单独做成一个 Power 而不是在卡里记状态：卡牌打出后就进弃牌堆了，
/// 没有地方可以挂"还欠两个回合"这件事。Power 是原版唯一合适的定时器载体。</para>
///
/// <para><c>Amount</c> 在这里被当作<b>剩余回合数</b>用（StackType.Counter 天然支持），
/// 所以它必须保持 ≥1，否则会被原版的 amount 判定移除。层数本身不存在这个 Power 上。</para>
/// </summary>
[RegisterPower]
public sealed class PendingDepthLossPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(PendingDepthLossPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    /// <summary>
    /// 每回合扣掉的层数。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["LossPerTurn"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _lossPerTurn = 6;

    /// <summary>从打出它的卡上取真实值；取不到就沿用默认值。</summary>
    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        // 从打出它的卡上取真实值（卡面数字改了就真的生效）；取不到就沿用默认值。
        if (cardSource is { } src && src.DynamicVars.ContainsKey("LossPerTurn"))
        {
            _lossPerTurn = src.DynamicVars["LossPerTurn"].IntValue;
        }

        return Task.CompletedTask;
    }

    public override PowerType Type => PowerType.Debuff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override LocString Description
    {
        get
        {
            var loc = new LocString("powers", base.Id.Entry + ".description");
            if (loc.Exists())
            {
                DynamicVars.AddTo(loc);
                loc.Add("Amount", Amount);
            }

            return loc;
        }
    }

    protected override string SmartDescriptionLocKey => base.Id.Entry + ".description";

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // ★★ 2026-10-13 联机适配：**必须先判"这个回合是不是我的"**。
        //    钩子会按"每一个玩家的回合"对场上每个监听者各跑一遍（`Hook.AfterPlayerTurnStart`），
        //    而下面扣的是**钩子实参**那个玩家的层数 ⇒ 不判的话，A 身上的「滞留下坠」
        //    会在 **B 的回合**去扣 **B 的层数**，同时 A 的剩余回合数还按人数倍速递减。
        //    同型判据见 `AmmoPower.cs:214` / `FeranmutsPower.cs:142`。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player owner || owner != player)
        {
            return;
        }

        // 先扣层数。（用解析出来的 owner，不用钩子实参 —— 两者现在相等，但口径只有一处。）
        await DepthCmd.AddDepth(choiceContext, owner, -_lossPerTurn);

        // 再递减"剩余回合数"；归零就把自己摘掉。
        await PowerCmd.ModifyAmount(choiceContext, this, -1m, applier: null, cardSource: null);

        if (Amount <= 0)
        {
            await PowerCmd.Remove(this);
        }
    }
}
