using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
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

    /// <summary>
    /// ★★ 2026-10-19 <b>表现层挂载点</b>（用户："填装形态下，卡牌不再是简单飞回弃牌堆或者简单抽上来。
    /// 并且需要有个常驻特效"）。
    ///
    /// <para>和「巨兽」<c>FeranmutsPower.AfterApplied → CardFieldVfx.AttachGiantEye</c> 完全同一套路：
    /// Power 只管<b>什么时候挂/收</b>，画什么全在 <c>Art\CardFieldVfx.ReloadForm.cs</c>。
    /// 「收」不需要我们操心 —— 弹链自己每 0.5s 自查一次 <c>GetPower&lt;LoadingPosturePower&gt;()</c>。</para>
    ///
    /// <para>⚠️ 本钩子<b>只做表现</b>：不加减弹药、不改上限。数值在 <see cref="AfterCardPlayed"/> 里。</para>
    /// </summary>
    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        RayMod.Art.CardFieldVfx.AttachAmmoBelt(base.Owner?.Player ?? base.Owner?.PetOwner);

        // ★ 2026-10-19：她**身边**的常驻件（缠体螺旋光带 + 下落黄铜弹壳）。
        //   与上面那条 HUD 弹链是两件事：弹链在界面角上，这个画在战场上、贴着她本人。
        RayMod.Art.CardFieldVfx.AttachReloadAura(base.Owner?.Player ?? base.Owner?.PetOwner);

        return Task.CompletedTask;
    }

    /// <summary>能力掉了：立即收场（不靠轮询也来得及）。</summary>
    public override Task AfterRemoved(Creature oldOwner)
    {
        RayMod.Art.CardFieldVfx.RetireAmmoBelt();
        RayMod.Art.CardFieldVfx.RetireReloadAura();
        return Task.CompletedTask;
    }

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
