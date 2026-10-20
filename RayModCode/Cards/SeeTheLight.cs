using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Cards;

/// <summary>
/// 稀有攻击「得见光芒」：造成 30 点伤害，<b>击晕敌人</b>，
/// <b>下一张攻击牌费用变为 0</b>；<b>若打出后弹药大于等于 3，则返回手牌并弹药 -3</b>。
/// 升级后伤害 30 → 40。
///
/// <para>⚠️ <b>2026-10-13 用户改的口径</b>：原来是"**大于** 3"，用户实机（<b>子弹模式</b>下）报
/// "弹药大于3但是没有回手" ⇒ 改成"**大于等于** 3"（`AfterCardPlayedLate` 里 `&lt;= cost` → `&lt; cost`）。
/// 那一局的日志正好是证据：`消耗 3 点弹药 ⇒ 发射 3 颗（SeeTheLight …，弹药 6 → 3）` ——
/// **子弹模式把这张牌的弹药费用在 `BeforeCardPlayed` 就扣掉了**，于是这里读到的"打出后的弹药"
/// 已经是 6-3=3，旧判据（要 &gt; 3）当场不成立。改成 ≥ 之后 6 点弹药就回手了。</para>
///
/// <para><b>为什么"返回手牌"写在 <c>AfterCardPlayedLate</c> 而不是 <c>OnPlay</c> 里</b>：
/// <c>OnPlay</c> 跑完之后，牌还会被框架"移动到结果牌堆"（弃牌堆）。
/// 如果在 <c>OnPlay</c> 里就把它塞回手牌，随后的移动会把它重新拽回弃牌堆。
/// 原版「有样学样」（<c>MakeItSo</c>）与「右手右手」（<c>RightHandHand</c>）
/// 都用 <c>AfterCardPlayedLate</c> —— 那时框架已经不再动它了，照抄这个位置最稳。</para>
///
/// <para>⚠️ 判断条件用 <c>Pile.Type != PileType.Hand</c>（而不是 <c>== Discard</c>）：
/// 原版那两张牌一个判 <c>Discard</c> 一个判 <c>!= Hand</c>，而实测里这个时机牌可能还在
/// <c>Play</c> 牌堆 —— 判"不已经在手上"能同时覆盖两种情况。</para>
///
/// <para><b>"下一张攻击牌免费"用原版 <see cref="FreeAttackPower"/></b>（照抄「狙击镜」）：
/// 它自己负责"手牌/出牌堆里的下一张攻击牌费用 = 0"以及打出时自减 1 层，
/// 别自己写费用钩子 —— 那种"两处判据各写一遍"的坑在「爆破」上踩过（坑 31）。</para>
/// </summary>
[RegisterCard(typeof(RayModCardPool))]
public sealed class SeeTheLight : ModCardTemplate, IAmmoCard
{
    private const int BaseEnergyCost = 3;
    private const CardType CardKind = CardType.Attack;
    private const CardRarity CardRarityValue = CardRarity.Rare;
    private const TargetType CardTarget = TargetType.AnyEnemy;
    private const bool ShowInCardLibrary = true;

    public override CardAssetProfile AssetProfile => new(
        PortraitPath: RayMod.Art.CardArt.Portrait(GetType().Name, RayMod.Art.CardArt.Placeholder.Attack));

    protected override HashSet<CardTag> CanonicalTags => new() { CardTag.Strike };

    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DamageVar(30m, ValueProp.Move),
        new DynamicVar("AmmoCost", 3m)
    ];

    public SeeTheLight() : base(BaseEnergyCost, CardKind, CardRarityValue, CardTarget, ShowInCardLibrary)
    {
    }

    protected override async Task OnPlay(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        ArgumentNullException.ThrowIfNull(cardPlay.Target);

        // ★★ 让伤害等特效收束完 —— 用户 2026-10-12 定："伤害应该在特效炸开的瞬间结算"。
        //    收束是出牌那一刻（CardFieldVfx.Play）起手的，这里只等剩下的那一段；
        //    特效被关掉 / 测试台模式时 SeeLightHitWait() 返回 0，行为与从前完全一致。
        //    2026-10-12 实测背景：不这么对齐时，伤害早就结算完了、整套特效才炸开（"时机不对"）。
        float seeLightWait = RayMod.Art.CardFieldVfx.SeeLightHitWait();

        if (seeLightWait > 0.001f && Godot.Engine.GetMainLoop() is Godot.SceneTree seeTree)
        {
            await seeTree.ToSignal(
                seeTree.CreateTimer(seeLightWait, processAlways: false),
                Godot.SceneTreeTimer.SignalName.Timeout);
        }

        await DamageCmd.Attack(DynamicVars.Damage.BaseValue)
            .FromCard(this, cardPlay)
            .Targeting(cardPlay.Target)
            .WithHitFx("vfx/vfx_starry_impact")   // 枪械卡：点状命中（原来是 blunt 闷响）
            .Execute(choiceContext);

        // 照抄原版 Whistle（3 费 33 伤害 + 击晕 + 消耗）：伤害之后直接击晕。
        // 目标已经死了的话 StunInternal 自己会静默跳过（它判了 !IsDead）。
        await CreatureCmd.Stun(cardPlay.Target);

        await PowerCmd.Apply<FreeAttackPower>(
            choiceContext, Owner.Creature, 1m, Owner.Creature, this);
    }

    /// <summary>
    /// 打出之后：弹药够就回到手牌并扣弹药（设计稿："如果打出之后弹药数**大于等于** 3"）。
    /// 位置与判据的理由见类注释。
    ///
    /// <para>⚠️ 判据是 <c>&lt; cost</c>（<b>≥ 3 就回手</b>）—— 2026-10-13 用户从"大于 3"改过来的；
    /// <b>子弹模式</b>下这张牌的弹药费用在 <c>BeforeCardPlayed</c> 已被扣掉，用旧判据会永远不成立。</para>
    /// </summary>
    public override async Task AfterCardPlayedLate(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (cardPlay.Card != this)
        {
            return;
        }

        int cost = DynamicVars["AmmoCost"].IntValue;

        // "打出之后"——所以扣的是打完这张牌之后剩下的弹药；≥ cost 才回手。
        if (AmmoCmd.GetAmmo(Owner) < cost)
        {
            return;
        }

        // 已经在手上了就什么都不做（原版 MakeItSo 的同款防御）。
        if (Pile is { Type: PileType.Hand })
        {
            return;
        }

        AmmoCmd.ConsumeAmmo(Owner, cost);

        await CardPileCmd.Add(this, PileType.Hand);
    }

    protected override void OnUpgrade()
    {
        // 升级：伤害 30 → 40（设计稿："伤害提升至40"）。
        DynamicVars.Damage.UpgradeValueBy(10m);
    }
}
