using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「饲养」：将 2 张「沙地兽」加入你的手牌。<b>消耗</b>。升级后将 3 张。
///
/// <para>"造一张牌进手牌"的标准写法（照抄原版 <c>CollisionCourse</c>）：
/// <c>CombatState.CreateCard&lt;T&gt;(Owner)</c> 造实例 →
/// <c>CardPileCmd.AddGeneratedCardsToCombat(cards, PileType.Hand, Owner)</c> 入堆。</para>
///
/// <para><b>⚠️ 这里造的是「沙地兽」<u>卡</u></b>（<c>RayMod.Cards.SandBeast</c>，
/// 0 费、召唤 1 只、自带抽牌、消耗），不是场上的召唤物
/// （<c>RayMod.Beasts.SandBeast</c>）。写全限定名避免歧义。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Raise : ModCardTemplate, ISandBeastCard
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
        new CardsVar("Count", 2)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public Raise() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int count = DynamicVars["Count"].IntValue;

        for (int i = 0; i < count; i++)
        {
            var beastCard = CombatState.CreateCard<RayMod.Cards.SandBeast>(Owner);

            await CardPileCmd.AddGeneratedCardToCombat(beastCard, PileType.Hand, Owner);
        }
    }

    protected override void OnUpgrade()
    {
        // 升级：2 张 → 3 张。
        DynamicVars["Count"].UpgradeValueBy(1m);
    }
}
