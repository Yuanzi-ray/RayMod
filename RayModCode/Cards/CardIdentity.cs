using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Cards;

/// <summary>
/// ★★★ <b>"这张卡是不是我们自己的" —— 全部按类名查表的映射都必须先过这一道</b>（2026-10-15）。
///
/// <para><b>为什么必须单独有一个判据</b>：本工程的语音 / 音效 / 命中特效 / 出牌特效 /
/// 卡面标签 / 动作映射<b>一律用 <c>card.GetType().Name</c>（简单类名）当键</b> ——
/// 这是刻意的（本地化键与中文名会随文案改动，类名不会）。但<b>简单类名不带命名空间</b>，
/// 而我们有两张卡与<b>原版别的角色</b>的卡撞名：</para>
///
/// <list type="table">
///   <item><term>RayMod.Cards.<b>Feed</b></term>
///       <description>原版 <c>MegaCrit.Sts2.Core.Models.Cards.Feed</c> —— <b>铁甲战士</b>的牌。**仍撞名**</description></item>
///   <item><term>~~RayMod.Cards.<b>Darkness</b>~~ ⇒ 已于 2026-10-16 改名为 <b>RayDarkness</b></term>
///       <description>原版 <c>…Cards.Darkness</c>（中文名「漆黑」）是 <b>故障机器人</b>的牌。
///       ⚠️ 我们那张「黑暗之下」原来也叫 `Darkness` ⇒ **卡 ID 撞成 `RAY_MOD_CARD_DARKNESS`**，
///       卡面美化 mod 会张冠李戴（玩家反馈）。**已改名，连卡图 / 本地化键 / 查表键一起改**。</description></item>
/// </list>
///
/// <para>⇒ 于是"装了本 mod 但玩别的角色"时，打原版的 <c>Feed</c>（**以及改名前的 <c>Darkness</c>**）
/// 会命中<b>我们</b>的表项：铁甲战士嘴里冒出莱伊的台词、放莱伊的音效。这就是用户 2026-10-15 报的
/// <b>"语音和音效别的角色也会触发"</b>（不是联机问题 —— 单机也这样）。
/// ⚠️ `Darkness` 改名后**只剩 `Feed` 一处**靠本判据兜着。</para>
///
/// <para><b>判据本身</b>：命名空间以 <c>RayMod.</c> 开头（我们的卡全在 <c>RayMod.Cards</c>）。
/// 与 <c>Patches\CardPartArtPatch.IsOurCard</c> <b>完全同一套口径</b> ——
/// 那处从 2026-10-10 起就是这么写的，一直是对的；本类只是把它提成公共判据
/// （铁律 50：同一件事只留一份判据）。</para>
///
/// <para><b>fail-open 的取舍</b>：拿不到类型就<b>返回 true</b>（放行），
/// 与 <c>VoiceGate.NotMine</c> 同一条原则 —— 宁可多响一声，也绝不因为一个判据异常
/// 就让莱伊自己的声音整个消失。</para>
///
/// <para>⚠️ <b>加新卡时别再手工检查撞名</b>：跑
/// <c>python -B _tools\audit_card_name_collisions.py</c> —— 它会拿 604 个原版卡类名
/// 逐个表对账（详见 §S47）。</para>
/// </summary>
internal static class CardIdentity
{
    private const string OurNamespacePrefix = "RayMod.";

    private static readonly Logger Log = new("RayMod.Audio", LogType.Generic);

    /// <summary>已经因为撞名挡下过的类名（每类只提示一次，避免刷日志）。</summary>
    private static readonly HashSet<string> WarnedNames = new();

    /// <summary>
    /// <b>这张卡是我们自己的吗</b>。false ⇒ 所有按类名查表的映射都<b>不许命中</b>。
    /// </summary>
    internal static bool IsOurs(CardModel? card)
    {
        if (card == null)
        {
            return false;
        }

        return IsOurType(card.GetType());
    }

    /// <summary>同上，收 <c>object</c>（本工程的补丁多数不引用游戏类型）。</summary>
    internal static bool IsOurs(object? card) => card != null && IsOurType(card.GetType());

    /// <summary>
    /// <b>按类名查映射表的总入口</b>：是我们自己的卡才返回类名，否则返回 <c>null</c>。
    ///
    /// <para>调用处写成 <c>string? n = CardIdentity.NameForMapLookup(card);
    /// if (n == null) return;</c> —— 一眼能看出"这里为什么不怕撞名"。</para>
    /// </summary>
    internal static string? NameForMapLookup(object? card) =>
        card != null && IsOurType(card.GetType()) ? card.GetType().Name : null;

    private static bool IsOurType(Type type)
    {
        try
        {
            string? ns = type.Namespace;

            if (ns != null && ns.StartsWith(OurNamespacePrefix, StringComparison.Ordinal))
            {
                return true;
            }

            // ★ 撞名的**唯一**表现：原版卡打进来时命中过我们的表 —— 这里留一条日志，
            //   否则"我们自己的卡忽然没声音"会完全查不到原因（铁律 35：日志要打实际发生的事）。
            WarnOnce(type);
            return false;
        }
        catch
        {
            return true;   // fail-open，见类注释
        }
    }

    private static void WarnOnce(Type type)
    {
        string name;

        try
        {
            name = type.FullName ?? type.Name;
        }
        catch
        {
            return;
        }

        lock (WarnedNames)
        {
            if (!WarnedNames.Add(name))
            {
                return;
            }
        }

        try
        {
            Log.Info($"[卡的归属] 忽略非本 mod 的卡：{name}" +
                     "（按类名查表的映射一律只认 RayMod.*，防原版同名卡误命中）");
        }
        catch
        {
            // 日志失败不影响任何事。
        }
    }
}
