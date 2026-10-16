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
/// 稀有能力「积累」：<b>下场战斗开始时，把你的层数变为本场战斗结束时的值</b>。升级后添加<b>保留</b>。
///
/// <para>用户 2026-10-04 拍板：「积累」是<b>有意的跨战斗层数保留</b>，
/// 取回合结束时的值 —— 这是"层数每场战斗归零"这条基础规则里<b>唯一的自愿例外</b>
/// （见 <see cref="DepthState.CarriedDepth"/>）。</para>
///
/// <para><b>为什么它值得一张稀有位</b>：层数是本角色的核心资源，但它每场战斗都归零，
/// 所以"高层数档位"（&gt;50 禁召唤 / &gt;60 遗物档）在正常玩法里很难摸到。
/// 这张牌把上一场的高度直接搬到下一场的开头 —— 是本角色唯一能"跨战斗攒层数"的手段。</para>
///
/// <para>⚠️ 只带<b>一场</b>：<c>DepthCmd.EnsurePower</c> 读走之后就消费掉了，
/// 不会滚雪球。所以每场战斗都要重新打一张「积累」才能续上。</para>
///
/// <para>逻辑在 <see cref="AccumulatePower"/>（那里记了两个记录点与原因）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Accumulate : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Power;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有数值占位符（带过去的层数是动态的，写不进卡面）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    public Accumulate() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 角标只显示一个 1（没有"层数"语义），数值本身在跑团级存档里。
        await PowerCmd.Apply<AccumulatePower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："添加保留词条"）。
        AddKeyword(CardKeyword.Retain);
    }
}
