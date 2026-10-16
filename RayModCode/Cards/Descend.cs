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
/// 普通卡「深入」：层数 +10；接下来两个回合开始时层数 -6。<b>消耗</b>。升级后费用减一。
///
/// <para>设计稿里 20 张普通卡中<b>唯一</b>一张既不碰弹药也不碰沙地兽/印记的牌，
/// 所以它能独立实现。「装弹」「有所发现」只有 +2/+3，
/// 而它是 +10 —— 这是把层数快速推过 20/30/50 档的关键牌之一。</para>
///
/// <para>副作用用 <see cref="PendingDepthLossPower"/> 承载（卡牌打出后进弃牌堆，
/// 没有地方记"还欠两个回合"）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class Descend : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 2;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    /// <summary>打出时立刻获得的层数。</summary>
    private const int DepthGain = 10;

    /// <summary>之后每回合扣的层数持续几个回合。</summary>
    private const int LossTurns = 2;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CardsVar("DepthGain", DepthGain),
        new CardsVar("LossTurns", LossTurns),
        // 卡面「层数-6」的那个 6：**生效真值**在共享文件里 ——
        // Depth\PendingDepthLossPower.cs 的 `public const int LossPerTurn = 6;`。
        // 卡文件没地方把这个值传过去：PowerCmd.Apply 只能传 Amount，
        // 而 PendingDepthLossPower 的 Amount 已经被"剩余回合数"占用了。
        // 这里按本地化门禁要求（占位符名字必须能在本卡自己的 .cs 里找到同名符号）
        // 声明一份同名字面量；⚠️ 与 Power 里那个 const 必须同步改。
        new DynamicVar("LossPerTurn", 6)
    ];

    /// <summary>消耗词条（设计稿明确写了）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Exhaust
    ];

    public Descend() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 1) 立刻 +10 层。
        await DepthCmd.AddDepth(choiceContext, Owner, DynamicVars["DepthGain"].IntValue, cardSource: this);

        // 2) 挂上"接下来两回合各 -6 层"的欠债。
        //    Amount = 剩余回合数（见 PendingDepthLossPower）。
        await PowerCmd.Apply<PendingDepthLossPower>(
            choiceContext, Owner.Creature, DynamicVars["LossTurns"].IntValue,
            applier: Owner.Creature, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：费用 2 → 1（设计稿："费用减一"）。
        EnergyCost.UpgradeBy(-1);
    }
}
