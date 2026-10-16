using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 「井」升级版的**选项载体**：层数 -5。不是真的能打出的牌。
/// 与 <see cref="WellDeeper"/> 成对，用法见那里的注释。
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class WellShallower : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Basic;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = false;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>
    /// 卡面变量：<b>只负责卡面上显示的那个 5</b>。
    ///
    /// <para>这张牌永远不会被打出（<see cref="OnPlay"/> 是空实现），真正的 ±5 由
    /// <see cref="Well"/> 结算 —— <c>Well.cs</c> 里声明的那份<b>同名同值</b>的
    /// <c>Depth</c> 变量才是<b>生效真值</b>
    /// （<c>int step = DynamicVars["Depth"].IntValue;</c>）。两处必须同步改。</para>
    ///
    /// <para>这里之所以还要再声明一份，是本地化门禁的硬要求：卡面占位符
    /// <c>{Depth:diff()}</c> 的名字必须能在<b>本卡自己的 .cs</b> 里找到同名符号（声在别处不认）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("Depth", 5)
    ];

    public WellShallower() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>空实现：这张牌只作为「井」的选项展示，不会被真正打出。</summary>
    protected override Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        return Task.CompletedTask;
    }

    protected override void OnUpgrade()
    {
    }
}
