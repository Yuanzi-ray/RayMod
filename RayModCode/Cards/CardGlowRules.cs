using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Depth;
using STS2RitsuLib.Scaffolding.Cards.HandGlow;

namespace RayMod.Cards;

/// <summary>
/// 卡牌**手牌发光**规则（用户 2026-10-07 要求）：
/// 当卡面描述里的那个"条件"<b>真的生效</b>时，让卡在手牌里发光，一眼看出"这张现在更值"。
///
/// <para><b>两个通道</b>（RitsuLib 直接映射到原版的
/// <c>CardModel.ShouldGlowGold</c> / <c>ShouldGlowRed</c>）：
/// <list type="bullet">
/// <item><b>金</b> = 加成条件已满足（更值）。</item>
/// <item><b>红</b> = 手牌警告（有代价/风险）。</item>
/// </list></para>
///
/// <para>⚠️⚠️ <b>铁律：谓词必须与卡面描述里的条件<b>逐字对应</b>，包括阈值。</b>
/// 2026-10-07 踩坑：一开始图省事全用 <c>AmmoCmd.HasAmmo</c>(&gt;0)，
/// 结果「爆破」卡面写"弹药<b>大于7</b>"，弹药=7 时就发光了 ——
/// 玩家以为触发了效果，实际没有，<b>这是误导</b>（用户原话："这不是误导玩家吗"）。
/// 所以：<b>卡面写大于 N，谓词就必须是 &gt; N</b>；卡面无条件，就<b>不该发光</b>。</para>
///
/// <para>谓词只读 <c>card.Owner</c> 的战斗状态，<b>不读写 <c>DynamicVars</c></b>
/// （初始化期那个坑踩过两次）。</para>
/// </summary>
public static class CardGlowRules
{
    /// <summary>在 <see cref="Entry.Initialize"/> 里调用一次。</summary>
    public static void Register()
    {
        // ══ 金：弹药条件（阈值与卡面逐字对应）══════════════════════════════
        // 阿兰娜的车：「若有弹药，额外造成…」→ >0
        RegisterGold<AlannaCar>(AmmoAbove0);
        // 爆破：「弹药大于{AmmoPaymentThreshold}=7」→ >7（同 Cannon.IsPaidWithAmmo）
        RegisterGold<Cannon>(card => AmmoCmd.GetAmmo(card.Owner) > 7);
        // 得见光芒：「若弹药大于{AmmoCost}=3」→ >3
        RegisterGold<SeeTheLight>(card => AmmoCmd.GetAmmo(card.Owner) > 3);
        // 霰弹枪：「消耗掉所有弹药，每层使其再结算一次」→ >0 才有多段
        RegisterGold<Shotgun>(AmmoAbove0);
        // 狙击：「消耗掉所有弹药，每消耗1层就造成伤害」→ >0 才有伤害
        RegisterGold<Snipe>(AmmoAbove0);
        // 脱身矢：伤害来自"上一张牌"，但弹药部分是白拿的 → 不设条件、不发光
        // 你只能走到这了：伤害来自"本回合弹药牌已造成伤害" → 前面打过才满足
        RegisterGold<OnlyGoThisFar>(card => AmmoCmd.GetTurnAmmoDamage(card.Owner) > 0);
        // 伏击：「你每有1层弹药，每段额外+N」→ >0 才有多出来的部分
        RegisterGold<Ambush>(AmmoAbove0);
        // 精制子弹：「额外造成相当于当前弹药数的伤害」→ >0 才有加成
        RegisterGold<RefinedBullet>(AmmoAbove0);
        // 重构子弹：「下两张攻击牌额外造成相当于当前弹药数的伤害」→ >0
        RegisterGold<RefactoringBullets>(AmmoAbove0);
        // 口径扩大：「你每有一张与弹药有关的牌，额外+N」——数的是**本场所有 IAmmoCard**，
        // 其中包含它自己，所以 >1 才代表"除了自己还有别人"。
        RegisterGold<CaliberEnlargement>(AmmoCardCountAbove1);

        // ⚠️ 以下两张**故意不注册**（它们没有任何条件，永远成立 → 发光只会变成噪音）：
        //   AttackIsDefense「获得相当于弹药上限的格挡」—— 恒成立
        //   Reserve「获得相当于当前层数一半的弹药」—— 恒成立

        // ══ 金：沙地兽条件 ═══════════════════════════════════════════════
        // 沙地兽突袭：「若场上有沙地兽，额外造成…」→ 场上有兽
        RegisterGold<SandBeastRaid>(HasBeast);
        // ⚠️ 沙地兽狂潮 / 沙地兽之盾 **用户 2026-10-07 明确要求不加**（曾注册过，已移除）。
        // 理由：它们的加成是"本场每召唤一只 +N"的**累积被动值**，卡面上已经用
        // {CalculatedDamage}/{CalculatedBlock} 直接显示成实算总数了 —— 不存在
        // "条件此刻才成立"这回事（召唤过就一直成立），发光只会变成噪音。
        // 对比「沙地兽突袭」是"场上有兽 vs 没有"的**开关式**条件，才值得发光。
        // 沙地兽的拯救：「本回合每召唤一只，额外打出一次」→ 本回合召唤过
        RegisterGold<SandBeastRescue>(card => SummonedThisTurn(card) > 0);
        // 沙地兽背包 / 跳舞的月光：「等同于本场已召唤数量」→ 本场召唤过才有值
        RegisterGold<SandBeastBackpack>(card => SummonedThisCombat(card) > 0);
        RegisterGold<DancingMoonlight>(card => SummonedThisCombat(card) > 0);

        // ══ 金：层数门槛已满足（= 现在就打得出去）═════════════════════════
        // 判定直接借原版的 CanPlay —— 它已包含"层数条件 或 探井人豁免"，
        // 与卡面/可打出状态完全一致，不重复实现条件逻辑。
        RegisterGold<Continuous>(DepthGateOpen);
        RegisterGold<Dune>(DepthGateOpen);
        RegisterGold<EarningLiving>(DepthGateOpen);
        RegisterGold<EndlessDarkness>(DepthGateOpen);
        RegisterGold<GrinningValley>(DepthGateOpen);
        RegisterGold<ProveWhat>(DepthGateOpen);
        RegisterGold<TargetedKilling>(DepthGateOpen);
        RegisterGold<Travel>(DepthGateOpen);
        RegisterGold<UntappedTreasure>(DepthGateOpen);

        // ══ 红：手牌警告 ════════════════════════════════════════════════
        // 禁止明火：「若层数大于9，失去{HpLoss}点生命」→ 层数>9 时才付代价
        RegisterRed<NoFire>(card => DepthCmd.GetDepth(card.Owner) > 9);
    }

    // ── 谓词（名字即语义，便于逐条对照卡面）──────────────────────────────
    private static bool AmmoAbove0(CardModel card) => AmmoCmd.GetAmmo(card.Owner) > 0;

    private static bool HasBeast(CardModel card) => BeastCmd.GetCount(card.Owner) > 0;

    private static int SummonedThisTurn(CardModel card) =>
        BeastCmd.TryGetState(card.Owner)?.SummonedThisTurn ?? 0;

    private static int SummonedThisCombat(CardModel card) =>
        BeastCmd.TryGetState(card.Owner)?.SummonedThisCombat ?? 0;

    private static bool AmmoCardCountAbove1(CardModel card) =>
        (card.Owner.PlayerCombatState?.AllCards.Count(c => c is IAmmoCard) ?? 0) > 1;

    /// <summary>
    /// 层数门槛已满足。用原版 <c>CanPlay</c> 判定，并排除"能量/星不足"这类与层数无关的原因
    /// —— 我们要表达的是"层数条件开了"，不是"资源够不够"。
    /// </summary>
    private static bool DepthGateOpen(CardModel card) =>
        card is DepthGatedCard
        && card.CanPlay(out UnplayableReason reason, out _)
        && !reason.HasFlag(UnplayableReason.EnergyCostTooHigh)
        && !reason.HasFlag(UnplayableReason.StarCostTooHigh);

    // ── 注册小助手 ──────────────────────────────────────────────────────
    private static void RegisterGold<TCard>(Func<CardModel, bool> predicate) where TCard : CardModel =>
        ModCardHandGlowRegistry.Register<TCard>(ModCardHandGlowRules.Gold(predicate));

    private static void RegisterRed<TCard>(Func<CardModel, bool> predicate) where TCard : CardModel =>
        ModCardHandGlowRegistry.Register<TCard>(ModCardHandGlowRules.Red(predicate));
}
