using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using RayMod.Ammo;
using RayMod.Beasts;
using RayMod.Depth;

namespace RayMod.Patches;

/// <summary>
/// <b>悬停在带【层数】/【弹药】/【沙地兽】的卡上时，旁边给出这三个概念的简短说明。</b>
///
/// <para><b>为什么必须走"接口判据"而不是"看卡面有没有这个词"</b>（2026-10-07 反编译确认）：
/// 游戏的悬停提示<b>完全不看卡面文字</b> —— 它只认 <c>CardModel.Keywords</c> 里的 <c>CardKeyword</c> 枚举。
/// 我们卡面里的 <c>[gold]层数[/gold]</c> 只是<b>上色</b>，不产生任何悬停。
/// 原版那行金色「消耗。」之所以能悬停，是因为卡声明了 <c>CanonicalKeywords => CardKeyword.Exhaust</c>
/// （见 <c>Abundance.cs</c>，它的文案里根本找不到 "Exhaust" 这个词）。</para>
///
/// <para><b>判据用三个标记接口</b>：<see cref="IDepthCard"/> / <see cref="IAmmoCard"/> / <see cref="ISandBeastCard"/>。
/// 已核对过：接口覆盖的卡与"文案里出现该词"的卡是<b>同一批</b>
/// （层数 33/34、弹药 27/27、沙地兽 26/26，差的那 1 个是我脚本把类名 `BackFan` 拼成 `BACKFAN`、
/// 而文案键是 `BACK_FAN` 造成的假差异）。用接口比搜字符串稳，也不怕以后换语言/改文案。</para>
///
/// <para><b>为什么用 Harmony Postfix 而不是给 92 张卡各写一遍</b>：
/// <c>ModCardTemplate.AdditionalHoverTips</c> 是逐卡覆写的，改 92 个文件没必要；
/// 挂在基类 <c>CardModel.HoverTips</c> 上一次覆盖全池。RitsuLib 自己也在这个 getter 上有两个 Postfix
/// （<c>CardModelHoverTipsModKeywordPatch</c> / <c>ContentSourceCardHoverTipsPatch</c>），共存安全。</para>
///
/// <para>文案在 <c>localization\{zhs,eng}\card_keywords.json</c>（<c>RAY_KW_*.title/.description</c>），
/// 和 cards.json 同目录、同名表会自动 merge 进 base 表。</para>
/// </summary>
[HarmonyPatch(typeof(CardModel), "get_HoverTips")]
internal static class CardConceptHoverTipPatch
{
    private const string Table = "card_keywords";

    private static bool _loggedFailure;

    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref IEnumerable<IHoverTip> __result)
    {
        try
        {
            var extra = new List<IHoverTip>(3);

            if (__instance is IDepthCard)
            {
                extra.Add(Make("RAY_KW_DEPTH"));
            }

            if (__instance is IAmmoCard)
            {
                extra.Add(Make("RAY_KW_AMMO", AmmoCapFor(__instance)));
            }

            if (__instance is ISandBeastCard)
            {
                extra.Add(Make("RAY_KW_BEAST"));
            }

            if (extra.Count == 0)
            {
                return;
            }

            // ⚠️ 用 Concat 而不是"强转成 List 再 Add"：原版实现目前返回 List，
            //    但那是内部细节，将来一改强转就静默失效（提示直接消失，很难查）。
            __result = __result.Concat(extra).ToList();
        }
        catch (Exception e)
        {
            // 补丁里抛异常会污染整个悬停链路：只记一次，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Entry.Logger.Error($"[悬停] CardConceptHoverTipPatch 失败（只记这一次）：{e}");
            }
        }
    }

    /// <summary>
    /// 概念悬停文案里的 <c>{AmmoCap}</c>：<b>上限的真实数字</b>。
    ///
    /// <para><b>为什么不能写死 7</b>（2026-10-12）：遗物「弩」让基础上限变成 10，
    /// 文案里那句"上限7"立刻变成骗人的（铁律 28）—— 所以走占位符 + 运行时注入。</para>
    ///
    /// <para>⚠️ <b>顺序要求：先判 <c>CombatState</c> 再碰 <c>Owner</c></b> ——
    /// <c>Owner</c> 的 getter 里有 <c>AssertMutable()</c>，图鉴里那张 canonical 卡一碰就抛
    /// <c>CanonicalModelException</c>（§S25.7 的 <c>BulletAmmoCostHintPatch</c> 踩过同一个坑）。
    /// 战斗外/拿 owner 失败时退回<b>基础值</b> <see cref="AmmoState.InitialCap"/>。</para>
    /// </summary>
    private static int AmmoCapFor(CardModel card)
    {
        try
        {
            if (card.CombatState != null && card.Owner is { } owner)
            {
                return AmmoCmd.GetCap(owner);
            }
        }
        catch (Exception)
        {
            // 拿不到就用基础值 —— 出问题也绝不能让整条悬停消失。
        }

        return AmmoState.InitialCap;
    }

    /// <summary>
    /// 造一条概念悬停。
    /// ⚠️ 文案里**写了** <c>{AmmoCap}</c> 就必须**总能**注入：漏注入 ⇒ SmartFormat 渲染失败 ⇒ 整段悬停空白。
    /// </summary>
    private static HoverTip Make(string stem, int? ammoCap = null)
    {
        var title = new LocString(Table, $"{stem}.title");
        var description = new LocString(Table, $"{stem}.description");

        if (ammoCap.HasValue)
        {
            description.Add("AmmoCap", ammoCap.Value);
        }

        return new HoverTip(title, description);
    }
}
