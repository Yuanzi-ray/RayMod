using MegaCrit.Sts2.Core.Entities.Cards;
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
/// 稀有技能「填装形态」的载体：<b>你每打出一张牌，就 +Amount 点弹药、+Amount 点弹药上限</b>。
///
/// <para>钩子用 <c>AfterCardPlayed</c>（<c>AbstractModel.cs:472</c>）。
/// ⚠️ <b>顺序不能反</b>：必须<b>先抬上限、再加弹药</b> ——
/// <see cref="AmmoCmd.AddAmmo"/> 会被上限夹住（用户确认的规则："不能超过上限"），
/// 先加弹药的话，等于白加。</para>
///
/// <para>只认<b>自己</b>打出的牌：<c>AfterCardPlayed</c> 是全局广播，
/// 宠物/敌人打牌时也会派发。</para>
/// </summary>
[RegisterPower]
public sealed class LoadingPosturePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(LoadingPosturePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每张牌加多少。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not { } owner)
        {
            return;
        }

        // 只算自己打出的牌。
        if (cardPlay.Card?.Owner != owner)
        {
            return;
        }

        int per = (int)base.Amount;

        if (per <= 0)
        {
            return;
        }

        // 先抬上限再加弹药，理由见类注释。
        AmmoCmd.ModifyCap(owner, per);
        AmmoCmd.AddAmmo(owner, per);

        await Task.CompletedTask;
    }
}
