using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using RayMod.Characters;

namespace RayMod.Patches;

/// <summary>
/// <b>给莱伊的卡背换图案</b>（跑团历史 / 多人对局面板里那张小卡）。
///
/// <para><b>原版机制</b>：卡背<b>不是</b>每个角色一张图 —— 全游戏只有一张共享贴图
/// <c>res://images/packed/run_history/card_back.png</c>（128×128，一个灰色空心圆角框），
/// <c>NTinyCard.SetCardBackColor</c> 只是把它的 <c>Modulate</c> 设成
/// <c>cardPool.DeckEntryCardColor</c>（莱伊 = 主题色青蓝）。所以原版在莱伊这里是"一块平的青色"。</para>
///
/// <para><b>本补丁做什么</b>：Postfix 拦 <c>SetCardBackColor</c>，若卡池是
/// <see cref="RayModCardPool"/> 就把 <c>%CardBack</c> 的 Texture 换成我们那张。
/// 选这个钩子是因为 <c>SetCard</c> 和 <c>Set</c> <b>都会</b>调它，一处覆盖两个入口；
/// 而小卡是<b>复用</b>的，每次设置都跑 → 从别的卡池切回莱伊也能正确换回来。</para>
///
/// <para><b>⚠️ 为什么不反射 <c>_cardBack</c> 字段</b>：0.111.0 里 <c>NTinyCard</c> <b>有两个</b>同名成员 ——
/// 静态只读 <c>StringName _cardBack = "_cardBack"</c>（属性绑定用）和实例字段 <c>TextureRect _cardBack</c>。
/// 按名字反射有歧义风险，所以直接用游戏自己的唯一节点名 <c>%CardBack</c>（<c>_Ready</c> 也是这么取的）。</para>
///
/// <para><b>⚠️ 打的是私有方法</b>：不能用 <c>nameof</c>，所以成员名一旦在别的游戏版本里改掉，
/// <c>PatchAll</c> 会在启动时抛异常、整个模组加载失败。升级游戏版本时请先在
/// <c>sts2.dll</c> 里确认 <c>NTinyCard.SetCardBackColor</c> 仍然存在
/// （2026-10-06 已在 0.111.0 上核对过）。</para>
/// </summary>
[HarmonyPatch(typeof(NTinyCard), "SetCardBackColor")]
internal static class RayModCardBackPatch
{
    /// <summary>换卡背的图；加载失败只记一次日志，之后不再重试。</summary>
    private static Texture2D? _cardBackTexture;

    private static bool _loadAttempted;

    private static bool _loggedFailure;

    [HarmonyPostfix]
    private static void Postfix(NTinyCard __instance, CardPoolModel cardPool)
    {
        try
        {
            // 只换莱伊的卡池；其他角色/事件/无色池保持原版。
            if (cardPool is not RayModCardPool || !GodotObject.IsInstanceValid(__instance))
            {
                return;
            }

            if (!_loadAttempted)
            {
                _loadAttempted = true;

                _cardBackTexture = ResourceLoader.Load<Texture2D>(
                    $"{Entry.ResPath}/images/characters/RayMod_card_back.png");

                if (_cardBackTexture == null)
                {
                    Entry.Logger.Warn("[卡背] 载入 RayMod_card_back.png 失败，继续用原版卡背");
                }
            }

            if (_cardBackTexture == null)
            {
                return;
            }

            var rect = __instance.GetNodeOrNull<TextureRect>("%CardBack");

            if (rect != null && GodotObject.IsInstanceValid(rect))
            {
                rect.Texture = _cardBackTexture;
            }
        }
        catch (Exception e)
        {
            // 补丁里抛异常会污染整个调用点：只记一次，绝不再抛。
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Entry.Logger.Error($"[卡背] RayModCardBackPatch 失败（只记这一次）：{e}");
            }
        }
    }
}
