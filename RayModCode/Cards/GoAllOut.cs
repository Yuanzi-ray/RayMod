using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「全力以赴」：获得相当于<b>当前层数</b>的格挡；但<b>本回合从其他牌获得的格挡 -80%</b>。
/// 升级后削减 80% → 70%。
///
/// <para>用三元组让卡面显示实算值（<c>基准 0 + 1 × 当前层数</c>），
/// 所以层数越高这张牌越厚 —— 代价是这一回合别的防御牌几乎给不出格挡。</para>
///
/// <para>口径（用户 2026-10-03 拍板 Q9）：只减<b>牌</b>给的格挡（能力/遗物不减），
/// 且只持续<b>本回合</b>；这张牌自己的格挡不减。实现见 <see cref="GoAllOutPower"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class GoAllOut : ModCardTemplate, IDepthCard
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
        // 基准 0 + 1 × 层数 ⇒ 卡面显示的就是"当前层数"。
        new CalculationBaseVar(0m),
        new CalculationExtraVar(1m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => DepthCmd.GetDepth(card.Owner)),
        new DynamicVar("Reduction", 80m)
    ];

    public GoAllOut() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 先挂上"削减"，再给自己格挡 —— 这样它立刻对本回合之后所有别的牌的格挡生效。
        // 自己那笔不受影响：Power 会用 cardSource 认出本牌（见 GoAllOutPower）。
        await PowerCmd.Apply<GoAllOutPower>(
            choiceContext, Owner.Creature, DynamicVars["Reduction"].BaseValue, Owner.Creature, this);

        await CreatureCmd.GainBlock(
            Owner.Creature,
            DynamicVars.CalculatedBlock.Calculate(cardPlay.Target),
            DynamicVars.CalculatedBlock.Props,
            cardPlay);
    }

    protected override void OnUpgrade()
    {
        // 升级：削减 80% → 70%（设计稿："-70%"）。
        DynamicVars["Reduction"].UpgradeValueBy(-10m);
    }
}
