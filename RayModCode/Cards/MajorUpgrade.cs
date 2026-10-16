using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「重大升级」：<b>弹药上限 +10</b>，消耗。升级后 +15。
///
/// <para>⚠️ 弹药上限**没有硬顶**（<c>AmmoCmd.ModifyCap</c> 只保证 ≥ 0）——
/// 用户 2026-10-03 明确决定保持现状。所以这张牌在长战斗里可以叠得很高。</para>
///
/// <para>上限是每场战斗重置的（<c>AmmoState.ResetForCombat</c> 拉回 7），与本牌的一次性效果同生命周期。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class MajorUpgrade : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>消耗词条（设计稿写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Cap", 10)
    ];

    public MajorUpgrade() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        AmmoCmd.ModifyCap(Owner, DynamicVars["Cap"].IntValue);

        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
        // 升级：上限 +10 → +15（设计稿："升级后弹药上限+15"）。
        DynamicVars["Cap"].UpgradeValueBy(5m);
    }
}
