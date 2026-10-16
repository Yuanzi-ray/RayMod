using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「撤退」：获得 7 点格挡，<b>层数 -10</b>，抽两张牌。升级后层数额外 -5（即 -15）。
///
/// <para>层数可以减少 —— <c>DepthCmd.AddDepth</c> 接受负数，内部会把结果夹在 ≥ 0
/// （<c>DepthCmd.cs</c> 的 <c>SetDepth</c> 用 <c>Math.Max(0, value)</c>），所以不用担心扣成负数。</para>
///
/// <para>执行顺序照设计稿：格挡 → 层数 → 抽牌。注意顺序会影响"层数变化触发"类能力
/// （「资金」「源矿」）的结算时机，别随手调换。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Withdraw : ModCardTemplate, IDepthCard
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
        new BlockVar(7m, ValueProp.Move),
        new CardsVar("DepthLoss", 10),
        new CardsVar(2)
    ];

    public Withdraw() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        await DepthCmd.AddDepth(choiceContext, Owner, -DynamicVars["DepthLoss"].IntValue, cardSource: this);

        await CardPileCmd.Draw(choiceContext, DynamicVars.Cards.BaseValue, Owner);
    }

    protected override void OnUpgrade()
    {
        // 升级：层数额外 -5（10 → 15）。格挡与抽牌数不变。
        DynamicVars["DepthLoss"].UpgradeValueBy(5m);
    }
}
