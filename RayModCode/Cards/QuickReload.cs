using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「快速装填」：弹药 +5。升级后：弹药 +6。
///
/// <para>纯弹药牌，没有别的效果。0 伤害 0 格挡，是最干净的"弹药上限硬夹"测试用例：
/// 弹药 5/7 时打出它只会到 7（用户确认：不能超过上限）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class QuickReload : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Ammo", 5)
    ];

    public QuickReload() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        AmmoCmd.AddAmmo(Owner, DynamicVars["Ammo"].IntValue);
        await Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        DynamicVars["Ammo"].UpgradeValueBy(1m);
    }
}
