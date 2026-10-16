using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「一场误会」（设计稿没给类名，这个名字是补的）：获得 6 点格挡，
/// 随机打出抽牌堆中一张<b>费用为零</b>的牌。升级后费用 1 → 0。
///
/// <para>原版有 <c>CardPileCmd.AutoPlayFromDrawPile</c>，但它只支持
/// "顶部/底部/随机"三种取法、<b>不能按费用过滤</b>
///（见 <c>CardPileCmd.cs:1000</c>），所以这里自己筛：
/// 挑出 0 费牌 → <c>CardPileCmd.Add(card, PileType.Play)</c> 挪到出牌区 →
/// <c>CardCmd.AutoPlay</c> 结算。顺序照抄原版那个方法。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Misunderstanding : ModCardTemplate
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
        new BlockVar(6m, ValueProp.Move)
    ];

    public Misunderstanding() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        // 抽牌堆里所有"当前费用为 0"的牌（X 费不算）。
        var freeCards = PileType.Draw.GetPile(Owner).Cards
            .Where(card => !card.EnergyCost.CostsX
                           && card.EnergyCost.GetWithModifiers(CostModifiers.All) == 0)
            .ToList();

        if (freeCards.Count == 0)
        {
            return;
        }

        var pick = Owner.RunState.Rng.CombatCardSelection.NextItem(freeCards);

        if (pick == null)
        {
            return;
        }

        await CardPileCmd.Add(pick, PileType.Play);
        await CardCmd.AutoPlay(choiceContext, pick, null);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 1 → 0（设计稿："升级后：费用减1"）。
        EnergyCost.UpgradeBy(-1);
    }
}
