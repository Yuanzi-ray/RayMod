using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见能力「弹药库」：每回合开始时，<b>若弹药未达上限就补 4 点</b>。升级后补 5 点。
///
/// <para>和「无限子弹」（抬上限）是一条互补的线：那张把天花板抬高、这张负责往上涨。
/// 逻辑在 <see cref="MagazinePower"/> 的回合开始钩子里。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Magazine : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Ammo", 4)
    ];

    public Magazine() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<MagazinePower>(
            choiceContext, Owner.Creature, DynamicVars["Ammo"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每回合 +4 → +5。
        DynamicVars["Ammo"].UpgradeValueBy(1m);
    }
}
