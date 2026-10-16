using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「无限子弹」的载体：<b>每回合开始时弹药上限 +Amount</b>
/// （打出时 2，升级版 3）。叠加则 Amount 相加。
///
/// <para>⚠️ 弹药上限是<b>每场战斗重置</b>的（<c>AmmoState.ResetForCombat</c> 把 Cap 拉回
/// <c>InitialCap = 7</c>），而这个 Power 只在战斗内存在 —— 两者生命周期一致，不会有残留。</para>
///
/// <para>给它涨上限的牌不止这一张（「重大升级」+10、「填装形态」+1/张），
/// 但**没有任何硬顶**：<c>AmmoCmd.ModifyCap</c> 只保证 ≥ 0。用户 2026-10-03 决定保持现状。</para>
/// </summary>
[RegisterPower]
public sealed class InfiniteAmmoPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(InfiniteAmmoPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每回合加多少上限。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return Task.CompletedTask;
        }

        int gain = (int)base.Amount;

        if (gain > 0)
        {
            AmmoCmd.ModifyCap(player, gain);
        }

        return Task.CompletedTask;
    }
}
