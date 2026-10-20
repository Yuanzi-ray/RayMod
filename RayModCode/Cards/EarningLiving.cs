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
/// 普通卡「谋生之道」：获得 1 点能量。默认<b>消耗</b>；<b>深度大于 19 时本牌不消耗</b>。
/// 升级后添加<b>保留</b>词条。
///
/// <para><b>★ 2026-10-17 平衡</b>：取消了原来的"深度大于 19 才能打出"门槛（用户口径"去除层数限制"），
/// 并把「消耗」改成按深度动态判定。</para>
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

    /// <summary>
    /// 消耗词条：<b>静态</b>，永远带 Exhaust。
    ///
    /// <para>⚠️⚠️ <b>2026-10-17 踩过的真坑（0.1.5 线上 bug）</b>：我原来把它改成按深度动态返回
    /// <c>DepthCmd.IsDepthGreaterThan(Owner, 19) ? [] : [Exhaust]</c>，结果<b>整个卡牌图鉴崩了</b>：</para>
    /// <code>
    /// CanonicalModelException: Canonical model of type RayMod.Cards.EarningLiving used in incorrect place.
    ///    at RayMod.Cards.EarningLiving.get_CanonicalKeywords()
    /// </code>
    /// <para>原因：<c>CanonicalKeywords</c> 是在<b>"原型卡"（canonical model）</b>上被读的，
    /// 那里<b>不能碰任何运行时状态</b>（<c>DepthCmd</c> 会走到 run data / Creature）。
    /// 图鉴一次性读所有卡的词条 ⇒ 一张卡抛异常 ⇒ 整个网格渲染不出来，
    /// 页面只剩空槽和游戏自己的占位卡。</para>
    ///
    /// <para><b>所以"深度 &gt; 19 不消耗"改由 <see cref="GetResultLocationForCardPlay"/> 实现</b>——
    /// 那个方法只在<b>真正出牌时</b>被调一次（反编译 <c>CardModel.cs:1881</c>），
    /// 手上有活玩家，读深度是安全的。</para>
    /// </summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public EarningLiving() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>
    /// ★ 2026-10-17 平衡（用户口径："大于 19 层时不消耗"）。
    ///
    /// <para>基类（<c>CardModel.GetResultLocationForCardPlay</c>）里
    /// <c>Keywords.Contains(Exhaust)</c> 会把它送进消耗堆；这里在深度 &gt; 19 时
    /// <b>直接返回弃牌堆</b>，绕过那条分支。</para>
    ///
    /// <para>⚠️ 与 <c>CanonicalKeywords</c> 的禁忌完全不同：本方法只在出牌结算时调用，
    /// 不会在卡牌图鉴/原型卡上被读，所以这里读 <see cref="DepthCmd"/> 是安全的。</para>
    /// </summary>
    protected override CardLocation GetResultLocationForCardPlay()
    {
        if (DepthCmd.IsDepthGreaterThan(Owner, 19))
        {
            return new CardLocation(Owner, PileType.Discard, CardPilePosition.Bottom);
        }

        return base.GetResultLocationForCardPlay();
    }

    /// <summary>
    /// ★ 2026-10-17 平衡（用户口径："去除层数限制"）：门槛取消，任何深度都可打出。
    ///
    /// <para>仍然保留 <see cref="DepthGatedCard"/> 基类（它同时负责「探井人」的豁免接线），
    /// 只是条件恒为真 —— 比换成 <c>ModCardTemplate</c> 的改动面小得多。</para>
    /// </summary>
    protected override bool DepthConditionMet => true;

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
