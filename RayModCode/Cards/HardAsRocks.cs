using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有能力「宛如沙石」：<b>每当有生物的 buff 变动时，获得 1 点格挡</b>。升级后 2 点。
///
/// <para>口径（用户 2026-10-04 拍板）：只算<b>己方</b>生物身上的 <c>Buff</c>；
/// 敌人身上的、以及所有 Debuff 都不算。</para>
///
/// <para>和"力量/敏捷"这类会反复变动的 buff 配合收益最高；
/// 本角色自己的层数/弹药走 <c>SetAmount</c>（不触发那条钩子），所以不会爆刷。
/// 细节见 <see cref="HardAsRocksPower"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class HardAsRocks : ModCardTemplate
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// 用普通 DynamicVar（不是 BlockVar）：这张牌自己不直接给格挡，
    /// 格挡是 Power 给的，所以不需要 <c>GainsBlock</c>。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("BlockGain", 2m)
    ];

    public HardAsRocks() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<HardAsRocksPower>(
            choiceContext, Owner.Creature, DynamicVars["BlockGain"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：1 → 2 点格挡（设计稿）。
        DynamicVars["BlockGain"].UpgradeValueBy(1m);
    }
}
