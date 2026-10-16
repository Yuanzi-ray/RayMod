using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「动物图册」：<b>每回合开始时，把一张"和沙地兽有关的牌"加入你的手牌</b>。
/// 升级后费用 2 → 1。
///
/// <para>候选池是角色卡池里所有实现了 <see cref="ISandBeastCard"/> 的牌
/// （目前 21+ 张，见 <c>_tools\check_sandbeast_cards.ps1</c>）。
/// 所以它每回合都会送来一张沙地兽体系的牌 —— 召唤、印记、上限、弹药联动都可能。</para>
///
/// <para>逻辑在 <see cref="AnimalGuidePower"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class AnimalGuide : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Count", 1)
    ];

    public AnimalGuide() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<AnimalGuidePower>(
            choiceContext, Owner.Creature, DynamicVars["Count"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："费用变为1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
