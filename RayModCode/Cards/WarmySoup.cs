using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「温米的汤」：获得 6 点格挡；<b>下回合获得 1 点能量</b>。
/// 升级后格挡 9、下回合 2 点能量。
///
/// <para>下回合加能量用原版 <see cref="EnergyNextTurnPower"/>
///（原版卡 <c>ChargeBattery</c> / <c>Outmaneuver</c> 同款）。它是一个 Power，
/// 所以卡进弃牌堆也不影响结算。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class WarmySoup : ModCardTemplate
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new BlockVar(6m, ValueProp.Move),
        new EnergyVar(1)
    ];

    public WarmySoup() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        await PowerCmd.Apply<EnergyNextTurnPower>(
            choiceContext, Owner.Creature, DynamicVars.Energy.BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：格挡 6 → 9，下回合能量 1 → 2（设计稿）。
        DynamicVars.Block.UpgradeValueBy(3m);
        DynamicVars.Energy.UpgradeValueBy(1m);
    }
}
