using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（同 BeastArt.cs）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Patches;

/// <summary>
/// ★ 2026-10-10：<b>把卡框 / 立绘外框换成莱伊自己的</b>（用户："卡牌太素了"）。
///
/// <para><b>为什么"素"</b>：卡牌的视觉是好几层拼的，我们原来<b>只填了卡面图</b>
/// （92 张卡全是 <c>new CardAssetProfile(PortraitPath: …)</c>）。卡框的形状是原版
/// 三张通用贴图（按牌型 attack/skill/power），只是被池子的
/// <c>PoolFrameMaterial</c> 染了色 —— 所以是"颜色对、但不属于莱伊"的卡。</para>
///
/// <para><b>为什么用 Harmony Postfix 而不是给 92 张卡各写一遍</b>：
/// RitsuLib 的换图通道有两条 ——
/// ① <c>IModCardAssetOverrides.CustomFramePath</c>：<b>逐卡</b>，要改 92 个文件；
/// ② 角色级 <c>ModCharacterOwnedVisualOverrideHelper</c>：它走的是
/// <c>TryGetVanillaCardVisualOverrideForContext</c>，<b>只认原版卡 ID</b>，对我们自己的卡<b>无效</b>。
/// 所以挂在 <c>CardModel.Frame</c> / <c>CardModel.PortraitBorder</c> 这两个 getter 上，
/// 一次覆盖全池（和 <c>CardConceptHoverTipPatch</c> 挂 <c>get_HoverTips</c> 同一个套路）。</para>
///
/// <para><b>配色为什么不会跑偏</b>：卡框会被 <c>PoolFrameMaterial</c> 做【替换色相】
/// （RitsuLib 的 replace-hue shader：保留明度/饱和度，只换色相）。我们的新卡框
/// <b>直接用主题青 + 银</b>作画 —— 青过 shader 仍是青，银（低饱和）保持银。
/// ★ 特别地：<c>mix(vec3(value), target_color*value*gain, saturation)</c> 意味着
/// <b>低饱和的银灰不会被染色</b>，这是这套零件的"银 + 青"双色语言能成立的原因。</para>
///
/// <para>资源在 <c>res://RayMod/images/cards/parts/</c>（<b>必须在 res://RayMod/** 下</b>，
/// 否则导出静默跳过 —— 铁律 21）。缺图就静默沿用原版，绝不因为美术问题报错。</para>
/// </summary>
[HarmonyPatch(typeof(CardModel), "get_Frame")]
internal static class CardFrameTexturePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref Texture2D __result)
    {
        CardPartArt.Apply(__instance, ref __result, "card_frame", byRarity: true);
    }
}

/// <summary>立绘外框那一张（和卡框同源，见 <see cref="CardPartArt"/>）。</summary>
[HarmonyPatch(typeof(CardModel), "get_PortraitBorder")]
internal static class CardPortraitBorderTexturePatch
{
    [HarmonyPostfix]
    private static void Postfix(CardModel __instance, ref Texture2D __result)
    {
        // 立绘外框只有【牌型】一档：它是卡图的画框，稀有度画在卡框上，别做两遍。
        CardPartArt.Apply(__instance, ref __result, "card_portrait_border", byRarity: false);
    }
}

/// <summary>两个补丁共用的取图逻辑。</summary>
internal static class CardPartArt
{
    private static readonly Logger Log = new("RayMod.Art", LogType.Generic);

    private const string Dir = Entry.ResPath + "/images/cards/parts";

    private static bool _loggedFailure;

    /// <summary>把 <paramref name="result"/> 换成我们的零件图；任何异常都只记一次日志。</summary>
    /// <param name="prefix">资源前缀：<c>card_frame</c> 或 <c>card_portrait_border</c>。</param>
    /// <param name="byRarity">
    /// 是否按稀有度分档取图。<b>只有卡框分档</b>：稀有度徽记（用户给的蚀刻章）是画在
    /// 卡框文字面板右下角的 —— 立绘外框不分档，否则稀有度会被画两遍。
    /// </param>
    internal static void Apply(CardModel? card, ref Texture2D? result, string prefix, bool byRarity)
    {
        try
        {
            if (card == null || !IsOurCard(card))
            {
                return;
            }

            // ★ 2026-10-10：先古牌**保持原样**（用游戏自己的零件）。
            //   用户："其他原有的东西别动！" —— 本轮**不动**先古牌，所以要换的时候再说。
            //   先古档的卡框图（card_frame_*_ancient_ray.png）仍然生成着，方便以后一行打开。
            if (card.Rarity == CardRarity.Ancient)
            {
                return;
            }

            string? kind = KindOf(card.Type);

            if (kind == null)
            {
                return;
            }

            string? rarity = byRarity ? RarityOf(card.Rarity) : null;

            // ★ 右上角机制图标（层数/沙地兽/弹药）。用户 2026-10-10：
            //   "把层数/沙地兽/弹药的 power 图标对应倾斜放上去，可以超出卡牌边缘。如果没有的就不用。"
            //   映射由生成的 CardTagMap.g.cs 提供（扫卡牌源码得出，改卡后重跑 draw_card_parts.py）。
            string tags = byRarity ? TagsOf(card) : string.Empty;

            // 取图顺序：精确档（带图标）→ 无图标档 → 牌型通用档。
            // 通用档是"分档图还没来得及画"的保险，有了就绝不会掉回原版零件。
            foreach (string name in Candidates(prefix, kind, rarity, tags))
            {
                string path = $"{Dir}/{name}";

                if (!ResourceLoader.Exists(path))
                {
                    continue;
                }

                var tex = ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse);

                if (tex != null)
                {
                    result = tex;
                    return;
                }
            }

            WarnOnce($"[卡框] 缺资源 {Dir}/{prefix}_{kind}_ray.png（含稀有度与图标分档）—— 这些卡沿用原版零件");
        }
        catch (Exception e)
        {
            // 美术出问题绝不能影响游戏能不能跑（同"音效/美术失败不影响结算"的思路）。
            WarnOnce($"[卡框] 换图失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>该卡涉及的机制图标组（"a"=弹药 "b"=沙地兽 "d"=层数；可能为空串 = 右上角不放）。</summary>
    private static string TagsOf(CardModel card) =>
        CardTagMap.Tags.TryGetValue(card.GetType().Name, out string? t) ? t : string.Empty;

    /// <summary>候选资源名（按优先级）。</summary>
    private static IEnumerable<string> Candidates(string prefix, string kind, string? rarity, string tags)
    {
        if (rarity != null)
        {
            if (tags.Length > 0)
            {
                yield return $"{prefix}_{kind}_{rarity}_{tags}_ray.png";
            }

            yield return $"{prefix}_{kind}_{rarity}_ray.png";
        }

        yield return $"{prefix}_{kind}_ray.png";
    }

    /// <summary>
    /// 稀有度 → 资源后缀。★ 2026-10-10 用户拍板："稀有+ 和罕见用我的素材就行了"
    /// —— 罕见(uncommon) 用<b>图鉴章</b>，稀有(rare) 及以上用更大的<b>探照灯章</b>，
    /// 普通/基础不戴章（保持干净）。这一档只影响卡框上那枚银灰铭牌的有无与大小。
    /// </summary>
    private static string? RarityOf(CardRarity rarity) => rarity switch
    {
        CardRarity.Basic => "basic",
        CardRarity.Common => "common",
        CardRarity.Uncommon => "uncommon",
        CardRarity.Rare => "rare",
        CardRarity.Ancient => "ancient",
        _ => null,
    };

    /// <summary>只换我们自己的卡（全部在 <c>RayMod.*</c> 命名空间下）。</summary>
    private static bool IsOurCard(CardModel card)
    {
        string? ns = card.GetType().Namespace;
        return ns != null && ns.StartsWith("RayMod.", StringComparison.Ordinal);
    }

    /// <summary>牌型 → 资源后缀。原版把 Status/Curse/Quest 都按 Skill 的框处理，这里照做。</summary>
    private static string? KindOf(CardType type) => type switch
    {
        CardType.Attack => "attack",
        CardType.Skill => "skill",
        CardType.Power => "power",
        CardType.Status => "skill",
        CardType.Curse => "skill",
        CardType.Quest => "skill",
        _ => null,
    };

    private static void WarnOnce(string message)
    {
        if (_loggedFailure)
        {
            return;
        }

        _loggedFailure = true;
        Log.Warn(message);
    }
}
