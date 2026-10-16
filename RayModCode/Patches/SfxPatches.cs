using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using RayMod.Audio;

namespace RayMod.Patches;

// ─────────────────────────────────────────────────────────────────────────────
//  音效抑制补丁（2026-10-08）
//
//  背景：莱伊的三个技能卡（脱身矢 / 广域警觉 / 得见光芒）**都是 CardType.Attack**，
//  所以它们出牌时既会走游戏的 AttackSfx（我们已换成普攻音，见 Audio/CharacterSfx），
//  又会被 SfxCatalog 放各自的技能音 —— 两条路都会响，撞成两声。
//
//  这里只压"当前这张卡自带技能音"时的那一条**普攻**事件，别的一律放行：
//  · 只认 VirtualFmodEventRegistry 注册的 raymod_attack（我们自己那条）；
//  · 只在 SpineAnimDispatch.CurrentCard 属于那三张卡时压。
//  于是：普通攻击牌 = 普攻音；三张技能牌 = 各自的技能音。
//
//  ★ 2026-10-13 联机适配：补一道 <c>LocalContext.IsMine(CurrentCard)</c>。
//    `SpineAnimDispatch.CurrentCard` 是**静态**的（"此刻正在结算的那张牌"），而联机下
//    每个客户端都为**所有玩家**跑一遍钩子 ⇒ 队友打出那三张技能卡时，我的
//    `CurrentCard` 会变成**他的**卡，于是**我自己**莱伊的普攻音被误压掉。
//    加上"这张卡是我的"即可（单机恒真，行为不变）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>三张技能卡出牌时，压掉游戏自己那条普攻音（避免和技能音撞声）。</summary>
[HarmonyPatch(typeof(SfxCmd), nameof(SfxCmd.Play), new[] { typeof(string), typeof(float) })]
internal static class SuppressGenericAttackSfxPatch
{
    private static bool Prefix(string sfx)
    {
        try
        {
            if (sfx == CharacterSfx.AttackEvent
                && SfxCatalog.CurrentCardHasOwnSfx
                && LocalContext.IsMine(Art.SpineAnimDispatch.CurrentCard))
            {
                return false;   // 跳过这次播放（技能音由 SfxCatalog 负责）
            }
        }
        catch
        {
            // 判断出岔子就照常放音 —— 宁可多一声，也不能把音效整个吃掉。
        }

        return true;
    }
}
