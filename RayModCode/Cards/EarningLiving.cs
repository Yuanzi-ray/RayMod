using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「谋生之道」：<b>层数大于 19 时才能打出</b>；获得 1 点能量。<b>消耗</b>。
/// 升级后添加<b>保留</b>词条。
///
/// <para>"可以打出"的门槛用 <see cref="IsPlayable"/> 拦（原版同款钩子，
/// 不满足时牌会以不可打出的样式显示，费用不足的手感）。</para>
///
/// <para>它是唯一一张把层数<b>换成能量</b>的牌：层数越高越难降下来（10/30/50 层有减益），
/// 所以这 1 点能量是有代价的。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class EarningLiving : DepthGatedCard, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("RequiredDepth", 19),
        new EnergyVar(1)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public EarningLiving() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 19（即 20 层起）才满足条件（「探井人」的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, 19);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        await PlayerCmd.GainEnergy(DynamicVars.Energy.BaseValue, Owner);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："升级后添加保留词条"）。
        AddKeyword(CardKeyword.Retain);
    }
}
