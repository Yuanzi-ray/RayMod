using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Ammo;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见能力「无限子弹」：每回合开始时弹药上限 +2。升级后 +3。
///
/// <para>和「填装形态」（每打一张牌 +1）互补：那张靠出牌节奏，这张靠回合数保底。
/// 上限是<b>每场战斗重置</b>的，所以这张牌的价值集中在长战斗。</para>
///
/// <para>⚠️ 目前弹药上限<b>没有硬顶</b>（用户 2026-10-03 决定保持现状），
/// 长战斗 + 多张此类牌会让上限涨得很高，配平时留意。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class InfiniteAmmo : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Cap", 2)
    ];

    public InfiniteAmmo() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<InfiniteAmmoPower>(
            choiceContext, Owner.Creature, DynamicVars["Cap"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每回合 +2 → +3。
        DynamicVars["Cap"].UpgradeValueBy(1m);
    }
}
