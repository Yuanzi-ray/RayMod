using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有技能「这是对的！」：<b>本回合内层数翻倍</b>。升级后添加<b>保留</b>。
///
/// <para>走 <see cref="DepthCmd.SetDepth"/>（"设为"语义，原版"积累"那类牌同款）。
/// ⚠️ 层数翻倍是<b>乘法</b>：40 层直接变 80 层，会一脚踩进 51 层的禁召唤 + 每回合 -5HP、
/// 以及 101 层的"连续 3 回合死亡"倒计时 —— 这是设计意图（层数越高收益与代价同时爆炸）。</para>
///
/// <para>因为层数每场战斗归零，这张牌的价值完全在"本场立刻兑现"上，
/// 所以升级给的是保留（等一个层数够高的回合再打）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ThatsRight : ModCardTemplate, IDepthCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Skill;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.Self;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Defend));

    /// <summary>本卡没有任何数值变量，描述里也不出现占位符。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => [];

    // 2026-10-10 用户要求去掉「消耗」→ 本卡不再声明 CardKeyword.Exhaust。
    // 原先此处有一个只为 Exhaust 而存在的 CanonicalKeywords override，现已整个删除：
    // CardModel.CanonicalKeywords 是 virtual 且默认返回 Array.Empty<CardKeyword>()，
    // 删掉 override 即回落到「无词条」，比留一个返回空集合的空 override 更干净。
    // （升级仍照旧 AddKeyword(CardKeyword.Retain)，与本次改动无关。）

    public ThatsRight() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        int depth = DepthCmd.GetDepth(Owner);

        if (depth <= 0)
        {
            return;
        }

        await DepthCmd.SetDepth(choiceContext, Owner, depth * 2, cardSource: this);
    }

    protected override void OnUpgrade()
    {
        // 升级：添加"保留"（设计稿："添加保留词条"）。
        AddKeyword(CardKeyword.Retain);
    }
}
