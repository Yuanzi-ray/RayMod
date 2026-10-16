using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「扩容弹匣」：获得 4 点格挡，弹药上限 +3。<b>消耗</b>。升级后上限 +4。
///
/// <para>加的是<b>上限</b>（<see cref="AmmoCmd.ModifyCap"/>）不是当前弹药 ——
/// 上限是本场战斗内的状态，战斗结束归零。上限提高后当前弹药不会自动补满，
/// 需要配合「装弹」「快速装填」「弹药库」这类牌兑现。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Expansion : ModCardTemplate, IAmmoCard
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
        new BlockVar(4m, ValueProp.Move),
        new CardsVar("Cap", 3)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public Expansion() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await CreatureCmd.GainBlock(Owner.Creature, DynamicVars.Block, cardPlay);

        AmmoCmd.ModifyCap(Owner, DynamicVars["Cap"].IntValue);
    }

    protected override void OnUpgrade()
    {
        // 升级：上限 +3 → +4（格挡维持 4）。
        DynamicVars["Cap"].UpgradeValueBy(1m);
    }
}
