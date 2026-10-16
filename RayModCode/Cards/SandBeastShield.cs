using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「沙地兽之盾」：获得 8 点格挡；<b>本场战斗每召唤过一只沙地兽，额外获得 2 点格挡</b>。
/// 升级后每只 +4。
///
/// <para>格挡版的动态显示用 <c>CalculatedBlockVar</c>：<c>基准 8 + 2 × 本场召唤数</c>
/// ⇒ 卡面上的 <c>{CalculatedBlock}</c> 就是实际会拿到的格挡。
/// 结算照抄原版 <c>Stack.cs</c> 的写法（<c>CalculatedBlock.Calculate(target)</c> + <c>.Props</c>）。</para>
///
/// <para>⚠️ 格挡版的"每单位值"是 <c>CalculationExtraVar</c>（不是 <c>ExtraDamageVar</c>，
/// 那个名字只管伤害；源码里也没有 <c>ExtraBlockVar</c> 这个类）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastShield : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    public override bool GainsBlock => true;

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(8m),
        new CalculationExtraVar(2m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => BeastCmd.TryGetState(card.Owner)?.SummonedThisCombat ?? 0)
    ];

    public SandBeastShield() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(
            Owner.Creature,
            DynamicVars.CalculatedBlock.Calculate(cardPlay.Target),
            DynamicVars.CalculatedBlock.Props,
            cardPlay);
    }

    protected override void OnUpgrade()
    {
        // 升级：每只 2 → 4（设计稿："额外格挡数提升至4"）。
        DynamicVars.CalculationExtra.UpgradeValueBy(2m);
    }
}
