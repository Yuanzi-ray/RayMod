using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

using RayMod.Beasts;

namespace RayMod.Cards;

/// <summary>
/// 稀有 能力「沙地兽巢穴」：每回合开始时，将 2 张「沙地兽」加入手牌。<b>升级后获得「固有」</b>。
///
/// <para>和「饲养」（一次给两张）的区别是<b>持续性</b>：饲养是爆发，巢穴是每回合持续给。
/// 造卡的逻辑在 <see cref="SandBeastLairPower"/> 的 <c>BeforeHandDraw</c> 里。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastLair : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Count", 2)
    ];

    public SandBeastLair() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<SandBeastLairPower>(
            choiceContext, Owner.Creature, DynamicVars["Count"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：获得"固有"（数量不再变，固定每回合 2 张）。
        AddKeyword(CardKeyword.Innate);
    }
}
