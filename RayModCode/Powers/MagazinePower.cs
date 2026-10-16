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
/// 罕见能力牌「弹药库」的载体：<b>每回合开始时，若弹药未达上限就补 Amount 点</b>（打出时 4，升级版 5）。
///
/// <para>"未达上限才补"是设计稿的原文 —— 所以满弹时会空过（不浪费，也不溢出）。
/// 上限本身会被「重大升级」「无限子弹」抬高，所以这张牌在长战斗里会越来越能补。</para>
/// </summary>
[RegisterPower]
public sealed class MagazinePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(MagazinePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每回合补多少。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return Task.CompletedTask;
        }

        int gain = (int)base.Amount;

        // 设计稿："如果你的子弹没有达到上限，弹药+4" —— 满了就不补。
        if (gain > 0 && AmmoCmd.GetAmmo(player) < AmmoCmd.GetCap(player))
        {
            AmmoCmd.AddAmmo(player, gain);
        }

        return Task.CompletedTask;
    }
}
