using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Hud;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib;
using STS2RitsuLib.RunData;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Ammo;

/// <summary>
/// 弹药的战斗内状态（当前值 + 上限）。
///
/// <para><b>用户确认的规则（2026-10-03）：</b></para>
/// <list type="number">
/// <item><b>弹药每场战斗归零</b>（和层数一样）。</item>
/// <item><b>上限也每场重置回 <see cref="InitialCap"/></b> —— 所以"上限+3"这类牌
/// 只在本场战斗内有效，不是永久成长。</item>
/// <item>默认<b>能量优先支付</b>，弹药只是增伤燃料（弹药不足不阻止打牌，只要能量够卡面费用）。</item>
/// <item>「子弹模式」是<b>特例</b>：开启后该场战斗只能用弹药支付。</item>
/// <item><b>不能超过上限</b>，硬夹（<c>弹药+2</c> 于 5/7 → 7）。
/// <b>「无限」是唯一的例外</b>（2026-10-04 新增）：打出它的那个回合内不夹上限，
/// 见 <see cref="OverCapThisTurn"/>。</item>
/// </list>
///
/// <para>因为 2 的存在，本类其实是个纯战斗内容器，战斗开始由
/// <see cref="AmmoCmd.EnsurePower"/> 调 <see cref="ResetForCombat"/> 清空。</para>
/// </summary>
public sealed class AmmoState
{
    /// <summary>弹药初始上限（用户 2026-10-10 确认：上限保持 7 不变）。</summary>
    public const int InitialCap = 7;

    /// <summary>
    /// 每场战斗开始时<b>实际装填</b>的弹药数（2026-10-10 用户要求：上限 7，但开局只装 4 颗）。
    ///
    /// <para>⚠️ 这是"装填量"不是"上限" —— <see cref="Cap"/> 仍然回 <see cref="InitialCap"/>。
    /// 别把这个常量用到上限语义的地方（<c>AmmoPower.Data.Cap</c> / <c>CanonicalVars("Cap")</c>
    /// 都必须继续用 <see cref="InitialCap"/>）。</para>
    /// </summary>
    public const int InitialAmmo = 4;

    /// <summary>
    /// <b>每场战斗复位时的"基础上限"</b>（2026-10-12 新增，为遗物「弩」而设）。
    ///
    /// <para>= <see cref="InitialCap"/> + 玩家身上所有 <see cref="IAmmoCapModifier"/> 遗物的加成。
    /// 战斗内被卡牌改过的 <see cref="Cap"/> 只活一场；<b>基础</b>上限每场都会重新算，
    /// 所以遗物加成天然是"永久"的（用户 2026-10-12：「弩：弹药永久上限+3」）。</para>
    ///
    /// <para>⚠️ 这是**唯一**允许把 <see cref="InitialCap"/> 当上限基准的地方
    /// （<c>AmmoPower.Data.Cap</c> / <c>CanonicalVars("Cap")</c> 的初值仍然照旧用它，
    /// 它们的真实值由 <c>SyncFromData</c> 每场覆盖）。</para>
    /// </summary>
    public static int BaseCap(Player? player)
    {
        if (player == null)
        {
            return InitialCap;
        }

        try
        {
            int bonus = 0;

            foreach (var relic in player.Relics)
            {
                if (relic is IAmmoCapModifier modifier)
                {
                    bonus += modifier.AmmoCapBonus;
                }
            }

            return InitialCap + bonus;
        }
        catch (Exception e)
        {
            // 取遗物列表失败不能影响战斗开始（宁可退回默认上限）。
            AmmoRunData.Log.Warn($"[AmmoCmd] BaseCap 计算失败，退回 {InitialCap}: {e.Message}");
            return InitialCap;
        }
    }

    /// <summary>当前弹药（0 ~ <see cref="Cap"/>）。</summary>
    public int Current { get; set; }

    /// <summary>本场战斗的弹药上限。</summary>
    public int Cap { get; set; } = InitialCap;

    /// <summary>
    /// <b>本回合弹药可以超过上限</b>（稀有技能「无限」开的开关，2026-10-04 新增）。
    ///
    /// <para>默认 false —— 用户 2026-10-03 定的基础规则是"<b>不能超过上限</b>"，
    /// 所有写入路径都硬夹在 <see cref="Cap"/> 内。这张牌是<b>唯一的例外</b>，
    /// 而且只持续<b>一个回合</b>：由 <c>AmmoPower.AfterPlayerTurnStart</c> 在下个回合开始时
    /// 关掉开关，并把超出的部分夹回上限（"本回合内"的字面语义）。</para>
    ///
    /// <para>⚠️ 三个写入路径都必须认这个开关（<c>AddAmmo</c> / <c>SetAmmo</c> / <c>ModifyCap</c>）——
    /// 漏一个就会出现"加了弹药却被上限夹掉"或"抬上限顺手把超出的弹药削掉"的静默错误。</para>
    /// </summary>
    public bool OverCapThisTurn { get; set; }

    /// <summary>
    /// 每场战斗开始时复位。
    ///
    /// <para><b>2026-10-10 用户要求：上限保持 <see cref="InitialCap"/>（7）不变，但开局只装
    /// <see cref="InitialAmmo"/>（4）颗。</b>开局因此留出 7 - 4 = 3 颗的空缺，
    /// 前几回合要靠"弹药 +N"这类牌自己往枪里装。</para>
    ///
    /// <para>⚠️ <b>订正（2026-10-04 那版"开局满弹药"已废弃）</b>：旧注释里那条连带影响
    /// ——"<b>弹药 +N 类牌在开局第一回合会被上限夹掉</b>"——<b>现在不再成立</b>。
    /// 开局是 <c>Current = 4 &lt; Cap = 7</c>，有 3 颗空缺可装，
    /// <see cref="AmmoCmd.AddAmmo"/> 的硬夹（夹在 <see cref="Cap"/> 内）在开局第一回合
    /// <b>不会</b>吃掉任何装填量；只有真正装到 7 之后才会夹。
    /// 也就是说"攒弹药"的前期曲线被<b>还原</b>了，这是本次平衡想要的取舍。</para>
    ///
    /// <para><b>上限的复位行为没变</b>：<see cref="Cap"/> 仍每场回 <see cref="InitialCap"/>。</para>
    ///
    /// <para>之所以安全：调用方 <see cref="AmmoCmd.EnsurePower"/> 的顺序是
    /// "先 <c>ResetForCombat()</c> → 再 <c>PowerCmd.Apply&lt;AmmoPower&gt;(…, 1m, …)</c>
    /// → 最后 <c>SyncFromData(Current, Cap)</c>"。出生值 1 会被
    /// <c>AmmoPower._syncedFromData</c> 守卫挡掉（坑 8），
    /// 真正的显示值来自后面那次 <c>SyncFromData</c>，所以"4/7"能正确反映到角标。</para>
    /// </summary>
    public void ResetForCombat(int baseCap)
    {
        Cap = Math.Max(0, baseCap);
        Current = InitialAmmo;   // 上限由 baseCap 决定、开局只装 4 颗（2026-10-10 用户要求）
        OverCapThisTurn = false;
    }

    /// <summary>每场战斗开始时复位（基础上限 = <see cref="InitialCap"/>，没有遗物加成的场合）。</summary>
    public void ResetForCombat() => ResetForCombat(InitialCap);
}

/// <summary>
/// 弹药状态的存储入口（与层数同构）。
/// </summary>
public static class AmmoRunData
{
    private static PlayerRunSavedData<AmmoState>? _store;

    internal static readonly Logger Log = new("RayMod.Ammo", LogType.Generic);

    public static PlayerRunSavedData<AmmoState> Store =>
        _store ??= RitsuLibFramework.GetRunSavedDataStore(Entry.ModId)
            .RegisterPerPlayer<AmmoState>("ammo_state");
}

/// <summary>
/// 弹药的对外 API。所有卡牌都通过这里读写弹药，不要直接碰 <see cref="AmmoPower"/>。
///
/// <para>下面是 2026-10-03 排"AmmoPower 的 SYNC 日志一次都没出现"时加的
/// 异常兜底日志。RitsuLib / HookBus 会把钩子里的异常吞掉（只留栈不成因），
/// 所以这里在每一层都 try/catch + 明确日志，
/// 免得又出现"没生效但不报错"的黑盒。</para>
/// </summary>
public static class AmmoCmd
{
    // ===================== 读 =====================

    /// <summary>当前弹药。</summary>
    public static int GetAmmo(Player? player)
    {
        if (player == null)
        {
            return 0;
        }

        try
        {
            return AmmoRunData.Store.Get(player).Current;
        }
        catch (Exception e)
        {
            // 跑团还没开始（主菜单预览卡牌）时拿不到存档，返回 0 而不是抛异常。
            AmmoRunData.Log.Warn($"[AmmoCmd] GetAmmo failed: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    /// <summary>当前上限。</summary>
    public static int GetCap(Player? player)
    {
        if (player == null)
        {
            return AmmoState.InitialCap;
        }

        try
        {
            return AmmoRunData.Store.Get(player).Cap;
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Warn($"[AmmoCmd] GetCap failed: {e.GetType().Name}: {e.Message}");
            return AmmoState.InitialCap;
        }
    }

    /// <summary>是否还有弹药（「阿兰娜的车」用）。</summary>
    public static bool HasAmmo(Player? player) => GetAmmo(player) > 0;

    /// <summary>本回合弹药是否<b>可以超过上限</b>（「无限」开的开关）。</summary>
    public static bool IsOverCapThisTurn(Player? player)
    {
        if (player == null)
        {
            return false;
        }

        try
        {
            return AmmoRunData.Store.Get(player).OverCapThisTurn;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 开/关"本回合弹药可以超过上限"（「无限」用）。关掉时会把超出的部分夹回上限。
    /// </summary>
    public static void SetOverCapThisTurn(Player player, bool enabled)
    {
        try
        {
            var state = AmmoRunData.Store.Get(player);

            if (state.OverCapThisTurn == enabled)
            {
                return;
            }

            state.OverCapThisTurn = enabled;

            AmmoRunData.Log.Info(
                $"[AmmoCmd] 弹药超上限开关 -> {enabled}（current={state.Current} cap={state.Cap}）");

            if (!enabled)
            {
                ClampToCap(state);
            }

            SyncToPower(player);
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] SetOverCapThisTurn({enabled}) 失败: {e}");
        }
    }

    /// <summary>
    /// 回合结束时收尾：关掉超上限开关，并把超出的弹药夹回上限。
    /// 由 <c>AmmoPower.AfterPlayerTurnStart</c> 在<b>下个回合开始</b>时调用
    /// （"本回合内"= 开关只活一个回合）。
    /// </summary>
    public static void EndOverCapTurn(Player player)
    {
        if (!IsOverCapThisTurn(player))
        {
            return;
        }

        SetOverCapThisTurn(player, false);
    }

    /// <summary>
    /// 取"本回合与弹药有关的攻击牌<b>实际造成</b>的伤害总和"，并<b>清零</b>
    /// （「你只能走到这了」专用：一张牌只领一次账本，避免连打两张翻倍）。
    /// </summary>
    public static int TakeTurnAmmoDamage(Player? player)
    {
        var power = player?.Creature.GetPower<AmmoLedgerPower>();
        return power?.TakeDamageThisTurn() ?? 0;
    }

    /// <summary>
    /// 只看不清零 —— 给「你只能走到这了」的<b>卡面显示</b>用（卡面要能实时反映它会打多少）。
    /// </summary>
    public static int GetTurnAmmoDamage(Player? player) =>
        player?.Creature.GetPower<AmmoLedgerPower>()?.DamageThisTurn ?? 0;

    // ===================== 写 =====================

    /// <summary>
    /// 弹药 +delta，<b>硬夹在上限内</b>（用户确认：不能超过上限）。
    /// 返回实际增加量（可能因为已满而少于 delta）。
    ///
    /// <para>唯一的例外是「无限」开的 <see cref="AmmoState.OverCapThisTurn"/> 开关：
    /// 那时不夹上限（见 <see cref="UpperBound"/>）。</para>
    /// </summary>
    public static int AddAmmo(Player player, int delta)
    {
        try
        {
            var state = AmmoRunData.Store.Get(player);
            int before = state.Current;
            state.Current = Math.Clamp(state.Current + delta, 0, UpperBound(state));

            AmmoRunData.Log.Info(
                $"[AmmoCmd] AddAmmo +{delta}: {before} -> {state.Current} (cap={state.Cap})");

            SyncToPower(player);
            AmmoEvents.RaiseAmmoChanged(player, before, state.Current);

            // ── 音效（2026-10-07）：弹药的音**只有这里和 ConsumeAmmo 两个出口**，
            //    卡牌侧一律不播，免得同一次动作撞成两声 ──
            //    有沙地兽在场 → 「沙地兽取弹」（游戏里的 ray_sndbst_add_bullet，本来就是它补的弹）；
            //    没有沙地兽 → 普通装填音。
            //    ⚠️ 放在**状态落定、事件广播之后**：放音万一出岔子也影响不了弹药结算。
            if (state.Current > before)
            {
                // ★ 2026-10-15：带上 player ⇒ 门控会确认"本地玩家 + 他玩的是莱伊"才响
                //    （否则玩别的角色时，任何加弹药的来源都会放出莱伊的装填音，§S47）。
                Audio.SfxPlayer.Play(Beasts.BeastCmd.GetCount(player) > 0
                    ? Audio.Sfx.BeastTakeBullet
                    : Audio.Sfx.Reload, player);
            }

            return state.Current - before;
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] AddAmmo({delta}) 失败（弹药不会变化）: {e}");
            return 0;
        }
    }

    /// <summary>
    /// 消耗弹药，返回<b>实际消耗量</b>（弹药不足时少于请求量）。
    /// 「狙击」「霰弹枪」这类"消耗所有弹药"的牌要用返回值来决定效果强度。
    /// </summary>
    public static int ConsumeAmmo(Player player, int amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        try
        {
            var state = AmmoRunData.Store.Get(player);
            int before = state.Current;
            int used = Math.Min(amount, state.Current);
            state.Current -= used;

            AmmoRunData.Log.Info($"[AmmoCmd] ConsumeAmmo {used}/{amount}: now {state.Current}");

            SyncToPower(player);
            AmmoEvents.RaiseAmmoChanged(player, before, state.Current);

            // 音效：真的打出去子弹了才响开火声（弹药不足、一点没消耗时不响）。
            // 同样放在结算之后 —— 放音绝不能影响弹药。
            if (used > 0)
            {
                // ★ 2026-10-15：带 player（门控见 SfxPlayer.Play 重载，§S47）。
                Audio.SfxPlayer.Play(Audio.Sfx.Shot, player);
            }

            return used;
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] ConsumeAmmo({amount}) 失败: {e}");
            return 0;
        }
    }

    /// <summary>消耗全部弹药，返回实际消耗量。</summary>
    public static int ConsumeAllAmmo(Player player) => ConsumeAmmo(player, GetAmmo(player));

    /// <summary>弹药上限 +/-delta（本场战斗内有效）。上限降低时会同步压低当前弹药。</summary>
    public static void ModifyCap(Player player, int delta)    {
        try
        {
            var state = AmmoRunData.Store.Get(player);
            int before = state.Current;
            state.Cap = Math.Max(0, state.Cap + delta);

            // ⚠️ 只在**没开**超上限开关时才把当前弹药夹回来。
            //    开着的场合（「无限」）里抬上限也不该削弹药 —— 那会静默吃掉刚攒的超额弹药。
            if (!state.OverCapThisTurn)
            {
                state.Current = Math.Min(state.Current, state.Cap);
            }

            AmmoRunData.Log.Info($"[AmmoCmd] ModifyCap {delta}: cap={state.Cap} current={state.Current}");

            SyncToPower(player);
            AmmoEvents.RaiseAmmoChanged(player, before, state.Current);
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] ModifyCap({delta}) 失败: {e}");
        }
    }

    /// <summary>
    /// <b>把本场上限重新对齐到"基础上限"</b>（= 初始 <see cref="AmmoState.InitialCap"/> + 遗物加成），
    /// 差值走 <see cref="ModifyCap"/>（所以超上限开关、当前弹药的夹取都照旧）。
    ///
    /// <para><b>调用点</b>：遗物「弩」的 <c>AfterObtained</c> / <c>AfterRemoved</c> ——
    /// 战斗<b>中途</b>拿到或失去它时本场上限要立刻跟着动；下一场战斗由
    /// <c>ResetForCombat(BaseCap(player))</c> 自然生效，<b>两条路都指向同一个真值</b>。</para>
    /// </summary>
    public static void RecomputeCap(Player player)
    {
        try
        {
            int want = AmmoState.BaseCap(player);
            int have = AmmoRunData.Store.Get(player).Cap;

            if (want == have)
            {
                return;
            }

            AmmoRunData.Log.Info($"[AmmoCmd] 上限重算（遗物加成变化）：{have} -> {want}");

            ModifyCap(player, want - have);
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] RecomputeCap 失败: {e}");
        }
    }

    /// <summary>直接设定当前弹药（「乘算子弹」翻倍这类用）。</summary>
    public static void SetAmmo(Player player, int value)
    {
        try
        {
            var state = AmmoRunData.Store.Get(player);
            int before = state.Current;
            state.Current = Math.Clamp(value, 0, UpperBound(state));

            SyncToPower(player);
            AmmoEvents.RaiseAmmoChanged(player, before, state.Current);
        }
        catch (Exception e)
        {
            AmmoRunData.Log.Error($"[AmmoCmd] SetAmmo({value}) 失败: {e}");
        }
    }

    // ===================== 内部 =====================

    /// <summary>
    /// 当前弹药的上界：默认是 <see cref="AmmoState.Cap"/>，
    /// 「无限」生效时放到 <c>int.MaxValue</c>（等于"没有硬顶"）。
    /// </summary>
    private static int UpperBound(AmmoState state) =>
        state.OverCapThisTurn ? int.MaxValue : state.Cap;

    /// <summary>把当前弹药夹回上限内（关掉超上限开关时用）。</summary>
    private static void ClampToCap(AmmoState state)
    {
        if (state.Current > state.Cap)
        {
            AmmoRunData.Log.Info($"[AmmoCmd] 超上限结束：{state.Current} -> {state.Cap}");
            state.Current = state.Cap;
        }
    }

    // ===================== 战斗内同步 =====================

    /// <summary>
    /// 确保玩家身上挂着 <see cref="AmmoPower"/>。
    /// 和 <c>DepthCmd.EnsurePower</c> 同构：<b>"Power 不存在"= 本场战斗第一次</b>，
    /// 借此把弹药与上限一起归零/复位。
    ///
    /// 由初始遗物「下矿」在每个回合开始时调用（和层数一起）。
    /// </summary>
    public static async Task EnsurePower(
        PlayerChoiceContext choiceContext, Player player, CardModel? cardSource = null,
        bool announceHud = true, bool clearBus = true)
    {
        var power = player.Creature.GetPower<AmmoPower>();

        if (power == null)
        {
            AmmoRunData.Store.Get(player).ResetForCombat(AmmoState.BaseCap(player));

            power = await PowerCmd.Apply<AmmoPower>(
                choiceContext, player.Creature, 1m, applier: null, cardSource: cardSource);

            // 本回合"与弹药有关的攻击牌打了多少伤害"的账本（「你只能走到这了」用）。
            // 挂在这里是因为它的判据和 AmmoPower 完全同源：都是"本场战斗第一次"。
            // 它必须**先于**任何卡牌存在（账本要在打出那张牌之前就开始记账），
            // 所以不能由卡牌自己创建。
            await PowerCmd.Apply<AmmoLedgerPower>(
                choiceContext, player.Creature, 1m, applier: null, cardSource: cardSource);

            // ⚠️ 清空弹药事件总线（2026-10-04 补，与 DepthCmd.EnsurePower 里的
            //    DepthEvents.ClearAll() 对称）：订阅者是**静态**列表，
            //    Power 被移除时虽然会注销，但万一没走到那一步（战斗被强制结束等），
            //    上一个存档的效果会继续响应新战斗的弹药变化，且**完全不报错**。
            //    这里是"本场战斗第一次"的唯一判定点，也是唯一安全的清理时机。
            //
            // ★ 2026-10-13：加 `clearBus` 开关 —— **给"非莱伊玩家"挂系统时必须传 false**。
            //   理由见 `DepthCmd.EnsurePower` 同处注释：`ClearAll()` 是全局的，
            //   而那条路会在**每一个玩家**的回合开始被走到 ⇒ 会把别人刚注册的订阅清掉。
            if (clearBus)
            {
                AmmoEvents.ClearAll();
            }
        }

        // 同步显示。注意这里是"把自己拿到的 player 传进去"，
        // 而不是让 Power 内部再去解析 Owner —— 见 AmmoPower.SyncFromData 的说明。
        if (power != null)
        {
            var state = AmmoRunData.Store.Get(player);
            power.SyncFromData(state.Current, state.Cap);

            // 战斗刚开始时也把 HUD 计数器推一次（否则要等第一次弹药变动才出现）。
            // ★ 2026-10-13：加 `announceHud` 开关（非莱伊传 false，理由同上）。
            if (announceHud && LocalContext.IsMe(player))
            {
                ResourceHud.Ammo.Push(AmmoHudText(state));
            }
        }
    }

    /// <summary>
    /// ★★ 「无限」生效期间，HUD 上**上限那一位显示 <c>∞</c>**（用户 2026-10-12 定的演出落点：
    /// "后面那个数字才是上限"）。
    ///
    /// <para><b>为什么在这里改</b>：这里是 HUD 文本的<b>唯一汇聚点</b>（所有增减弹药的命令最后都走到这），
    /// 改一处就够了 —— 演出期间无论弹药怎么变，上限那一位都会是 ∞。</para>
    ///
    /// <para>判据与数值<b>同源</b>：<see cref="AmmoState.OverCapThisTurn"/>，它的关闭点是
    /// <c>AmmoPower.AfterPlayerTurnStart</c>（下回合开始并把超出的部分夹回上限）——
    /// 所以"显示 ∞"和"真的能超上限"是同一个开关，不可能对不上。</para>
    ///
    /// <para>⚠️ <b>这里只写 <c>"/"</c>，那个 <c>∞</c> 是 <c>Hud\HudInfinityFx</c> 画上去的</b> ——
    /// 2026-10-12 实机踩到：直接往 Label 里塞 <c>∞</c>（U+221E）<b>什么都不显示</b>
    /// （游戏字库里多半没有这个字形，而字库打包在 pck 里、离线查不了）。
    /// 所以符号改成<b>自己画</b>（两个相扣金环），Label 只负责那串一定渲染得出来的数字与斜杠。</para>
    /// </summary>
    private static string AmmoHudText(AmmoState state) =>
        state.OverCapThisTurn ? $"{state.Current}/" : $"{state.Current}/{state.Cap}";

    private static void SyncToPower(Player player)
    {
        var state = AmmoRunData.Store.Get(player);

        // ★ 能量球旁边的弹药计数器（2026-10-07）。这里**是唯一的汇聚点** ——
        //   所有增减弹药的命令最后都走到这，所以推一次就够，不用轮询、不用查树。
        //   多人对局里只推自己的那份（别人的弹药不该显示在我的 HUD 上）。
        if (LocalContext.IsMe(player))
        {
            ResourceHud.Ammo.Push($"{state.Current}/{state.Cap}");
        }

        // Power 不在战斗里就是 null（例如事件里改弹药）：存档已经更新，不用报错。
        var power = player.Creature.GetPower<AmmoPower>();

        if (power == null)
        {
            AmmoRunData.Log.Info("[AmmoCmd] SyncToPower: 没有 AmmoPower（战斗外？），只更新了存档");
            return;
        }

        power.SyncFromData(state.Current, state.Cap);
    }
}
