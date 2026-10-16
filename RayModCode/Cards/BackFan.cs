using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「背扇」：获得 8 点格挡；将一张「沙地兽」添加到<b>抽牌堆</b>；层数 +3。<b>消耗</b>。
/// 升级后层数 +5。
///
/// <para>注意它加的是<b>抽牌堆</b>而不是手牌 —— 和「饲养」正好互补：
/// 饲养是立刻能用但占手牌位，背扇是延后到手（位置随机）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class BackFan : ModCardTemplate, ISandBeastCard, IDepthCard
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
        new BlockVar(8m, ValueProp.Move),
        new CardsVar("Depth", 3)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public BackFan() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        // 造的是「沙地兽」卡（RayMod.Cards.SandBeast），不是召唤物。
        var beastCard = CombatState.CreateCard<RayMod.Cards.SandBeast>(Owner);

        await CardPileCmd.AddGeneratedCardToCombat(
            beastCard, PileType.Draw, Owner, CardPilePosition.Random);

        await DepthCmd.AddDepth(choiceContext, Owner, DynamicVars["Depth"].IntValue, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：层数 +3 → +5（设计稿："升级后层数+5"）。
        DynamicVars["Depth"].UpgradeValueBy(2m);
    }
}
