using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 普通卡「口径扩大」：对所有敌人造成 4 点伤害；<b>你每有一张与"弹药"有关的牌，这张牌伤害 +1</b>。
/// 升级后基础伤害提升至 6（每张的 +1 不变）。
///
/// <para><b>⚠️ "与弹药有关"的判据换过两次（2026-10-03）</b>：</para>
/// <list type="number">
/// <item>最早靠 <c>card.DynamicVars.ContainsKey("Ammo")</c> 猜 —— 全工程只命中 4 张，
/// 精制子弹（变量叫 <c>Uses</c>）、扩容弹匣／重大升级／无限子弹（变量叫 <c>Cap</c>）、
/// 乘算子弹／沙地兽背包（没有变量）等**全漏**，成长几乎看不见（用户反馈"真的有生效吗"）。</item>
/// <item>现在按用户 2026-10-03 的口径：<b>"卡面描述里带『弹药』二字的牌"都算，而且这张牌自己也算</b>
/// （它自己的描述里就有"弹药"）。实现走标记接口 <see cref="IAmmoCard"/>，
/// 并由离线校验器强制"接口成员 == 文案里带弹药的牌"，防止再次静默失效。</item>
/// </list>
///
/// <para>⚠️ <b>为什么不直接在运行时读文案</b>：描述是按语言取的（中文"弹药"、英文"Ammo"），
/// 那样会让<b>伤害值随语言变化</b> —— 玩法不能依赖显示语言。</para>
///
/// <para>群体攻击的写法照抄原版 <c>Sow</c>：<c>.TargetingAllOpponents(CombatState)</c>。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class CaliberEnlargement : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Common;
    private const TargetType CardTarget = TargetType.AllEnemies;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(4m),
        new ExtraDamageVar(1m),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => CountAmmoCards(card))
    ];

    /// <summary>
    /// 数"与弹药有关的牌"。<b>口径 = 跑团级主牌组 + 战斗中凭空生成的弹药牌</b>。
    ///
    /// <para>⚠️ 必须是 <c>static</c> 写法：乘数 lambda 一旦捕获 <c>this</c>，原版
    /// <c>CalculatedVar.WithMultiplier</c> 会直接抛 <c>"Multiplier calc must be static!"</c>
    /// （坑 27）。静态方法组转成的委托 <c>Target</c> 是 null，所以能过那道检查。</para>
    ///
    /// <para><b>为什么是"牌组"而不是"当前牌堆"（2026-10-16 玩家反馈的真 bug）</b></para>
    /// <para>卡面英文写的本来就是 <c>"for each Ammo-related card <b>in your deck</b>"</c> ——
    /// 这就是原版 <c>PerfectedStrike</c>（"你的牌组中每有一张攻击牌"）的口径。原实现读的是
    /// <c>PlayerCombatState.AllCards</c>（手牌+抽牌堆+弃牌堆+消耗堆+出牌堆），
    /// 而<b>打出的能力牌会被原版移出全部战斗牌堆</b>：</para>
    /// <code>
    /// CardModel.GetResultLocationForCardPlay():
    ///     if (IsDupe || Type == CardType.Power) return new CardLocation(Owner, PileType.None, ...);
    /// → MoveCardToResultPileAfterPlay() 的 case PileType.None:
    ///     await CardPileCmd.RemoveFromCombat(this, ...)   // 五个战斗牌堆全摘掉，但不动跑团牌组
    /// </code>
    /// <para>而我们的弹药牌里有 <b>5 张是能力牌</b>（子弹模式 / 入神 / 无限子弹 / 装填姿态 / 弹匣），
    /// ⇒ 每打出一张，<c>AllCards</c> 就少一张，<b>「口径扩大」的伤害当场 −1，且本场战斗不再回来</b>。
    /// 玩家报的就是"打出能力牌之后伤害变低"。</para>
    ///
    /// <para><b>新口径的两个来源（互斥，天然不会重复计数）</b></para>
    /// <list type="number">
    /// <item><b>跑团级牌组</b> <c>owner.Deck</c> —— 战斗开始时原版把牌组里每张牌<b>克隆</b>一份进抽牌堆，
    /// 并回填 <c>clone.DeckVersion = 原牌</c>（<c>Player.PopulateCombatState</c>）。
    /// 所以牌组里的牌="名册"，打出的能力牌本体仍在里面 ⇒ 数得到。</item>
    /// <item><b>战斗中生成的弹药牌</b>（<c>DeckVersion is null</c>）—— 这部分卡面没有本体，
    /// 只能从战斗牌堆里数。用户 2026-10-04 拍板的"复制品当然要计算"由此保住。</item>
    /// </list>
    /// <para>两段判据用 <c>DeckVersion</c> 天然切开：牌组里的牌一定是从①数的，
    /// 战斗牌堆里有本体的克隆（<c>DeckVersion != null</c>）不再从②数一遍。<b>不需要任何去重集合。</b></para>
    ///
    /// <para>顺带订正一条旧注释（2026-10-04）说"别用 <c>PileType.Deck</c>，控制台加进手牌的那张数不到"：
    /// 那个判据换掉是错的 —— 控制台加进手牌的牌<b>没有 DeckVersion</b>，正好落在②里，
    /// 照样数得到。当时少 1 点伤害的真正原因是"只用了①、没加②"，不是"不能用牌组"。</para>
    ///
    /// <para>⚠️ 残余边界：战斗中<b>生成出来的能力牌</b>（<c>DeckVersion == null</c>）打出后
    /// 仍然会从计数里消失 —— 本 mod 目前没有这种牌（生成线只有「精制子弹」等非能力牌），
    /// 真出现了再单独记账。</para>
    /// </summary>
    private static int CountAmmoCards(CardModel card)
    {
        // ① 跑团级主牌组：卡面 "in your deck" 的准确来源。打出的能力牌本体还在这里。
        int count = card.Owner.Deck.Cards.Count(c => c is IAmmoCard);

        // ② 战斗中生成/复制的弹药牌（无牌组本体）。非战斗界面时 PlayerCombatState 为 null。
        if (card.Owner.PlayerCombatState is { } combat)
        {
            count += combat.AllCards.Count(c => c is IAmmoCard && c.DeckVersion is null);
        }

        return count;
    }

    public CaliberEnlargement() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // 伤害走 CalculatedDamage（基准 4 + 1 × 弹药牌数），卡面显示的就是实算值。
        // ⚠️ 乘数 lambda 里必须用 card.Owner，不能捕获 this（原版 WithMultiplier 会抛
        //    "Multiplier calc must be static!"），所以上面把 Owner 换成了 card.Owner。
        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .TargetingAllOpponents(CombatState)
            .WithHitFx("vfx/vfx_starry_impact")   // 枪械卡：点状命中（原来是 blunt 闷响）
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：基础伤害 4 → 6（用户 2026-10-03 定的数值；每张弹药牌的 +1 不变）。
        DynamicVars.CalculationBase.UpgradeValueBy(2m);
    }
}
