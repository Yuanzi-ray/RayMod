using System;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Audio;
using RayMod.Hud;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Settings;

/// <summary>
/// <b>Mod 设置页：界面</b>（模组设置 → RayMod → 莱伊 · 界面）。
///
/// <para>2026-10-08 从「莱伊 · 语音」里**拆出来**的：HUD 位置跟语音毫无关系，
/// 塞在一起找起来费劲（用户原话："为什么 ui 设置要放在语音里啊"）。
/// 以后 HUD/界面类的设置都加在这一页。</para>
///
/// <para><b>为什么 HUD 位置要做成滑条</b>：计数器挂在能量球右侧，而那一角同时挤着
/// 能量球、<b>游戏自己的 Power 图标框</b>和手牌 —— 我按代码猜了两版都撞上东西，
/// 与其继续猜屏幕坐标，不如把旋钮交给用户当场调（改完立即生效，数值存在
/// <c>user://raymod_voice.cfg</c>，重启不丢）。</para>
///
/// <para>⚠️ 整包包在 try/catch 里：设置页注册失败也绝不能让 mod 启动失败。</para>
/// </summary>
public static class RayModHudSettings
{
    private const string PageId = "raymod_hud";

    // ⚠️ 键名就是存档里的字段名，改了老玩家的设置会丢，别乱动。
    private const string HudOffsetXKey = "hud_offset_x";
    private const string HudOffsetYKey = "hud_offset_y";
    private const string HudScaleKey = "hud_scale";
    private const string HitVfxKey = "hit_vfx";
    private const string HitVfxScaleKey = "hit_vfx_scale";
    private const string HitVfxBrightKey = "hit_vfx_bright";
    private const string HitVfxSpeedKey = "hit_vfx_speed";

    private static readonly Logger Log = new("RayMod.Hud", LogType.Generic);

    public static void Register()
    {
        try
        {
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                page =>
                {
                    page.WithTitle(ModSettingsText.Literal("莱伊 · 界面"));
                    page.WithDescription(ModSettingsText.Literal(
                        "战斗界面上自绘元素的位置与大小（改完立即生效，不用重启）。"));

                    page.AddSection("hud_counter", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("战斗 HUD 计数器"));
                        section.WithDescription(ModSettingsText.Literal(
                            "层数 / 弹药两个计数器。那一角还挤着能量球、游戏自己的 Power 图标框和手牌，" +
                            "重叠时用这三条挪开。"));

                        section.AddSlider(
                            HudOffsetXKey,
                            ModSettingsText.Literal("左右偏移"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HudOffsetXKey,
                                () => HudLayout.OffsetX,
                                value => { HudLayout.OffsetX = value; HudLayout.NotifyChanged(); },
                                VoiceState.Save, SaveScope.Profile),
                            -400.0, 400.0, 4.0,
                            value => $"{value:0}px",
                            ModSettingsText.Literal("正数往右。被手牌挡住就往左挪（负数）。"));

                        section.AddSlider(
                            HudOffsetYKey,
                            ModSettingsText.Literal("上下偏移"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HudOffsetYKey,
                                () => HudLayout.OffsetY,
                                value => { HudLayout.OffsetY = value; HudLayout.NotifyChanged(); },
                                VoiceState.Save, SaveScope.Profile),
                            -400.0, 400.0, 4.0,
                            value => $"{value:0}px",
                            ModSettingsText.Literal("正数往下。被 Power 图标框挡住就往上挪（负数）。"));

                        section.AddSlider(
                            HudScaleKey,
                            ModSettingsText.Literal("大小"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HudScaleKey,
                                () => HudLayout.Scale,
                                value => { HudLayout.Scale = value; HudLayout.NotifyChanged(); },
                                VoiceState.Save, SaveScope.Profile),
                            0.5, 2.0, 0.05,
                            value => $"{value * 100:0}%",
                            ModSettingsText.Literal("100% = 图标 64px（能量球是 128px）。"));
                    });

                    // ── 卡牌命中特效（2026-10-08 追加，用户要"和做特效之前对比"）──
                    page.AddSection("hit_vfx", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("卡牌命中特效"));
                        section.WithDescription(ModSettingsText.Literal(
                            "莱伊三个技能（脱身矢 / 广域警觉 / 得见光芒）命中时放的明日方舟原版特效。"));

                        section.AddToggle(
                            HitVfxKey,
                            ModSettingsText.Literal("命中特效"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, HitVfxKey,
                                () => VoiceState.HitVfxEnabled,
                                value => VoiceState.HitVfxEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("开 = 放莱伊原版命中特效；关 = 用游戏自带的钝击特效（= 做特效之前）。改完立即生效。"),
                            (Func<bool>?)null);

                        // ── 烘焙版特效的三个旋钮（2026-10-08 路线 B）──
                        //  帧是 Unity 离线渲出来的序列帧；下面三条**改完立即生效**（下一发命中就是新值）。
                        section.AddSlider(
                            HitVfxScaleKey,
                            ModSettingsText.Literal("特效大小"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HitVfxScaleKey,
                                () => VoiceState.HitVfxScale,
                                value => VoiceState.HitVfxScale = value,
                                VoiceState.Save, SaveScope.Profile),
                            0.5, 3.0, 0.05,
                            value => $"{value * 100:0}%",
                            ModSettingsText.Literal("100% = 按方舟原始比例（爆闪核心约 400px）。觉得小就往右拉。"));

                        section.AddSlider(
                            HitVfxBrightKey,
                            ModSettingsText.Literal("特效亮度"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HitVfxBrightKey,
                                () => VoiceState.HitVfxBright,
                                value => VoiceState.HitVfxBright = value,
                                VoiceState.Save, SaveScope.Profile),
                            0.4, 2.0, 0.05,
                            value => $"{value * 100:0}%",
                            ModSettingsText.Literal("加法混合，越亮越接近过曝。糊成一片白就往左拉。"));

                        section.AddSlider(
                            HitVfxSpeedKey,
                            ModSettingsText.Literal("特效速度"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, HitVfxSpeedKey,
                                () => VoiceState.HitVfxSpeed,
                                value => VoiceState.HitVfxSpeed = value,
                                VoiceState.Save, SaveScope.Profile),
                            0.3, 1.5, 0.05,
                            value => $"{value * 100:0}%",
                            ModSettingsText.Literal("100% = 原速（约 0.5~0.9 秒）。默认 60% 更接近方舟的时长。"));
                    });
                },
                PageId);

            Log.Info("[界面] 设置页已注册（模组设置 → RayMod → 莱伊 · 界面）");
        }
        catch (Exception e)
        {
            Log.Warn($"[界面] 设置页注册失败（游戏照常，只是没地方调 HUD）：{e}");
        }
    }
}
