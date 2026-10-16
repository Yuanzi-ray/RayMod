using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Runs;
using RayMod.Audio;
using RayMod.Characters;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（与工程其它文件一致）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

// ─────────────────────────────────────────────────────────────────────────────
//  语音触发器（2026-10-07；★ 2026-10-13 补"联机门控"）
//
//  全部挂在游戏的 **Hook.*** 静态钩子上 —— 一次覆盖全池，**不用改 92 张卡的代码**。
//  Entry.Initialize() 里的 `new Harmony("RayMod").PatchAll(assembly)` 会自动挂上这些补丁。
//
//  ⚠️ 这些补丁里**只准调用 VoicePlayer.Play / SfxPlayer.Play**
//     （它们内部把一切都包在 try/catch 里）。
//     声音出问题绝不能影响战斗结算 —— 铁律 9 的同款思路。
//  ⚠️ Hook 的方法是 `async Task`：Harmony 的 Prefix/Postfix 在**方法被调用时**同步执行
//     （不是 await 完成之后）。对我们"放个音效"来说正合适，别改成去 await 它。
//
//  ★★ 2026-10-13 新增：**联机门控**（多人模式适配的第一刀）
//     ─────────────────────────────────────────────────────────────────────────
//     STS2 的联机是"**确定性锁步**"：每个客户端都把**所有玩家**的钩子链各跑一遍
//     （官方注释原文：`ActionQueueSynchronizer.cs:182-189` "Since hooks are executed on
//      all peers…"）。所以这些补丁在联机时**会在每个客户端、为每个玩家各响一次** ——
//     队友打出莱伊的牌，我这边也会放**我的**莱伊语音；我玩别的角色时，队友的莱伊
//     语音照样在我的机器上响。
//     ⇒ 规矩：**"某个玩家的"语音/音效只由他自己的客户端播**。
//     判据一律用 `LocalContext.IsMe(那个玩家)` —— 单机下 `NetId = 1` 且玩家 NetId = 1，
//     `IsMe` 恒真 ⇒ **单机行为与本文件改造前逐字一致**（依据：`RunManager.cs:711-718`
//       `Launch()` 里 `LocalContext.NetId = NetService.NetId`；单机 service 硬编码 1，
//       `NetSingleplayerGameService.cs:13,23`）。
//     ⚠️ 门控**只用于表现层**（这里是放声音）。绝不可用它去挡"改游戏状态"的代码 ——
//        那会造成一端改、一端不改 ⇒ 校验和不匹配 ⇒ 客机被踢回主菜单（`ChecksumTracker.cs:151`）。
// ─────────────────────────────────────────────────────────────────────────────

namespace RayMod.Patches;

/// <summary>
/// ★★ <b>表现层的"本地玩家"门控</b>（2026-10-13 联机适配）。
///
/// <para><b>设计口径（三条，别再改回去）</b>：</para>
/// <list type="number">
/// <item><b>知道是谁的，就只由他自己播</b>：<see cref="NotMine"/> ——
/// <b>只有"明确属于别人"才返回 true</b>（拿不到 owner 时返回 false = 保持旧行为），
/// 所以这个门**永远不会把我们看不懂的情况变成无声**。</item>
/// <item><b>系统语音（战斗开始/胜利这类没有归属的）用</b> <see cref="LocalIsRayOrUnknown"/>：
/// 只有"本地玩家本人就是莱伊"才播。副作用是**顺带修掉一个单机场景**：
/// 装了 mod 但选了别的角色（例如铁甲战士）时，以前也会播莱伊的战斗台词。</item>
/// <item>判据<b>只有这一处</b>（铁律 50：同一件事只留一份判据），新加语音一律走这里。</item>
/// </list>
///
/// <para><b>为什么不是"联机才门控"</b>：单机下 `IsMe` 恒真，门控是无操作的 ——
/// 加特例反而多一条会漂移的分支。</para>
/// </summary>
internal static class VoiceGate
{
    private static readonly Logger Log = new("RayMod.Audio", LogType.Generic);

    /// <summary>只在"第一次真的挡下了一条声音"时打一行日志（联机实机排查用）。</summary>
    private static bool _loggedGate;

    /// <summary>同上，专指"不是莱伊"那一种（§S47）。</summary>
    private static bool _loggedNotRay;

    /// <summary>
    /// ★★★ <b>这条声音该不该由本机播</b> —— <b>所有</b>莱伊的语音/音效都必须过这一个门
    /// （2026-10-15 加强，见 §S47）。
    ///
    /// <para><b>两个条件同时成立才响</b>：</para>
    /// <list type="number">
    /// <item><b>这次动作属于本地玩家</b>（<c>LocalContext.IsMe</c>）—— 联机下钩子会在每个客户端
    /// 为每个玩家各跑一遍，队友的动作不该在我这边出声；</item>
    /// <item><b>这个玩家本人就是莱伊</b>（<c>Character is RayModCharacter</c>）——
    /// <b>这一条才是单机"玩别的角色也响"的解药</b>。</item>
    /// </list>
    ///
    /// <para><b>为什么第二条必须在这里、而不是在调用处</b>：原来只有
    /// <see cref="NotMine"/>（只判第 1 条），而单机下 <c>LocalContext.IsMe</c> <b>恒为真</b>
    /// ⇒ 装备了本 mod 但选了别的角色时，<b>休息点锻造 / 召唤 / 回合问候这些台词全都会响</b>。
    /// 那种"没有归属"的钩子（<c>AfterRestSiteSmith</c> / <c>AfterSummon</c> /
    /// <c>AfterPlayerTurnStart</c>）只有把"是不是莱伊"并进来才能挡住 —— 见 §S47.5。</para>
    ///
    /// <para><b>fail-open</b>：拿不到玩家、或判据抛异常 ⇒ <b>返回 false（照旧放音）</b>。
    /// 宁可多响一声，也绝不因为一个判据异常就让莱伊自己的声音整个消失。</para>
    /// </summary>
    /// <param name="owner">这次动作的归属玩家（卡片则传 <c>Card.Owner</c>）。</param>
    internal static bool MuteFor(Player? owner)
    {
        try
        {
            if (owner == null)
            {
                return false;
            }

            // ① 不是本地玩家的动作 ⇒ 本机不播（联机用）。
            if (!LocalContext.IsMe(owner))
            {
                LogOnce(owner);
                return true;
            }

            // ② 是本地玩家，但他玩的**不是莱伊** ⇒ 本机同样不播（单机 + 联机都要）。
            if (!IsRay(owner))
            {
                LogOnceNotRay(owner);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// true = <b>这条语音/音效明确属于别人</b> ⇒ 该闭嘴。<b>拿不到 owner 时为 false</b>（放行）。
    ///
    /// <para>⚠️ 它<b>只判"是不是我的"</b>，<b>不判"我是不是莱伊"</b> —— 所以单机下它恒为 false。
    /// 凡是在"没有归属"的钩子里用它，<b>都必须改用 <see cref="MuteFor"/></b>（§S47）。</para>
    /// </summary>
    internal static bool NotMine(Player? owner)
    {
        try
        {
            if (owner == null)
            {
                return false;
            }

            if (LocalContext.IsMe(owner))
            {
                return false;
            }

            LogOnce(owner);
            return true;
        }
        catch
        {
            // 判断出岔子就照旧放音 —— 宁可多一声，也不能让语音整个消失。
            return false;
        }
    }

    /// <summary>这个玩家本人是不是莱伊（模型类型判定，拿不到就返回 false = 不放音）。</summary>
    internal static bool IsRay(Player? player) => player?.Character is Characters.RayModCharacter;

    /// <summary>
    /// 没有归属的系统语音（战斗开始 / 区域清空）的判据：
    /// <b>只有"本地玩家本人就是莱伊"才播</b>；<b>拿不到本地玩家时返回 true</b>（保持旧行为）。
    /// </summary>
    internal static bool LocalIsRayOrUnknown(object? playerCollection)
    {
        try
        {
            if (playerCollection is not IPlayerCollection players || players.Players.Count == 0)
            {
                return true;
            }

            Player? me = LocalContext.GetMe(players);

            if (me == null)
            {
                // 进 run 之前 / run 清理之后 NetId 为 null（`RunManager.cs:1589`）——
                // 这种窗口期不该把声音吞掉。
                return true;
            }

            bool isRay = me.Character is RayModCharacter;

            if (!isRay && players.Players.Count > 1)
            {
                LogOnce(me);
            }

            return isRay;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>联机下每次启动只打一行，证明门控真的在工作（铁律 56：门控型逻辑必须有确认日志）。</summary>
    private static void LogOnce(Player owner)
    {
        if (_loggedGate)
        {
            return;
        }

        _loggedGate = true;

        try
        {
            Log.Info($"[联机门控] 语音/音效只播本地玩家那份：已忽略玩家 {owner.NetId} 的声音" +
                     "（联机时每个客户端都会为所有玩家跑一遍钩子，所以必须在这里分派）");
        }
        catch
        {
            // 日志失败不影响任何事。
        }
    }

    /// <summary>只在"第一次因为'本地玩家不是莱伊'而挡下声音"时打一行（§S47 实机排查用）。</summary>
    private static void LogOnceNotRay(Player owner)
    {
        if (_loggedNotRay)
        {
            return;
        }

        _loggedNotRay = true;

        try
        {
            Log.Info($"[语音门控] 本地玩家不是莱伊（{owner.Character?.GetType().Name ?? "?"}）" +
                     "⇒ 莱伊的语音/音效一律不播（这就是'玩别的角色也会响'那条 bug 的修复点）");
        }
        catch
        {
            // 日志失败不影响任何事。
        }
    }
}

/// <summary>打出一张牌 → 该卡配的台词（<b>只播"打牌那个人自己"的那份</b>）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
internal static class CardPlayedVoicePatch
{
    private static void Postfix(CardPlay cardPlay)
    {
        if (cardPlay?.Card == null)
        {
            return;
        }

        // ★ 联机：队友打出的牌不在我这边配音（钩子在每个客户端都会跑一遍）。
        // ★★ 2026-10-15：改用 MuteFor（它比 NotMine 多判"本地玩家是不是莱伊"）。
        if (VoiceGate.MuteFor(cardPlay.Card.Owner))
        {
            return;
        }

        // ★★ 2026-10-15 **撞名门**：这张表是按**简单类名**查的，而**卡池实证**有两条卡
        //    与我们的卡同名 —— 原版 `MegaCrit.Sts2.Core.Models.Cards.Feed`（**IroncladCardPool**）
        //    与 `…Cards.Darkness`（**DefectCardPool**）。
        //    ⇒ 不加这道门，那两个角色打出各自那张原版牌时会**命中我们的表项**：
        //    他们嘴里冒莱伊的台词 + 放莱伊的音效。**单机也会，与联机无关。**
        //    判据见 `Cards\CardIdentity.cs`（与 `CardPartArtPatch.IsOurCard` 同一口径）。
        string? className = Cards.CardIdentity.NameForMapLookup(cardPlay.Card);

        if (className == null)
        {
            return;
        }

        VoiceLine line = VoiceCatalog.ForCardClass(className);
        VoicePlayer.Play(line, VoiceCategory.Card);

        // 技能音效（一技能脱身矢 / 二技能广域警觉 / 三技能得见光芒）——
        // 和语音互不干涉：音效不受语音那 5 秒冷却和"每场一次"约束。
        SfxPlayer.Play(SfxCatalog.ForCardClass(className));
    }
}

/// <summary>战斗开始 → 清空"本场已播"记录 + 行动出发（<b>只有本地玩家是莱伊时才播</b>）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCombatStart))]
internal static class CombatStartVoicePatch
{
    /// <summary>
    /// ⚠️ 用 <c>__args</c> 而不是声明 <c>IRunState runState</c>：
    /// 这是"宽类型注入"（把 <c>IRunState</c> 塞进一个 <c>object</c> 形参），
    /// 而 **Harmony 的补丁失败是完全静默的**（工程铁律：`PatchAll` 自己吞异常）。
    /// `__args` 是 Harmony 保证能注入的写法，本工程 `CardPlayVfxPatch` 也用同一套。
    /// 判据：启动日志里的 `[补丁自检] Hook.BeforeCombatStart：… RayMod 补丁 N 个`，N 必须 ≥ 1。
    /// </summary>
    private static void Postfix(object[] __args)
    {
        // "本场已播"是纯本地记录 ⇒ 无条件清（这是状态清理，不是演出）。
        VoicePlayer.ResetCombat();

        object? runState = __args is { Length: > 0 } ? __args[0] : null;

        if (!VoiceGate.LocalIsRayOrUnknown(runState))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.BattleStart, VoiceCategory.Combat);
    }
}

/// <summary>玩家回合开始 → 问候（受"每场一次 + 全局冷却"约束，所以实际只在第 1 回合响）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterPlayerTurnStart))]
internal static class PlayerTurnVoicePatch
{
    private static void Postfix(Player player)
    {
        // ★ 联机：回合是**每个玩家各一个**（2 人局一轮会触发 2 次）⇒ 只认自己那个回合。
        if (VoiceGate.MuteFor(player))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.Weather, VoiceCategory.Combat);
    }
}

/// <summary>战斗胜利 → 这片区域暂时没有危险了。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCombatVictory))]
internal static class CombatVictoryVoicePatch
{
    /// <summary>同 <see cref="CombatStartVoicePatch"/>：用 <c>__args</c>，避免宽类型注入静默失效。</summary>
    private static void Postfix(object[] __args)
    {
        object? runState = __args is { Length: > 0 } ? __args[0] : null;

        if (!VoiceGate.LocalIsRayOrUnknown(runState))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.AreaClear, VoiceCategory.Combat);
    }
}

/// <summary>玩家死亡（战斗失败）→ 你们快走。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterDeath))]
internal static class PlayerDeathVoicePatch
{
    private static void Postfix(Creature creature)
    {
        // 只认玩家自己的死亡；小怪/沙地兽死不说话。
        if (creature?.Player == null)
        {
            return;
        }

        // ★ 联机：队友死了不该由"我的莱伊"来喊这句。
        if (VoiceGate.MuteFor(creature.Player))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.Defeat, VoiceCategory.Combat);
    }
}

/// <summary>召唤沙地兽 → 过来，沙地兽，我们要出发了（每场战斗第一次召唤时响）。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterSummon))]
internal static class SummonVoicePatch
{
    private static void Postfix(Player summoner)
    {
        // ★ 联机：召唤者是**出牌的那个人**；队友召唤不该在我这边说话。
        if (VoiceGate.MuteFor(summoner))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.Squad, VoiceCategory.System);
    }
}

/// <summary>休息点锻造升级卡牌 → 人们就不想追逐日月了吗。</summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterRestSiteSmith))]
internal static class SmithVoicePatch
{
    private static void Postfix(Player player)
    {
        // ★ 联机：休息点是**各自选各自的**（`RestSiteSynchronizer`）⇒ 只有我锻造才说话。
        if (VoiceGate.MuteFor(player))
        {
            return;
        }

        VoicePlayer.Play(VoiceLine.ChaseSunMoon, VoiceCategory.System);
    }
}
