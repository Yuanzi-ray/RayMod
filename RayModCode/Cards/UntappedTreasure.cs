using System.Linq;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「未探之宝」：<b>层数大于 40 时才能打出</b>。造成 10 点伤害，<b>层数 -5</b>，
/// 往<b>抽牌堆</b>塞一张这张牌的复制品；<b>你每有一张和层数有关的牌，这张牌额外打出一次</b>。
/// 升级后伤害 10 → 12。
///
/// <para><b>用户 2026-10-04 拍板⑤（三条，都是这张牌的语义核心）</b>：</para>
/// <list type="number">
/// <item>额外打出<b>不触发"额外打出"本身</b> —— 次数在<b>打出那一刻算一次就定死</b>，
/// 不会因为复制品/额外打出而滚雪球；但<b>伤害 / 层数-5 / 复制品</b>这三件事<b>每次都会发生</b>。</item>
/// <item><b>中途层数 ≤40 时，后面所有额外打出全部不生效</b> —— 所以它不是"一次打 10 下"，
/// 而是"打到层数掉到 40 为止"。这条是这张牌真正的强度阀门（层数每多 5 层才多打一下）：
/// <b>45 层进来只打得动 1 下</b>（打出后变 40，额外打出全部作废），
/// 50 层 2 下、100 层 12 下（100→95→…→40）—— 前提是"你手上的层数牌"够多（次数上限）。</item>
/// <item>次数口径 = <b>本场战斗里你手上的全部牌</b>（抽牌堆 + 手牌 + 弃牌堆 + 消耗堆 + 出牌堆）
/// 中实现了 <see cref="IDepthCard"/> 的张数 —— 与「口径扩大」数弹药牌同一口径，
/// 也就是<b>原版同款</b>（原版 <c>PerfectedStrike</c> 用的就是
/// <c>PlayerCombatState.AllCards</c>，见 <c>CountDepthCards</c> 的注释）。</item>
/// </list>
///
/// <para><b>用户 2026-10-04 拍板："复制品当然要计算"</b> —— 所以这张牌每打一次塞进抽牌堆的
/// 复制品，<b>当场就进了"你手上的牌"</b>，下一次打出时计数更高（自己也是其中一张）。
/// 于是它天然是"越打越大"的牌：前提是层数还够（每次 -5，掉到 ≤40 就停）。</para>
///
/// <para><b>为什么用循环而不是 <c>CardCmd.AutoPlay</c></b>：AutoPlay 会把这张牌
/// <b>再完整打出一次</b>（重新进 <c>OnPlay</c> → 重新算次数 → 触发"额外打出"本身），
/// 与拍板⑤第一条直接冲突。循环只重复"结算三件事"，语义干净。
/// ⚠️ 注意"拍板⑤第 1 条"管的是<b>同一次打出内部</b>：这一次的次数在打出瞬间定死，
/// 不会因为本次塞进去的复制品而变多；复制品影响的是<b>下一次</b>打出。</para>
///
/// <para>门槛走 <see cref="DepthGatedCard"/>（层数门槛的唯一通道，「探井人」的豁免自动生效）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class UntappedTreasure : DepthGatedCard, IDepthCard
{
    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    private const int BaseEnergyCost = 0;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>可打出的层数门槛（严格大于 40）。★ <b>逻辑读这个常量。</b></summary>
    private const int RequiredDepth = 40;

    /// <summary>每次结算掉的层数。★ <b>逻辑读这个常量。</b></summary>
    private const int DepthLossPerPlay = 5;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    /// <summary>
    /// ★ 2026-10-07：卡面显示走 DynamicVar（这样改常量卡面自动跟着变），
    /// 但<b>逻辑仍读上面的 const</b> —— 两者由下面的静态自检强制同步。
    ///
    /// <para>⚠️ <b>为什么不直接读 <c>DynamicVars</c></b>（这是踩过的坑，别改回去）：
    /// 基类 <c>DepthGatedCard.IsPlayable =&gt; DepthGate.CanPlay(Owner, DepthConditionMet)</c>
    /// 会在<b>卡池构建阶段</b>就被求值，而那时 <c>DynamicVars</c> 可能尚未填充 ——
    /// 读它会在初始化期抛异常，导致<b>整个卡牌池加载失败、卡牌全都不显示</b>
    /// （2026-10-07 实测：卡牌压根不显示。而 «探照灯» 因为只在出牌时读，所以不受影响）。</para>
    /// </summary>
    /// <summary>
    /// 卡面 <c>{ExtraPlays}</c> 显示用：**实时**数本场"层数牌"张数。
    ///
    /// <para>⚠️ 2026-10-07 教训（逻辑错误，别重犯）：先前这里读的是一个只在
    /// <see cref="OnPlayGated"/> 里赋值的字段 <c>_extraPlayCount</c> ——
    /// 于是<b>卡面永远显示上一次打出时的值</b>（没打过就是 0），
    /// 而卡面恰恰要在<b>打出之前</b>告诉玩家"这次会额外打出几下"。
    /// 这里必须直接读场上状态，不能依赖"是否打出过"。</para>
    /// </summary>
    private int PreviewExtraPlays() => CountDepthCards();

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(10m, ValueProp.Move),
        new DynamicVar("RequiredDepth", RequiredDepth),
        new DynamicVar("DepthLossPerPlay", DepthLossPerPlay),
        // 卡面 {ExtraPlays}：本次会额外打出的次数（实时数本场层数牌张数）。
        // 用 DelegateVar 覆写取值 —— 不碰 CalculationBase（本卡没声明它，
        // 而 CalculatedVar.SetOwner 会在初始化期去取它 → 崩图鉴，已踩过）。
        new DelegateVar("ExtraPlays", 0m,
            card => (card as UntappedTreasure)?.PreviewExtraPlays() ?? 0)
    ];

    /// <summary>
    /// 同步自检：常量与卡面变量必须一致。改了常量却忘了改变量（或反之）
    /// 会在<b>加载时立刻抛异常</b>，而不是等玩家发现"卡面骗人"。
    /// 第二列是"卡面变量的本意值"，改阈值时两处一起改。
    /// </summary>
    private static readonly decimal[] _varSyncCheck =
    [
        RequiredDepth - 40m,
        DepthLossPerPlay - 5m
    ];

    public UntappedTreasure() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>层数大于 40 才满足条件（探井人的豁免由基类统一处理）。</summary>
    protected override bool DepthConditionMet => DepthCmd.IsDepthGreaterThan(Owner, RequiredDepth);

    protected override async Task OnPlayGated(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        var target = cardPlay.Target;

        // 额外打出的次数在**打出这一刻**定死（拍板⑤第 1 条）：
        // 包括随后塞进抽牌堆的复制品，都不会让它继续变多。
        int extraPlays = CountDepthCards();

        int plays = 1;

        await ResolveOnce(choiceContext, cardPlay, target);

        for (int i = 0; i < extraPlays; i++)
        {
            // 拍板⑤第 2 条：中途层数 ≤40 → 后面所有额外打出全部不生效。
            if (!DepthConditionMet)
            {
                break;
            }

            await ResolveOnce(choiceContext, cardPlay, target);
            plays++;
        }

        // 卡面**不显示**这个次数（没有对应占位符），所以留一条日志方便验收：
        // 一眼看出"数到了几张、实际打了几下、打完剩多少层"。
        Log.Info($"[未探之宝] 和层数有关的牌 {extraPlays} 张 -> 本次共打出 {plays} 次" +
                 $"（剩余层数 {DepthCmd.GetDepth(Owner)}）");
    }

    /// <summary>
    /// 一次结算 = 伤害 + 层数 -5 + 往抽牌堆塞一张复制品（拍板⑤：这三件事每次都会发生）。
    /// </summary>
    private async Task ResolveOnce(
        PlayerChoiceContext choiceContext, CardPlay cardPlay, Creature target)
    {
        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);

        await DepthCmd.AddDepth(choiceContext, Owner, -DepthLossPerPlay, cardSource: this);

        // 复制品照抄原版 Anger（0 费打进弃牌堆）的做法，只把牌堆换成抽牌堆：
        // CreateClone 保留升级状态，AddGeneratedCardToCombat 处理"战斗内新牌"的归属
        // （⚠️ 不能用 RunState.CreateCard —— 那种牌进不了战斗牌堆，坑 41）。
        var copy = CreateClone();

        CardCmd.PreviewCardPileAdd(
            await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Draw, Owner), 1.5f);
    }

    /// <summary>
    /// 数"和层数有关的牌"：<b>本场战斗里你手上的全部牌</b>（抽牌堆 + 手牌 + 弃牌堆 + 消耗堆 + 出牌堆）。
    ///
    /// <para><b>口径（用户 2026-10-04 拍板："复制品当然要计算"）</b>：这就是<b>规则④那种
    /// "牌组中的牌"口径</b>（"含抽牌堆+手牌+弃牌堆"），也正是<b>原版同款</b> ——
    /// 原版 <c>PerfectedStrike</c>（"你的牌组中每有一张攻击牌，伤害+2"）写的是
    /// <c>card.Owner.PlayerCombatState.AllCards.Count(...)</c>；原版 <c>Squeeze</c>
    /// 还特意写了 <c>c != card</c> 来<b>排除</b>自己，反证 <c>AllCards</c> 天然
    /// <b>包含正在打出的这一张</b>（打出时它在出牌堆里）。</para>
    ///
    /// <para>所以这个数里天然有：<b>自己这一张</b>、以及这张牌<b>历次塞进抽牌堆的复制品</b>
    /// —— 同一场里打得越多，以后每一次打得越多。</para>
    ///
    /// <para>⚠️ <b>别用 <c>PileType.Deck</c></b>（2026-10-04 走过一次弯路）：那是
    /// <b>跑团级主牌组</b>，而战斗中的牌其实是它的<b>克隆</b>
    /// （<c>Player.PopulateCombatState</c> 里 <c>state.CloneCard(item)</c>），
    /// 于是"控制台塞进手牌的那张"、"被效果生成/复制的牌"甚至<b>自己那一张</b>
    /// 都可能数不到（用户实测控制台加牌时就是这样，见坑 47）。</para>
    /// </summary>
    private int CountDepthCards() =>
        Owner.PlayerCombatState?.AllCards.Count(card => card is IDepthCard) ?? 0;

    protected override void OnUpgrade()
    {
        // 升级：伤害 10 → 12（设计稿："造成的伤害提升至12"）。
        DynamicVars.Damage.UpgradeValueBy(2m);
    }
}
