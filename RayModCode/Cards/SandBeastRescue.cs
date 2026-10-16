using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「沙地兽的拯救」：造成 2 点伤害；<b>本回合你每召唤过一次沙地兽，额外打出一次</b>；
/// <b>这张牌每造成伤害一次，它的伤害 +1（本场累积）</b>。升级后基础伤害 2 → 4。
///
/// <para><b>用户 2026-10-04 拍板⑥</b>：这个"+1"是<b>在本场战斗内累积</b>的
/// （不是每回合清零，也不是打完这张牌就没了）—— 所以同一场里打得越多，它越厚。</para>
///
/// <para><b>两处口径说明（设计稿没写死，按本工程既有约定取）</b>：</para>
/// <list type="number">
/// <item>"本回合你每召唤过一次沙地兽"读 <c>BeastState.SummonedThisTurn</c>
/// （<b>按只数</b>，一次召唤 3 只 = 3 次额外打出）——
/// 与「沙地兽之盾」「沙地兽狂潮」的"每一只"口径一致。
/// <b>★ 2026-10-11 补充</b>：只算<b>成功入场</b>的只数（含满上限替换），
/// 被层数减益拦成 0 只的那些不算 —— 判据是"本回合召唤=0 且 本回合被层数拦掉>0"
/// （见 <c>OnPlay</c> 里的诊断日志）。</item>
/// <item><b>★ 2026-10-11 修的那条真 bug</b>：这个"本回合"值以前靠
/// <c>SandBeastQuotaPower.AfterPlayerTurnStart</c> 清零，而那个钩子跑在<b>抽牌之后</b>
/// （<c>CombatManager.cs:921 抽牌 → :923 派发</c>）—— "回合开始流程中间"发生的召唤会被
/// 随后的清零吞掉、中间读到的又是上一回合的旧值。现在改成按回合号记账
/// （<c>BeastState.CountedTurn</c> + <c>BeastCmd.RollTurnOver</c>），与钩子时机解耦。</item>
/// <item>增伤存在<b>这张牌实例自己</b>的字段里（<see cref="DamageBonus"/>），
/// 在 <c>AfterCardEnteredCombat</c> 时归零 —— 天然做到"每场战斗重置"，
/// 而且同一副牌组里的两张副本各记各的（互不干扰）。</item>
/// </list>
///
/// <para><b>卡面显示</b>走三元组（<c>基准 2 + 1 × 本场已增伤次数</c>），
/// 乘数直接读实例字段（<c>card as SandBeastRescue</c>）—— 这是<b>静态</b>写法，
/// 不会踩"捕获 this 会抛 Multiplier calc must be static!"那个坑（坑 27）。</para>
///
/// <para>增伤在<b>每次攻击之后</b>立刻 +1，所以同一回合里的第 2、3 次额外打出
/// 就已经吃到了上面几次的加成 —— 这是"连打越打越疼"的手感来源，也是设计意图。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SandBeastRescue : ModCardTemplate, ISandBeastCard
{
    private const int BaseEnergyCost = 1;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    /// <summary>诊断日志（2026-10-07：验"每次打出伤害 +1"是否生效）。</summary>
    private static readonly Logger Log = new("RayMod.Cards", LogType.Generic);

    /// <summary>这张牌本场已经累积了多少点增伤（每次造成伤害 +1）。</summary>
    private int _damageBonus;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new CalculationBaseVar(2m),
        new ExtraDamageVar(1m),
        // 卡面 {ExtraPlays}：本回合召唤的**只数** = 本次会额外打出的次数。
        // 用 DelegateVar（覆写 UpdateCardPreview，不碰 CalculationBase/ExtraDamage 那对
        // 被伤害占用的变量），因此"动态伤害 + 额外次数"可以并存。
        new DelegateVar("ExtraPlays", 0m,
            card => BeastCmd.TryGetState(card.Owner)?.SummonedThisTurn ?? 0),
        new CalculatedDamageVar(ValueProp.Move)
            .WithMultiplier((card, _) => (decimal)((card as SandBeastRescue)?.DamageBonus ?? 0))
    ];

    public SandBeastRescue() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    /// <summary>
    /// 本场累积的增伤（只读，给卡面乘数用）。
    /// 是 <c>public</c> 只因为三元组的乘数 lambda 在<b>另一处</b>读它时更直观。
    /// </summary>
    public int DamageBonus => _damageBonus;

    /// <summary>
    /// 本场第一次进入战斗时归零（<c>AfterCardEnteredCombat</c> 只在这张牌
    /// "从没有牌堆变成有战斗牌堆"那一刻触发一次）。
    /// </summary>
    public override Task AfterCardEnteredCombat(CardModel card)
    {
        if (card == this)
        {
            _damageBonus = 0;
        }

        return Task.CompletedTask;
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        var target = cardPlay.Target;

        // 额外打出的次数在**打出这一刻**读一次（之后的结算不会再召唤沙地兽，所以等价）。
        // ★ 2026-10-11：TryGetState 会把"本回合"记账滚到当前回合号
        //   （BeastCmd.RollTurnOver），所以这里拿到的<b>一定是"本回合实际成功召唤的只数"</b>
        //   —— 不再依赖"回合开始钩子有没有在抽牌后跑过"。口径见 BeastState.CountedTurn。
        var state = BeastCmd.TryGetState(Owner);
        int extraPlays = state?.SummonedThisTurn ?? 0;
        int turn = Owner?.PlayerCombatState?.TurnNumber ?? -1;

        // 诊断日志（2026-10-07 建立；2026-10-11 补"回合号"与"本回合被层数拦掉几只"）。
        // 形如： [沙地兽的拯救] 起手 回合号=8 本回合召唤=0 额外打出=0 本回合被层数拦掉=2 起始增伤=1 首段伤害=3
        //
        // ⚠️ 判据读法（2026-10-11 实机踩过的那个"次数不对"）：
        //   本回合召唤=0 且 被层数拦掉>0 ⇒ <b>不是计数错了</b>，而是这回合的召唤被
        //   "30 层起少召唤 1 只"整只吃掉了（只召唤 1 只的牌 → 0 只，BeastCmd.Summon 里
        //   count==0 直接 return 0，压根没有"成功召唤"）。反过来，被拦掉=0 而召唤=0
        //   ⇒ 这回合确实没打过召唤牌。
        Log.Info($"[沙地兽的拯救] 起手 回合号={turn} 本回合召唤={extraPlays} 额外打出={extraPlays} " +
                 $"本回合被层数拦掉={state?.BlockedSummonsThisTurn ?? 0} " +
                 $"起始增伤={_damageBonus} 首段伤害={DynamicVars.CalculatedDamage.Calculate(cardPlay.Target)}");

        await Strike(choiceContext, cardPlay, target);

        for (int i = 0; i < extraPlays; i++)
        {
            await Strike(choiceContext, cardPlay, target);
        }

        Log.Info($"[沙地兽的拯救] 打完 累计增伤={_damageBonus} " +
                 $"（下次首段伤害={DynamicVars.CalculatedDamage.Calculate(cardPlay.Target)}）");
    }

    /// <summary>
    /// 一次结算 = 打一下，并且 <b>每打一下伤害 +1</b>（本场累积）。
    ///
    /// <para>★ 2026-10-07 用户口径修正：<b>额外打出的那些也要触发增伤</b>，
    /// 所以同一次出牌内的多段是<b>等差数列</b>（3、4、5、6…）。
    /// 因此 <c>_damageBonus++</c> 必须放在<b>每次 Strike 里</b>，不能放在
    /// <c>OnPlay</c> 末尾（那样一次出牌只涨 1，同一次内的多段全打同一个数 —— 就是"递增失效"）。</para>
    ///
    /// <para>之所以能这样写：反编译 0.111 的 <c>AttackCommand</c>（第 669 行）确认伤害是
    /// <b>每一段结算时</b>才调 <c>_calculatedDamageVar.Calculate(target)</c>，
    /// <b>不是</b>构造时定死 —— 所以两次 Strike 之间改 <c>_damageBonus</c> 会生效。</para>
    /// </summary>
    private async Task Strike(PlayerChoiceContext choiceContext, CardPlay cardPlay, Creature target)
    {
        _damageBonus++;

        await DamageCmd.Attack(DynamicVars.CalculatedDamage)
            .FromCard(this, cardPlay)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
            .Execute(choiceContext);
    }

    protected override void OnUpgrade()
    {
        // 升级：基础伤害 2 → 4（设计稿："造成伤害提升至4点"）。每打一次的 +1 不变。
        DynamicVars.CalculationBase.UpgradeValueBy(2m);
    }
}
