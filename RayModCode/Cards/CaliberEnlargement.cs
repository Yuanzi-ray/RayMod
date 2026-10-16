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
    /// 数"与弹药有关的牌"：<b>本场战斗里你手上的全部牌</b>（抽牌堆 + 手牌 + 弃牌堆 + 消耗堆 + 出牌堆）。
    ///
    /// <para>⚠️ 必须是 <c>static</c> 写法：乘数 lambda 一旦捕获 <c>this</c>，原版
    /// <c>CalculatedVar.WithMultiplier</c> 会直接抛 <c>"Multiplier calc must be static!"</c>
    /// （坑 27）。静态方法组转成的委托 <c>Target</c> 是 null，所以能过那道检查。</para>
    ///
    /// <para><b>口径（用户 2026-10-04 拍板："复制品当然要计算"）</b>：
    /// 这就是<b>规则④那种"牌组中的牌"口径</b>（"含抽牌堆+手牌+弃牌堆"），也正是<b>原版同款</b> ——
    /// 原版 <c>PerfectedStrike</c>（"你的牌组中每有一张攻击牌，伤害+2"）用的就是
    /// <c>PlayerCombatState.AllCards</c>；原版 <c>Squeeze</c> 还特意写了 <c>c != card</c>
    /// 来<b>排除</b>自己，反证 <c>AllCards</c> 天然<b>包含正在打出的这一张</b>（打出时它在出牌堆里）。</para>
    ///
    /// <para>所以"自己也算"是<b>结构上成立</b>的 —— 不需要任何特判；
    /// 战斗中生成/复制的弹药牌也一并计入。</para>
    ///
    /// <para>⚠️ <b>别用 <c>PileType.Deck</c></b>（2026-10-04 走过一次弯路）：那是
    /// <b>跑团级主牌组</b>，战斗中的牌是它的<b>克隆</b>，于是控制台加进手牌的那张
    /// （<c>card RAY_MOD_CARD_CALIBER_ENLARGEMENT hand</c>）数不到，卡面少 1 点伤害 ——
    /// 用户实测就是这样（见坑 47）。</para>
    /// </summary>
    private static int CountAmmoCards(CardModel card) =>
        card.Owner.PlayerCombatState?.AllCards.Count(c => c is IAmmoCard) ?? 0;

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
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：基础伤害 4 → 6（用户 2026-10-03 定的数值；每张弹药牌的 +1 不变）。
        DynamicVars.CalculationBase.UpgradeValueBy(2m);
    }
}
