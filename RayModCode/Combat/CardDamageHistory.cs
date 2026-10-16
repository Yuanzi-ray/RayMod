using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Combat;

/// <summary>
/// 「上一张牌造成了多少伤害」的查询（「脱身矢」用）。
///
/// <para><b>用户 2026-10-04 拍板的口径</b>：技能算 <b>0</b>、多段算<b>总和</b>、
/// <b>本场战斗内跨回合保留</b>（不是每回合清零）。</para>
///
/// <para><b>为什么不需要自己记账本</b>（原本以为要照抄 <c>AmmoLedgerPower</c>）:
/// 原版战斗史里 <c>DamageReceivedEntry</c> <b>带 <c>CardSource</c></b>
/// （<c>Core/Combat/History/Entries/DamageReceivedEntry.cs:17</c>），
/// 所以直接读历史就能还原"某一次出牌打了多少"。
/// （⚠️ <c>CreatureAttackedEntry</c> 只有 attacker + DamageResults、<b>没有</b> cardSource，
/// 别用它 —— 它无法区分是哪张牌打的。）</para>
///
/// <para><b>定位方式</b>：锚点是"本场<b>最近一次打完</b>的我方出牌" ——
/// 先找最后一个我方 <c>CardPlayFinishedEntry</c>，再取它之前最近的我方
/// <c>CardPlayStartedEntry</c>，两个下标之间、且 <c>CardSource != null</c>
/// 且 dealer 是我方的 <c>DamageReceivedEntry</c> 全部加起来。</para>
///
/// <para>⚠️ <b>为什么锚点必须是 finish 而不是 start（2026-10-11 修）</b>：
/// 卡面预览与出牌结算调的是<b>同一个</b> <see cref="LastCardDamage"/>，
/// 但两者的历史状态差一步 —— 出牌结算时本牌的 <c>CardPlayStarted</c> 已经入账
/// （<c>CardModel.cs:1929</c> 在 <c>OnPlay</c>（<c>CardModel.cs:1933</c>）之前），
/// 卡面预览时本牌还没开始结算。所以"最后一个我方 start"在结算途中=本牌、
/// 在预览时=<b>上一张牌自己</b> —— 拿它当锚点会让卡面预览又往前退一整张牌，
/// 显示值永远是"上上一张牌"的伤害（用户 2026-10-11 报"脱身矢动态伤害显示不正确"）。
/// 改成锚 finish 之后两条路径拿到同一个窗口：
/// 结算中本牌没有 finish → 最近 finish 仍是上一张牌；预览时上一张牌已 finish → 就是它本人。</para>
///
/// <para>这样三个口径全都自动成立：技能没有伤害条目 → 0；多段的每一下都落在窗口内 → 总和；
/// 窗口跨越回合 → 跨回合保留。⚠️ 用下标窗口（而不是"按卡牌实例匹配"）是<b>故意的</b>：
/// 同一张牌连续打两次时按实例匹配会把上一轮也算进来。</para>
/// </summary>
public static class CardDamageHistory
{
    private static readonly Logger Log = new("RayMod.Combat", LogType.Generic);

    /// <summary>
    /// 缓存：卡面预览会被战斗状态变更<b>反复</b>问到（每条历史入账都会触发一次卡面刷新，见
    /// <c>CombatStateTracker.OnCombatHistoryChanged</c>：<c>CombatHistory.Changed</c> →
    /// 下一帧 <c>CombatStateChanged</c> → <c>NPlayerHand.OnCombatStateChanged</c> → <c>NCard.UpdateVisuals</c>），
    /// 而"上一张牌的伤害"在两次出牌之间是<b>不变的</b>，所以按"战斗 + 历史条目数"缓存即可。
    ///
    /// <para>⚠️ 只按"条目数"当键<b>不够</b>：<c>CombatHistory</c> 在每场战斗结束/重置时会被
    /// <c>Clear()</c>（<c>CombatManager.cs:1199</c> / <c>CombatManager.cs:1316</c>），
    /// 下一场战斗从 0 重新累加 → 条目数会和上一场<b>撞上同一个值</b>，
    /// 那一刻缓存会吐出上一场的旧值（卡面显示一个不属于本场的数字，直到下一条历史入账）。
    /// 补上 <c>CurrentCombatId</c> 才真正不会过期。</para>
    ///
    /// <para>⚠️ 这层缓存的另一件事：<b>缓存只影响卡面，不会让结算取到过期值</b> ——
    /// 出牌时本牌的 <c>CardPlayStarted</c> 入账会把条目数 +1，必然打穿缓存（判据见报告 ②）。</para>
    /// </summary>
    private static Player? _cachePlayer;
    private static CombatId? _cacheCombatId;
    private static int _cacheEntryCount = -1;
    private static int _cacheValue;

    /// <summary>最近一次真正算出来的窗口（诊断日志用；「脱身矢」的卡面/结算判据日志读它）。</summary>
    private static Window _lastWindow = Window.Empty;

    /// <summary>最近一次「上一张牌」窗口：<see cref="Window.Damage"/> = 上一张牌造成的伤害。</summary>
    public static Window LastWindow => _lastWindow;

    /// <summary>
    /// 取"上一张牌造成的伤害"（实际掉血，被格挡的部分不算 —— 与本工程「你只能走到这了」同一口径）。
    /// 本场还没打完过牌时返回 0。出牌结算与卡面预览共用这一条路径。
    /// </summary>
    public static int LastCardDamage(Player? player)
    {
        if (player == null)
        {
            return 0;
        }

        try
        {
            CombatManager manager = CombatManager.Instance;
            CombatHistory history = manager.History;

            // Entries 的真身是 List，LINQ 的 Count() 会走 ICollection 快路径（O(1)），可以放心高频调用。
            int count = history.Entries.Count();
            CombatId? combatId = manager.CurrentCombatId;

            if (_cachePlayer == player && _cacheCombatId == combatId && _cacheEntryCount == count)
            {
                return _cacheValue;
            }

            Window window = Compute(player, history, combatId, count);

            _cachePlayer = player;
            _cacheCombatId = combatId;
            _cacheEntryCount = count;
            _cacheValue = window.Damage;
            _lastWindow = window;

            return window.Damage;
        }
        catch (Exception e)
        {
            // 读历史失败最多是"这张牌打 0 伤害"，绝不能把出牌流程搞崩。
            Log.Warn($"[上一张牌伤害] 读取失败（按 0 处理）: {e}");

            return 0;
        }
    }

    /// <summary>
    /// 一次「上一张牌伤害」查询的结果 + 它用的历史下标窗口。
    /// 全部字段都是诊断信息（下标是 <c>CombatHistory.Entries</c> 的下标），
    /// 出牌结算与卡面预览走同一条路径，所以两者拿到的窗口必须一致。
    /// </summary>
    public readonly record struct Window(
        int Damage,
        int PreviousStart,
        int PreviousFinish,
        int CurrentStart,
        CardPlay? ResolvingPlay,
        CombatId? Combat,
        int EntryCount)
    {
        /// <summary>本场还没有"上一张打完的牌"时的空窗口。</summary>
        public static readonly Window Empty = new(0, -1, -1, -1, null, null, 0);

        /// <summary>此刻有没有一张我方牌正在结算（true 时 <see cref="ResolvingPlay"/> 非空）。</summary>
        public bool Resolving => ResolvingPlay != null;

        /// <summary>
        /// 问这个值的是不是"正在结算的那张牌"本身：
        /// true = 出牌结算取值（伤害要用的就是它），false = 卡面预览取值（显示要用的就是它）。
        /// </summary>
        public bool IsResolvingCard(CardModel? card) => card != null && ReferenceEquals(ResolvingPlay?.Card, card);

        /// <summary>诊断串，例如：<c>previousStart=2 previousFinish=4 currentStart=-1(无) 在结算=否 条目数=5</c>。</summary>
        public string Describe()
        {
            string current = CurrentStart >= 0 ? CurrentStart.ToString() : "-1(无)";

            return $"previousStart={PreviousStart} previousFinish={PreviousFinish} currentStart={current} " +
                   $"在结算={(Resolving ? "是" : "否")} 条目数={EntryCount}";
        }
    }

    /// <summary>真正干活的那一遍（只在缓存失效时跑）。</summary>
    private static Window Compute(Player player, CombatHistory history, CombatId? combatId, int entryCount)
    {
        var entries = history.Entries.ToList();

        // 1) 「上一张牌」= 本场最近一次"打完"的我方出牌（有 finish 的那次）。
        //
        // ⚠️ 不能拿"最后一个我方 CardPlayStartedEntry"当锚点：卡面预览时本牌还没开始结算，
        //    最后一个 start 是"上一张牌"本身 —— 那样会往前多退一张牌，
        //    卡面显示值就比结算值慢一整张牌（见类注释）。
        int previousFinish = -1;

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is CardPlayFinishedEntry finished && finished.CardPlay.Card.Owner == player)
            {
                previousFinish = i;
                break;
            }
        }

        if (previousFinish < 0)
        {
            // 本场还没打完过任何一张我方牌（或第一张牌正在结算，它的 finish 还没入账）。
            return Window.Empty with { Combat = combatId, EntryCount = entryCount };
        }

        // 2) 这次出牌的 start（同一个 CardPlay 的 start 一定先于 finish 入账，CardModel.cs:1929 / 1961）。
        int previousStart = -1;

        for (int i = previousFinish - 1; i >= 0; i--)
        {
            if (entries[i] is CardPlayStartedEntry started && started.CardPlay.Card.Owner == player)
            {
                previousStart = i;
                break;
            }
        }

        if (previousStart < 0)
        {
            return Window.Empty with { Combat = combatId, EntryCount = entryCount };
        }

        // 3) 窗口内、由我方用卡牌造成的伤害，全部累加。
        int total = 0;

        for (int i = previousStart + 1; i < previousFinish; i++)
        {
            if (entries[i] is not DamageReceivedEntry damage)
            {
                continue;
            }

            // 只算"我方用牌打出去的"：排除敌方伤害、环境伤害、以及自己的自伤
            // （自伤走的是不带 dealer 的那条重载，CardSource 也不是牌）。
            if (damage.CardSource == null)
            {
                continue;
            }

            if ((damage.Dealer?.Player ?? damage.Dealer?.PetOwner) != player)
            {
                continue;
            }

            total += damage.Result.UnblockedDamage;
        }

        // 4) 诊断：此刻有没有一张我方牌正在结算（它的 start 在最近一次 finish 之后、finish 还没入账）。
        int currentStart = -1;
        CardPlay? resolvingPlay = null;

        for (int i = entries.Count - 1; i > previousFinish; i--)
        {
            if (entries[i] is CardPlayStartedEntry started && started.CardPlay.Card.Owner == player)
            {
                currentStart = i;
                resolvingPlay = started.CardPlay;
                break;
            }
        }

        return new Window(total, previousStart, previousFinish, currentStart, resolvingPlay, combatId, entryCount);
    }
}
