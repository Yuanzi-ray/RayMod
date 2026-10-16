using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「前行」的载体：<b>每回合开始时层数 +Amount</b>（打出时 Amount = 5，升级版 6）。
///
/// <para>叠加：<c>PowerStackType.Counter</c>，再打一张就是 Amount 相加（每回合 +10）。
/// 数值直接存在 Amount 里，所以升级差异由卡牌在 <c>OnPlay</c> 时传进来的数字决定，
/// 本 Power 不需要知道"是哪张卡"。与 <see cref="RayMod.Depth.DepthPower"/> 同一套写法。</para>
///
/// <para>⚠️ 层数只能走 <see cref="DepthCmd"/>（唯一入口）—— 真实层数存在跑团级存档里，
/// 直接改 Power 的 Amount 不会同步。</para>
/// </summary>
[RegisterPower]
public sealed class ForwardPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(ForwardPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每回合加多少层。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // 本 Power 永远挂在玩家自己的生物上，直接比对这个 player 就够（照抄 SandBeastQuotaPower）。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        int gain = (int)base.Amount;

        if (gain <= 0)
        {
            return;
        }

        await DepthCmd.AddDepth(choiceContext, player, gain);
    }
}
