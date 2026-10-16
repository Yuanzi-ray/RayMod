using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「去咧嘴谷」：<b>0 费</b>，层数小于 9 时才能打出，召唤 3 只沙地兽。
/// 升级后召唤 5 只。
///
/// <para>这是"低压区"的补偿牌：层数一旦上去（≥9）就再也打不出来，
/// 与「谋生之道」（层数 &gt;19 才能打）构成一对相反的闸门。</para>
///
/// <para>召唤统一走 <see cref="BeastCmd.Summon"/>：层数 ≥30 时它会自动少召唤一只、
/// &gt;50 时一只都不召唤但卡牌其他效果照常（用户 2026-10-03 拍板的口径）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class GrinningValley : DepthGatedCard, ISandBeastCard, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("DepthCeiling", 9),
        new CardsVar("Count", 3)
    ];

    public GrinningValley() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数小于 9 才满足条件（「探井人」的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthLessThan(Owner, 9);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await BeastCmd.Summon(choiceContext, Owner, DynamicVars["Count"].IntValue, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：3 只 → 5 只。
        DynamicVars["Count"].UpgradeValueBy(2m);
    }
}
