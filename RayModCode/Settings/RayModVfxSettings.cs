using System;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Audio;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Settings;

/// <summary>
/// <b>Mod 设置页：特效</b>（模组设置 → RayMod → 莱伊 · 特效）—— 2026-10-13 用户要求：
/// "**在设置里面加一个特效开关的按钮**"。
///
/// <para>★★ <b>设计口径（用户当天纠正过一次，别再往回改）</b>：第一版我按"每个特效一条"做了四个开关
/// （总开关 + 出牌特效 + 巨兽巨眼 + 无限 HUD），用户当场否掉：<br/>
/// "**这样不太好吧。太细了。到时候卡牌一多难道每个都有写开关吗**"<br/>
/// ⇒ **开关只按"类别"开，绝不按"卡"开**。新卡（以及以后加的任何演出）**自动归入类别**，
/// 永远不需要再加开关。所以这里只有**两个**：总开关 + 出牌特效。</para>
///
/// <para><b>纯开关，不碰规则</b>：关掉之后战斗逻辑一模一样（伤害/弹药/层数都不变），只是不放演出。
/// 想看"没特效是什么样"、或者演出跟别的 mod 打架时用。</para>
///
/// <para>⚠️ "命中特效"有它**自己的开关 + 三条滑条**，在「莱伊 · 界面」页（2026-10-08 就做好的），
/// 不往这一页搬 —— 那是历史设置，搬了老玩家的位置会变。</para>
///
/// <para>⚠️ 整包包在 try/catch 里：设置页注册失败也绝不能让 mod 启动失败。</para>
/// </summary>
public static class RayModVfxSettings
{
    private const string PageId = "raymod_vfx";

    // ⚠️ 键名就是存档里的字段名（`user://raymod_voice.cfg`），改了老玩家的设置会丢，别乱动。
    private const string MasterKey = "vfx";
    private const string FieldKey = "vfx_field";

    private static readonly Logger Log = new("RayMod.Vfx", LogType.Generic);

    public static void Register()
    {
        try
        {
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                page =>
                {
                    page.WithTitle(ModSettingsText.Literal("莱伊 · 特效"));
                    page.WithDescription(ModSettingsText.Literal(
                        "这个 mod 的战斗演出（自绘特效）。**只影响演出，不影响任何数值与规则**。" +
                        "开关按「类别」分，不按卡分 —— 以后加多少张卡都归入这两类，不会再多开关。"));

                    page.AddSection("vfx_master", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("特效总开关"));
                        section.WithDescription(ModSettingsText.Literal(
                            "一键关掉本 mod 的所有自绘战斗演出 —— 包括出牌特效、命中特效、" +
                            "巨兽的常驻巨眼、无限的 HUD 演出等等。想看「没特效是什么样」、" +
                            "或者演出跟别的 mod 打架时用这个。"));

                        section.AddToggle(
                            MasterKey,
                            ModSettingsText.Literal("特效总开关"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, MasterKey,
                                () => VoiceState.VfxEnabled,
                                value => VoiceState.VfxEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("开 = 有特效（默认）；关 = 全部不放，游戏照常。" +
                                                    "关掉它时下面那条就不起作用了。"),
                            (Func<bool>?)null);
                    });

                    page.AddSection("vfx_parts", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("分类"));
                        section.WithDescription(ModSettingsText.Literal(
                            "总开关开着时，这一条可以只关掉「卡牌触发的场上演出」" +
                            "（出牌层 / 弹幕 / 巨兽巨眼 / 无限 HUD …），而保留其它。"));

                        section.AddToggle(
                            FieldKey,
                            ModSettingsText.Literal("出牌特效"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, FieldKey,
                                () => VoiceState.FieldVfxEnabled,
                                value => VoiceState.FieldVfxEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("所有**卡牌触发**的场上演出。这是「类别」开关、" +
                                                    "不是逐卡的开关 —— 以后新加的卡自动归进这一类。"),
                            (Func<bool>?)null);
                    });

                    // ⚠️ 这里**不挂"改动即回调"**（那套 API 不确定，赌不起）：
                    //    长驻的巨眼由它自己每 0.5s 的自查收掉 —— 判定里带着开关，
                    //    所以关掉特效后**最多 0.5 秒**它就淡出（见 Art\CardFieldVfx.Feranmuts.cs 的 _Process）。
                    //    其余特效都是"下次触发时才判"，天然就是立即生效。
                },
                PageId);

            Log.Info("[特效] 设置页已注册（模组设置 → RayMod → 莱伊 · 特效；总开关 + 出牌特效，按类别不按卡）");
        }
        catch (Exception e)
        {
            Log.Warn($"[特效] 设置页注册失败（游戏照常，只是没地方调特效开关）：{e}");
        }
    }
}
