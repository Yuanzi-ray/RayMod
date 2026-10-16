using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「子弹模式」的载体：<b>你打出的牌改为消耗等量的弹药，不能用能量支付</b>。
///
/// <para><b>用户 2026-10-04 拍板⑪</b>：设计稿的"优先消耗弹药"应读作<b>"不可用能量支付"</b>。</para>
///
/// <para><b>三个挂钩点，判据必须同源</b>（坑 31：「爆破」两处判据各写一遍踩过）：</para>
/// <list type="number">
/// <item><see cref="TryModifyEnergyCostInCombat"/> 把费用改成 <b>0</b>
/// —— 它进的是 <c>GetWithModifiers(CostModifiers.Global)</c>，而<b>实际扣费</b>走
/// <c>GetAmountToSpend()</c> = <c>GetWithModifiers(All)</c>，所以<b>显示与实际同源</b>，卡面会显示 0 费。</item>
/// <item><see cref="ShouldPlay"/> 在<b>手动出牌</b>时否决"弹药不够"的牌 —— 这一步是关键：
/// <c>CardModel.CanPlay</c> 里会查 <c>Hook.ShouldPlay(..., AutoPlayType.None)</c>，
/// 所以返回 false 会让卡面变灰（<c>UnplayableReason.BlockedByHook</c>），
/// <b>也就堵死了"用能量顶"这条路</b>（费用钩子只是把费用变 0，管不了玩家手里的能量）。</item>
/// <item><see cref="BeforeCardPlayed"/> 真正扣弹药。</item>
/// </list>
///
/// <para><b>⚠️ 弹药费用 = "把【我们自己】摘出去之后，游戏算出来的费用"</b>（见 <see cref="AmmoCostOf"/>）：
/// 反编译 <c>CardEnergyCost.GetWithModifiers</c> → <c>Hook.ModifyEnergyCostInCombat</c> 的链条是
/// <b>先跑一遍全部监听者、再跑一遍 Late</b>，起点是"基础 + 牌自己的本地修正"。
/// <c>FreeAttackPower</c>（「狙击镜」/「得见光芒」给的"下一张攻击免费"）正是在 <b>Late</b> 那一遍里置 0，
/// 所以只要**临时把自己挂起再重新求一次**，就能拿到"别人的减免都算上"的价钱 —— 顺序无关。</para>
///
/// <para><b>★★ 2026-10-13 用户报的 bug（已修）</b>：以前用 <c>Local</c> 算弹药费用，
/// 于是这类 **Global/Late 的"费用改 0"完全不减免弹药** ——
/// 打一次「得见光芒」（自己 3 = 弹药 6→3）之后，本该免费的那张攻击牌**又扣 3** ⇒ 一共 6 颗，
/// 玩家读起来就是"费用变 0 的效果在子弹模式里失效"（原注释还写着"这是刻意的"，现已作废）。</para>
///
/// <para><b>X 费牌（「沙丘」）是这套东西里唯一的硬骨头，但也是最强联动</b>：
/// 原版 X 的语义是"<b>X = 你实际花掉的能量</b>"（<c>CardModel.SpendResources</c> →
/// <c>SpendEnergy</c> 里 <c>CapturedXValue = amount</c>），而 <c>GetWithModifiers</c> 对 X 费牌
/// <b>提前 return</b>（根本进不了费用钩子）—— 所以费用钩子对 X 费牌完全无效。三条通道各自解决：</para>
/// <list type="number">
/// <item><b>X 改成弹药</b>：<c>ResolveEnergyXValue()</c> = <c>Hook.ModifyXValue(CombatState, this, CapturedXValue)</c>
/// （<c>CardModel.cs:1123</c>）→ 覆写 <see cref="ModifyXValue"/> 即可。原版范例是遗物 <c>ChemicalX</c>。</item>
/// <item><b>弹药照扣</b>：在 <c>BeforeCardPlayed</c> 里把"当时的弹药"<b>缓存下来</b>再扣
/// —— ⚠️ <b>顺序关键</b>：<c>BeforeCardPlayed</c> 早于 <c>OnPlay</c>，而 X 是在 <c>OnPlay</c> 里
/// 才通过 <see cref="ModifyXValue"/> 读出来的。要是直接读当前弹药，就会读到"已经扣完"的 0（X = 0，白打）。</item>
/// <item><b>能量退回</b>：X 费牌的能量是被 <c>SpendResources</c> 强制扣掉的（<c>GetAmountToSpend()</c>
/// 对 X 费返回"当前能量"，没有任何钩子能拦），所以在 <see cref="AfterEnergySpent"/> 里
/// <b>原额退回</b> —— 这样"能量不是这个回合的货币"依然成立。
/// </item>
/// </list>
///
/// <para>★★ <b>2026-10-16 用户拍板</b>：<b>弹药为 0 时 X 费牌【必须能打出】</b>。
/// 用户原话："严格意义上不算白打：因为可以触发如同装填模式这种牌" ——
/// X=0 的 X 费牌仍带**非 X 依赖**的效果，一律否决等于把它们一起封死。
/// 语义现在是：<b>X = 弹药数；弹药为 0 时 X = 0，但牌照常打出、能量照样退回</b>。
/// （原 <see cref="ShouldPlay"/> 会直接否决，那是错的，已修。）</para>
/// </summary>
[RegisterPower]
public sealed class BulletModePower : PowerModel, IModPowerAssetOverrides
{
    /// <summary>
    /// 用户 2026-10-06：「子弹模式添加词条：每回合开始获得 3 弹药」。
    /// 钩子用 <c>BeforeHandDraw</c> —— 与本工程「沙地兽巢穴」同一处，已验证可用。
    /// ⚠️ 弹药会被上限夹住（不能超过上限），所以这里是纯 +3、不抬上限。
    /// </summary>
    public override async Task BeforeHandDraw(
        Player player, PlayerChoiceContext choiceContext, ICombatState combatState)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not { } owner)
        {
            return;
        }

        AmmoCmd.AddAmmo(owner, _ammoPerTurn);
    }

    /// <summary>
    /// 每回合开始白给的弹药数（用户 2026-10-06）。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["AmmoPerTurn"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _ammoPerTurn = 3;

    /// <summary>从打出它的卡上取真实值；取不到就沿用默认值。</summary>
    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        // 从打出它的卡上取真实值（卡面数字改了就真的生效）；取不到就沿用默认值。
        if (cardSource is { } src && src.DynamicVars.ContainsKey("AmmoPerTurn"))
        {
            _ammoPerTurn = src.DynamicVars["AmmoPerTurn"].IntValue;
        }

        // ★ 2026-10-12：「子弹模式」的常驻弹幕（子弹绕在莱伊身边；消耗弹药时随机几颗飞出去）。
        //   这里只负责"挂上"，之后数量/发射都由 AmmoEvents + 弹幕节点自己驱动。
        RayMod.Art.CardFieldVfx.AttachBulletSwarm(MyOwner);

        return Task.CompletedTask;
    }


    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(BulletModePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    /// <summary>正在结算的 X 费牌（见类注释第 2 条：X 必须在扣弹药之前缓存下来）。</summary>
    private CardModel? _xCardInPlay;

    /// <summary>那张 X 费牌这一次的 X 值（= 扣弹药前的弹药数）。</summary>
    private int _xValueInPlay;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标只显示一个 1（没有"层数"语义）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>我这个主人（玩家自己的 Creature 上 <c>PetOwner</c> 为 null，两个都要判）。</summary>
    private Player? MyOwner => base.Owner?.Player ?? base.Owner?.PetOwner;

    /// <summary>
    /// 这张牌的<b>弹药费用</b> = "**把【我们自己】摘出去之后**，游戏算出来的当前费用"。
    ///
    /// <para><b>为什么不能直接用 <c>CostModifiers.All</c></b>：<c>All</c> 里包含
    /// <b>我们自己的"费用改 0"</b> ⇒ 读自己 = 恒 0 = 永远不扣弹药（老版本踩过的坑）。</para>
    ///
    /// <para><b>为什么也不能只用 <c>Local</c></b>（老版本的写法）：只吃"牌自己的"修正，
    /// 于是 <c>Global</c>/<c>Late</c> 那一类"费用变 0"（<c>FreeAttackPower</c> =
    /// 「狙击镜」/「得见光芒」给的"下一张攻击免费"）**完全不减免弹药** ⇒
    /// 2026-10-13 用户实机报了这件事："子弹模式下打一次得见光芒消耗 6 颗"。</para>
    ///
    /// <para><b>解法（★ 不依赖监听顺序）</b>：临时把自己挂起（<see cref="_suspendDepth"/>），
    /// 然后<b>让游戏自己再跑一遍费用钩子链</b> —— 那一次的结果就是"别人的修正都算上、只有我们没参与"的费用。
    /// 依据（反编译）：<c>CardEnergyCost.GetWithModifiers</c> 在 <c>Global</c> 时会调
    /// <c>Hook.ModifyEnergyCostInCombat</c>，后者<b>先跑一遍全部监听者、再跑一遍 Late</b>，
    /// 而 <c>FreeAttackPower</c> 就是在 <b>Late</b> 那一遍里对"手牌/出牌堆里的攻击牌"置 0 的。</para>
    ///
    /// <para>⚠️ 这是**纯查询**（钩子只在被挂起时返回"不改"），不产生任何副作用。</para>
    /// </summary>
    internal int AmmoCostOf(CardModel card)
    {
        // X 费牌：GetWithModifiers 对 CostsX 提前 return（根本进不了费用钩子），
        // 它们的弹药费用是"当前弹药全扣"，走 BeforeCardPlayed / ModifyXValue 那条路。
        if (card.EnergyCost.CostsX)
        {
            return 0;
        }

        _suspendDepth++;

        try
        {
            return Math.Max(0, card.EnergyCost.GetWithModifiers(CostModifiers.All));
        }
        finally
        {
            _suspendDepth--;
        }
    }

    /// <summary>
    /// "这次求值只是我们自己为了算弹药费用"—— 此时费用钩子<b>什么都不做</b>，
    /// 好让 <c>GetWithModifiers(All)</c> 算出"别人眼中的费用"。
    ///
    /// <para><b>★ 2026-10-13 联机适配：从 <c>bool</c> 改成<b>计数</b>。</b>
    /// 原来是个布尔，语义上是"我正挂起"。但 <c>GetWithModifiers(All)</c> 会跑**整条费用钩子链**，
    /// 链条里任何一个监听者只要**反过来再查一次价**（另一个 mod 的 Power、将来某张在本链里
    /// 比价的卡），内层那次 <c>finally</c> 就会把标志**提前复位** ⇒ 外层还在查，却已经
    /// "算上了我们自己" ⇒ 弹药费用算成 0。</para>
    ///
    /// <para>为什么这值得单独修：这个值最终会体现在**卡的 <c>energyCost</c>** 上，而卡的
    /// <c>energyCost</c> 是联机校验和的字段之一 ⇒ 两端算不一样就是
    /// <c>State divergence</c>，房主直接把客机踢回主菜单。计数版让"嵌套查询"各退各的，
    /// 只要有一层还在挂起，钩子就继续什么都不做。</para>
    /// </summary>
    private int _suspendDepth;

    /// <summary>
    /// 卡面用（<see cref="RayMod.Patches.BulletAmmoCostHintPatch"/>）：这张牌<b>现在</b>要花多少弹药、
    /// 以及它是不是 X 费牌。<b>返回 false = 现在没有可显示的弹药消耗</b>
    /// （0 费牌 / 弹药已空的 X 费牌 —— 后者本来就打不出来，见 <see cref="ShouldPlay"/>）。
    ///
    /// <para>⚠️ <b>纯查询，不参与结算</b>：三条结算路径（<see cref="ShouldPlay"/> /
    /// <see cref="BeforeCardPlayed"/> / <see cref="ModifyXValue"/>）<b>一个字都没改</b>。
    /// 两条语义都与它们同源，没有第二份公式：</para>
    /// <list type="bullet">
    /// <item>普通牌 → <see cref="AmmoCostOf"/>（<c>Local</c> 那条算式<b>全工程只有那一处</b>）；</item>
    /// <item>X 费牌 → 当前弹药（同 <c>ModifyXValue</c> 的卡面预览分支：X = 打出时的弹药，全扣）。
    /// ⚠️ 对 X 费牌<b>绝不能用</b> <see cref="AmmoCostOf"/>：<c>GetWithModifiers</c> 对 <c>CostsX</c>
    /// 提前 return，算出来恒为 0，显示成「消耗0点弹药」与实际「全扣」完全相反。</item>
    /// </list>
    /// </summary>
    internal bool TryGetAmmoCost(CardModel card, out int amount, out bool isX)
    {
        isX = card.EnergyCost.CostsX;

        if (isX)
        {
            amount = MyOwner is { } owner ? AmmoCmd.GetAmmo(owner) : 0;
            return amount > 0;
        }

        amount = AmmoCostOf(card);

        if (amount > 0)
        {
            return true;
        }

        // ★ 2026-10-13：**本来要花弹药、被效果减免到 0** 的牌也要显示（卡面写"消耗0点弹药。"）——
        //    否则玩家看不出"下一张攻击免费"在子弹模式里到底有没有生效（正是用户报的那个问题）。
        //    判据：牌自己的（Local）费用 > 0，而把别人的减免算进来之后是 0。
        return card.EnergyCost.GetWithModifiers(CostModifiers.Local) > 0;
    }

    /// <summary>这张牌归不归本模式管（我方的牌）。</summary>
    private bool AppliesTo(CardModel? card) =>
        card != null && MyOwner is { } owner && card.Owner == owner;

    /// <summary>钱够不够：弹药 ≥ 弹药费用。0 费牌恒为真。</summary>
    private bool CanAfford(CardModel card) =>
        MyOwner is { } owner && AmmoCmd.GetAmmo(owner) >= AmmoCostOf(card);

    // ===================== ① 费用 = 0（不能用能量） =====================

    public override bool TryModifyEnergyCostInCombat(
        CardModel card, decimal originalCost, out decimal modifiedCost)
    {
        modifiedCost = originalCost;

        // ★ 正在被 <see cref="AmmoCostOf"/> 求值 ⇒ 这次要算的是"**别人**眼中的费用"，我们什么都不做。
        //    （不能省：一旦这里改成 0，那次求值就恒为 0，弹药费用又永远是 0 了。）
        if (_suspendDepth > 0)
        {
            return false;
        }

        // X 费牌根本进不来（GetWithModifiers 对 CostsX 提前 return），它们走 ModifyXValue 那条路。
        if (!AppliesTo(card) || card.EnergyCost.CostsX)
        {
            return false;
        }

        // 不管弹药够不够，费用都显示 0 —— "能量不是这个回合的货币"。
        // 弹药不够的牌由 ShouldPlay 否决（变灰），而不是在这里露出一副"可以用能量买"的样子。
        modifiedCost = 0m;
        return true;
    }

    // ===================== ①b X 费牌：X = 你的弹药 =====================

    /// <summary>
    /// 子弹模式下，X 费牌的 X 改成<b>你当前的弹药</b>（原版语义是"X = 实际花掉的能量"，
    /// 见类注释）。原版 <c>ChemicalX</c> 就是靠这个钩子加 X 的。
    /// </summary>
    public override int ModifyXValue(CardModel card, int originalValue)
    {
        if (!AppliesTo(card) || !card.EnergyCost.CostsX)
        {
            return originalValue;
        }

        // 正在结算它自己 → 用**扣弹药之前**缓存下来的那个值（否则会读到已扣完的 0）。
        if (_xCardInPlay == card)
        {
            return _xValueInPlay;
        }

        // 其他时候（例如卡面预览）就报"现在打的话 X 会是多少"。
        return MyOwner is { } owner ? AmmoCmd.GetAmmo(owner) : originalValue;
    }

    // ===================== ② 手动出牌时否决"买不起"的牌 =====================

    public override bool ShouldPlay(CardModel card, AutoPlayType autoPlayType)
    {
        // 只管手动出牌：自动打出放行，否则 AutoPlay 会把牌白白丢进弃牌堆。
        if (autoPlayType != AutoPlayType.None)
        {
            return true;
        }

        if (MyOwner is not { } owner || card.Owner != owner)
        {
            return true;
        }

        // ★★ 2026-10-16 修（玩家实机反馈："子弹为 0 时，会错误地阻止 X 费牌打出"）。
        //
        //   ★ 用户拍板：**X 费牌在弹药为 0 时也必须能打出**。原话的意思是
        //   "严格意义上不算白打：因为可以触发如同装填模式这种牌" ——
        //   即 X=0 的 X 费牌仍然携带**非 X 依赖的效果**（装填、抽牌、加格挡…），
        //   一律否决掉等于把这些牌一起封死。
        //
        //   于是这里**不再按弹药否决 X 费牌**：让它照常打出，X 由 ModifyXValue 算成 0，
        //   能量仍由 AfterEnergySpent 原额退回 ⇒ 玩家不亏能量，只是 X 的效果为 0。
        //   ⚠️ 普通牌那一段（下面的 AmmoCostOf 判定）**保持否决**：它们的效果基本全靠弹药结算，
        //      弹药不够本来就打不出来 —— 这一条没有改。
        if (card.EnergyCost.CostsX)
        {
            return true;
        }

        int cost = AmmoCostOf(card);

        if (cost <= 0 || AmmoCmd.GetAmmo(owner) >= cost)
        {
            return true;
        }

        Log.Info($"[子弹模式] 弹药不足：{card.Id.Entry} 需要 {cost}，当前 {AmmoCmd.GetAmmo(owner)} → 打不出来");

        return false;
    }

    // ===================== ③ 真的扣弹药 =====================

    public override async Task BeforeCardPlayed(CardPlay cardPlay)
    {
        // ★★★ 2026-10-12 真 bug 修复：**在这一刻**把"这次出牌打谁"记给弹幕特效。
        //
        //   反编译 `CardModel.PlayCard` 的顺序是 BeforeCardPlayed(:1926) → CardPlayStarted(:1929)，
        //   而弹药正是在**这个方法里**被扣掉的 ⇒ 特效那边若还等 CardPlayStarted（`Play()`）才记目标，
        //   记到的永远是**上一张牌**的 TargetType。用户实机现象：
        //   **「防御」「装弹」这类无目标牌会照着上一张「打击」的敌人把子弹飞出去**。
        //   放进同一个方法（扣之前）就天然同序 —— 见铁律 71。
        if (cardPlay.Player == MyOwner)
        {
            RayMod.Art.CardFieldVfx.NoteBulletTargetsForPlay(cardPlay);
        }

        // 自动打出不花能量 → 也不该扣弹药。
        if (cardPlay.IsAutoPlay || !AppliesTo(cardPlay.Card) || MyOwner is not { } owner)
        {
            return;
        }

        // X 费牌：X = 现在的弹药，全部扣掉。
        // ⚠️ 必须在扣之前把值缓存好 —— OnPlay 里读 X 时弹药已经变少了（见类注释）。
        if (cardPlay.Card.EnergyCost.CostsX)
        {
            int x = AmmoCmd.GetAmmo(owner);

            if (x <= 0)
            {
                return;
            }

            _xCardInPlay = cardPlay.Card;
            _xValueInPlay = x;

            AmmoCmd.ConsumeAmmo(owner, x, cardPlay.Card.Type == CardType.Attack);

            Log.Info($"[子弹模式] {cardPlay.Card.Id.Entry}：X = 弹药 {x}（已用弹药支付）");

            return;
        }

        if (!CanAfford(cardPlay.Card))
        {
            return;
        }

        int cost = AmmoCostOf(cardPlay.Card);

        if (cost > 0)
        {
            // ★ 2026-10-16：把"是不是攻击牌"传下去 —— 非攻击牌只响装填音，不响开火音
            //   （玩家实机反馈："消耗子弹打出非攻击牌，也会播放子弹攻击的音效"）。
            AmmoCmd.ConsumeAmmo(owner, cost, cardPlay.Card.Type == CardType.Attack);
        }
        else if (cardPlay.Card.EnergyCost.GetWithModifiers(CostModifiers.Local) > 0)
        {
            // ★ 2026-10-13：牌自己本来要花弹药，但被"费用变 0"的效果减免掉了 ⇒ 一顆弹药都不扣。
            //   日志要留痕：用户实机验证"免费攻击在子弹模式里生效"就看这一行（铁律 35：打实际值）。
            Log.Info($"[子弹模式] {cardPlay.Card.Id.Entry}：费用被减免到 0 ⇒ 不扣弹药（免费生效）");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 出牌结束：清掉 X 缓存（下一次出牌重新算）。
    /// </summary>
    public override Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        // ★ 出牌结算结束 ⇒ "这一次的目标"作废（之后再扣的弹药不再拿它乱飞，见 CardFieldVfx.EndBulletPlay）。
        //   ★ 2026-10-13 联机适配：**只对"我自己打出的牌"作废**。这个钩子会为**每一个玩家**的每次
        //   出牌跑一遍，不判归属的话队友出牌会把**我**这次出牌的 `_bulletTargets` / `_bulletPlayActive`
        //   提前清掉（同样的静态单例问题，见 `CardFieldVfx.BulletMode.cs` 的联机门控注释）。
        if (MyOwner is { } myOwner && cardPlay?.Card?.Owner == myOwner)
        {
            RayMod.Art.CardFieldVfx.EndBulletPlay();
        }

        if (_xCardInPlay != null && cardPlay.Card == _xCardInPlay)
        {
            _xCardInPlay = null;
            _xValueInPlay = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// X 费牌的能量是 <c>SpendResources</c> <b>强制扣掉</b>的（<c>GetAmountToSpend()</c> 对 X 费
    /// 返回"当前能量"，没有任何钩子能拦），所以这里<b>原额退回</b> ——
    /// 这样"能量不是这个回合的货币"依然成立，而且这是<b>唯一</b>能做这件事的时机。
    ///
    /// <para>⚠️ 这个钩子在 <c>SpendResources</c> 里就被 await（早于 <c>BeforeCardPlayed</c>），
    /// 所以判据只能看卡牌本身，不能依赖上面那个 X 缓存。</para>
    /// </summary>
    public override async Task AfterEnergySpent(CardModel card, int amount)
    {
        if (amount <= 0 || !AppliesTo(card) || !card.EnergyCost.CostsX)
        {
            return;
        }

        if (MyOwner is not { } owner)
        {
            return;
        }

        await PlayerCmd.GainEnergy(amount, owner);

        Log.Info($"[子弹模式] {card.Id.Entry}：退回被强制扣掉的 {amount} 点能量（X 已改为弹药支付）");
    }
}
