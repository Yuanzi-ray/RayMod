using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 罕见技能「塌方」：<b>你每有 5 层层数，层数 +3</b>。升级后每次 +4。
///
/// <para>层数按<b>打出瞬间的快照</b>算（先算"有几个 5 层"，再加层数），
/// 所以不会自己滚雪球 —— 20 层时是"4 档 × 3 = +12"，而不是"加了之后再重新数一遍"。</para>
///
/// <para>层数只能走 <c>DepthCmd</c>（唯一入口）：真实层数在跑团级存档里，
/// 它会负责同步战斗内的角标与档位结算。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Landslide : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Uncommon;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    /// <summary>几层算一档（设计稿："你每有5层层数"）。</summary>
    private const int DepthPerStep = 5;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// ★ 2026-10-07：<c>{PerDepth}</c> 喂卡面（原本"5"写死在文案里），
    /// 加 <c>CalculatedBlock</c> 让卡面显示**实得的层数**（用户要求）。
    ///
    /// <para>乘数 lambda 只读 <see cref="DepthCmd.GetDepth"/>，不碰 <c>DynamicVars</c>
    /// —— 避免"初始化期读 DynamicVars 导致卡牌全不显示"的坑。
    /// 实算值 = 当前层数 ÷ <see cref="DepthPerStep"/>（取整）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Gain", 3),
        new DynamicVar("PerDepth", DepthPerStep),
        new CalculationBaseVar(0m),
        new CalculationExtraVar(1m),
        new CalculatedBlockVar(ValueProp.Move)
            .WithMultiplier((card, _) => DepthCmd.GetDepth(card.Owner) / DepthPerStep)
    ];

    /// <summary>同步自检：<c>{PerDepth}</c> 与卡面本意值必须一致（本卡当前 5）。</summary>
    private static readonly decimal[] _varSyncCheck = [DepthPerStep - 5m];

    public Landslide() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 快照：用打出时的层数算档数，之后再一次性加。
        int steps = DepthCmd.GetDepth(Owner) / DepthPerStep;

        if (steps <= 0)
        {
            return;
        }

        await DepthCmd.AddDepth(
            choiceContext, Owner, steps * DynamicVars["Gain"].IntValue, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每档 +3 → +4（设计稿："你每有5层层数，层数+4"）。
        DynamicVars["Gain"].UpgradeValueBy(1m);
    }
}
