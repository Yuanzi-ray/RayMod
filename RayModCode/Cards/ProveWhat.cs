using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Characters;
using RayMod.Depth;
using RayMod.Powers;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「证明什么？」：<b>层数大于 60 时才能打出</b>；
/// <b>无视任何条件，打出你抽牌堆、手牌与弃牌堆中的所有攻击牌（除了这张）</b>；
/// <b>下回合开始时死亡</b>。<b>虚无</b>（升级后去掉）。
///
/// <para><b>规则④（用户拍板）</b>："牌组中所有攻击牌"含<b>抽牌堆 + 手牌 + 弃牌堆</b>
/// （<b>不含</b>消耗堆），"下回合死亡"= <b>下回合开始时</b>死。</para>
///
/// <para><b>为什么 <c>CardCmd.AutoPlay</c> 正好就是"无视任何条件"</b>：
/// 它<b>完全不查 <c>IsPlayable</c></b>，只查 <c>Unplayable</c> 词条与
/// <c>Hook.ShouldPlay</c>（默认实现 <c>return true</c>）；而且
/// <c>resources.EnergySpent = 0</c> → <b>免费</b>；<c>TargetType.AnyEnemy</c> 且不给目标时
/// <b>自己随机挑一个可命中敌人</b>（所以这里统一传 <c>null</c>）。
/// 牌在抽牌堆里也能直接打（<c>OnPlayWrapper</c> 会先把它挪进出牌堆）——
/// 原版 <c>CardPileCmd.AutoPlayFromDrawPile</c>（Havoc / Cascade）走的就是这条路。</para>
///
/// <para><b>⚠️ 三处必须防的坑</b>：</para>
/// <list type="number">
/// <item><b>先快照列表</b>：每打一张牌都会改变牌堆归属，边遍历边打必然漏牌/重复。</item>
/// <item><b>逐张 try/catch</b>：一张牌的效果炸了不能把后面十几张全吞掉，也不能把出牌流程搞崩。</item>
/// <item><b>随时检查战斗是否已结束</b>：把对面全打死之后继续 AutoPlay 是在对着一场结束了的战斗出牌。</item>
/// </list>
///
/// <para>它自己<b>不造成伤害</b>（设计稿没写伤害），牌面类型仍是"攻击"，
/// 所以它<b>会</b>被别的"所有攻击牌"效果算进去 —— 但它自己把自己排除在外，
/// 不会自己打自己（无限递归）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class ProveWhat : DepthGatedCard, IDepthCard
{
    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    private const int BaseEnergyCost = 4;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;


    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    /// <summary>本卡没有伤害数值（它自己不打伤害）。</summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("RequiredDepth", 50)
    ];

    /// <summary>虚无词条（设计稿；升级后去掉）。</summary>
    public override IEnumerable<CardKeyword> CanonicalKeywords =>
    [
        CardKeyword.Ethereal
    ];

    public ProveWhat() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 60 才满足条件（探井人的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, 50);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 规则④：抽牌堆 + 手牌 + 弃牌堆（不含消耗堆）。
        // ⚠️ 先 ToList() 快照 —— 每打一张牌都会改变牌堆归属。
        var targets = new[] { PileType.Hand, PileType.Draw, PileType.Discard }
            .SelectMany(pile => pile.GetPile(Owner).Cards)
            .Where(card => card.Type == CardType.Attack && card != this)
            .ToList();

        Log.Info($"[证明什么？] 准备无视条件打出 {targets.Count} 张攻击牌");

        int played = 0;

        foreach (var card in targets)
        {
            // 战斗已经结束就别再打了（对面可能已经被前面几张打死了）。
            if (CombatManager.Instance.IsOverOrEnding)
            {
                break;
            }

            // 已经被别的效果挪走/移除的牌跳过（Pile 为 null 时 AutoPlay 会试图把它塞进出牌堆）。
            if (card.Pile == null)
            {
                continue;
            }

            try
            {
                await CardCmd.AutoPlay(choiceContext, card, null);
                played++;
            }
            catch (Exception e)
            {
                // 一张牌炸了不能把后面十几张全吞掉，也不能把出牌流程搞崩。
                Log.Warn($"[证明什么？] {card.Id.Entry} 自动打出失败（跳过）: {e}");
            }
        }

        Log.Info($"[证明什么？] 实际打出 {played}/{targets.Count} 张；下回合开始时死亡");

        // 规则④："下回合死亡" = 下回合**开始时**死（由这个 Power 在回合开始钩子里执行）。
        await PowerCmd.Apply<ProveWhatPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    protected override void OnUpgrade()
    {
        // 升级：去除"虚无"（设计稿）。
        RemoveKeyword(CardKeyword.Ethereal);
    }
}
