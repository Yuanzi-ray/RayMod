using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见能力「源矿」：<b>每次层数变化，对随机敌人造成 6 点伤害</b>。升级后费用 2 → 1。
///
/// <para>一次 <c>AddDepth</c> 调用算一次事件（用户 2026-10-03 拍板 Q11）。
/// 所以「塌方」这种"按档数一次性加层数"的牌只触发一次 —— 这正是它不滚雪球的原因。</para>
///
/// <para>伤害是能力牌给的，不吃力量/易伤，也不吃沙地兽印记（印记只对攻击牌生效），
/// 但可以被格挡。细节见 <see cref="SourceOrePower"/>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SourceOre : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // 普通 DynamicVar（不是 DamageVar）：这个数字不会因为升级变化，
        // 升级改的是费用，所以不需要 :diff()。
        new DynamicVar("OreDamage", 6m)
    ];

    public SourceOre() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PowerCmd.Apply<SourceOrePower>(
            choiceContext, Owner.Creature, DynamicVars["OreDamage"].BaseValue, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："费-1"）。伤害不变。
        EnergyCost.UpgradeBy(-1);
    }
}
