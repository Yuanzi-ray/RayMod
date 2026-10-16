using System;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Audio;
using STS2RitsuLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Settings;

/// <summary>
/// <b>Mod 设置页：语音</b>（模组设置 → RayMod → 莱伊 · 语音）。
/// 和 <see cref="RayModSkinSettings"/> 同一套路：RitsuLib 出界面，值存在我们自己的
/// <c>user://raymod_voice.cfg</c>（见 <see cref="VoiceState"/>）。
///
/// <para>三块内容：<b>语种</b>（日/中）+ <b>按大类开关</b>（卡牌 / 战斗流程 / 系统事件）。
/// 改动立即生效（下一句就按新设置走），不需要重启。</para>
///
/// <para>⚠️ 整个注册包在 try/catch 里：设置页只是锦上添花，注册失败也<b>绝不能让 mod 启动失败</b>
/// （语音照旧按文件里的值播）。</para>
/// </summary>
public static class RayModVoiceSettings
{
    private const string PageId = "raymod_voice";

    // ⚠️ 键名就是存档里的字段名，改了老玩家的选择会丢，别乱动。
    private const string LangKey = "voice_lang";
    private const string CardKey = "voice_card";
    private const string CombatKey = "voice_combat";
    private const string SystemKey = "voice_system";
    private const string SfxKey = "voice_sfx";
    private const string SfxVolumeKey = "sfx_volume";

    private static readonly Logger Log = new("RayMod.Voice", LogType.Generic);

    public static void Register()
    {
        try
        {
            RitsuLibFramework.RegisterModSettings(
                Entry.ModId,
                page =>
                {
                    page.WithTitle(ModSettingsText.Literal("莱伊 · 语音与音效"));
                    page.WithDescription(ModSettingsText.Literal(
                        "莱伊的台词（明日方舟原音）与战斗音效。改完立即生效。"
                        + "\n（HUD 计数器的位置/大小在「莱伊 · 界面」那一页。）"));

                    // ── 语种 ──
                    page.AddSection("voice_lang", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("语种"));
                        section.AddEnumChoice<VoiceLang>(
                            LangKey,
                            ModSettingsText.Literal("语音语种"),
                            ModSettingsBindings.Callback<VoiceLang>(
                                Entry.ModId,
                                LangKey,
                                () => VoiceState.Lang,
                                value =>
                                {
                                    VoiceState.Lang = value;
                                    // ★ 必须通知播放器：清掉「本场已播过」记录。
                                    //   否则改完语种，这场里已经念过的句子一句都不会再响，
                                    //   看起来就像"改语种没生效"（2026-10-07 报的 bug）。
                                    VoicePlayer.OnLanguageChanged();
                                },
                                VoiceState.Save,
                                SaveScope.Profile),
                            lang => ModSettingsText.Literal(lang == VoiceLang.Cn ? "中文" : "日文"),
                            ModSettingsText.Literal("下一句生效，不用重启。"),
                            ModSettingsChoicePresentation.Dropdown);
                    });

                    // ── 大类开关 ──
                    page.AddSection("voice_category", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("按大类开关"));
                        section.WithDescription(ModSettingsText.Literal(
                            "关掉的类别说台词。抽牌/胜利这类战斗播报很密，嫌吵可以先关「战斗流程」。"));

                        section.AddToggle(
                            CardKey,
                            ModSettingsText.Literal("卡牌台词"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, CardKey,
                                () => VoiceState.CardEnabled,
                                value => VoiceState.CardEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("打出有配词的卡时念台词（同一句每场战斗只念一次）。"),
                            (Func<bool>?)null);

                        section.AddToggle(
                            CombatKey,
                            ModSettingsText.Literal("战斗流程"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, CombatKey,
                                () => VoiceState.CombatEnabled,
                                value => VoiceState.CombatEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("战斗开始 / 回合开始 / 胜利 / 失败。"),
                            (Func<bool>?)null);

                        section.AddToggle(
                            SystemKey,
                            ModSettingsText.Literal("系统事件"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, SystemKey,
                                () => VoiceState.SystemEnabled,
                                value => VoiceState.SystemEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal("选角界面 / 召唤沙地兽 / 遗物 / 卡牌升级 / 拿药水。"),
                            (Func<bool>?)null);
                    });

                    // ── 战斗音效（2026-10-07 追加）──
                    page.AddSection("voice_sfx", section =>
                    {
                        section.WithTitle(ModSettingsText.Literal("战斗音效"));
                        section.WithDescription(ModSettingsText.Literal(
                            "明日方舟原音：技能释放 / 沙地兽放置收回 / 弹药装填。与台词互不影响。"));

                        section.AddToggle(
                            SfxKey,
                            ModSettingsText.Literal("技能与沙地兽音效"),
                            ModSettingsBindings.Callback<bool>(
                                Entry.ModId, SfxKey,
                                () => VoiceState.SfxEnabled,
                                value => VoiceState.SfxEnabled = value,
                                VoiceState.Save, SaveScope.Profile),
                            ModSettingsText.Literal(
                                "「脱身矢」「广域警觉」「得见光芒」的技能音、沙地兽放置/收回/取弹、弹药装填。"),
                            (Func<bool>?)null);

                        // 音量滑条：同时作用于我们自己放的和角色那四条（攻击/施法/死亡/选角）。
                        section.AddSlider(
                            SfxVolumeKey,
                            ModSettingsText.Literal("音效音量"),
                            ModSettingsBindings.Callback<double>(
                                Entry.ModId, SfxVolumeKey,
                                () => VoiceState.SfxVolume,
                                value =>
                                {
                                    VoiceState.SfxVolume = value;

                                    // 角色的四条音是"注册时定音量"的，所以改完要重注册一遍
                                    // （RegisterOneShot 语义是"注册或替换"）。
                                    Audio.CharacterSfx.RegisterVirtualEvents();
                                },
                                VoiceState.Save, SaveScope.Profile),
                            0.0, 1.0, 0.05,
                            value => $"{value * 100:0}%",
                            ModSettingsText.Literal("0% = 静音。改完下一声就生效。"));
                    });
                },
                PageId);

            Log.Info("[语音] 设置页已注册（模组设置 → RayMod → 莱伊 · 语音与音效）");
        }
        catch (Exception e)
        {
            Log.Warn($"[语音] 设置页注册失败（游戏照常，只是没地方调语音）：{e}");
        }
    }
}
