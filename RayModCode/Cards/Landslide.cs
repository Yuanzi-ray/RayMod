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
    /// ★ 2026-10-17 修（用户指出："每有 5 获得 3 应该严格，而不是转成比例"）：
    ///
    /// <para><b>原实现的问题</b>：<c>CalculatedBlock</c> 的公式是
    /// <c>CalculationBase + CalculationExtra × 乘数</c>（反编译 <c>CalculatedVar.Calculate</c> 实证）。
    /// 原来是 Base=0 / Extra=1 / 乘数=档数 ⇒ 卡面算出来是 <b>档数本身</b>：
    /// 20 层显示「获得 4」，实际给的是 12。玩家看到 4（正好等于 20÷5）自然以为"按比例算了"。</para>
    ///
    /// <para><b>现在的口径</b>：把 <see cref="CalculationExtraVar"/> 直接当成"每档增益"，
    /// 乘数保持 <b>严格档数</b>（整数除法，向下取整）⇒ 卡面 = 档数 × 每档增益，
    /// 20 层显示 12、7 层显示 3、4 层显示 0，全是 3 的整数倍。<b>再没有任何比例成分。</b></para>
    ///
    /// <para>⚠️ 乘数 lambda 只读 <see cref="DepthCmd.GetDepth"/>，不碰 <c>DynamicVars</c>
    /// —— 避免"初始化期读 DynamicVars 导致卡牌全不显示"的坑（铁律 14）。
    /// 也因此"每档增益"必须由 <c>CalculationExtra</c> 承担，而不是在 lambda 里读 <c>{Gain}</c>。</para>
    ///
    /// <para>⚠️ 文案里的 <c>{Gain}</c> 已同步改成 <c>{CalculationExtra}</c>（同一个数，不再有两个真值源）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        // 每档 +3（升级 +1 → 4）。这是"每有 5 深度获得 N"里的 N，也是卡面实算的乘数系数。
        new CalculationExtraVar(3m),
        new DynamicVar("PerDepth", DepthPerStep),
        new CalculationBaseVar(0m),
        // 严格档数：整数除法 ⇒ 向下取整，不产生小数，不做比例。
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
            choiceContext, Owner, steps * DynamicVars.CalculationExtra.IntValue, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：每档 +3 → +4（设计稿："你每有5层层数，层数+4"）。
        // ★ 2026-10-17：改的是 CalculationExtra —— 它现在同时是卡面实算的系数，改 Gain 已经没意义。
        DynamicVars.CalculationExtra.UpgradeValueBy(1m);
    }
}
