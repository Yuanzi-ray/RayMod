using Godot;
using MegaCrit.Sts2.Core.Logging;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Audio;

/// <summary>
/// <b>语音设置的值 + 持久化</b>。和 <see cref="RayMod.Art.RaySkinState"/> 同一套路：
/// 用<b>自己的</b> <c>ConfigFile</c>（<c>user://raymod_voice.cfg</c>），RitsuLib 只负责界面。
///
/// <para>为什么不把值直接挂在 RitsuLib 的设置框架上：那套框架管"存哪儿、怎么存"，
/// 而我们需要<b>读盘时机可控</b>（必须在第一场战斗前，见 <c>Entry.Initialize</c>），
/// 并且<b>设置页注册失败也不能影响播放</b>（照旧走文件里的值）。</para>
/// </summary>
public static class VoiceState
{
    private const string ConfigPath = "user://raymod_voice.cfg";
    private const string Section = "voice";

    private static readonly Logger Log = new("RayMod.Voice", LogType.Generic);

    private static bool _loaded;

    /// <summary>语种。默认日文（原音，用户 2026-10-07 的方案）。</summary>
    public static VoiceLang Lang { get; set; } = VoiceLang.Jp;

    /// <summary>卡牌台词开关。</summary>
    public static bool CardEnabled { get; set; } = true;

    /// <summary>战斗流程语音开关。</summary>
    public static bool CombatEnabled { get; set; } = true;

    /// <summary>系统事件语音开关。</summary>
    public static bool SystemEnabled { get; set; } = true;

    /// <summary>
    /// 战斗音效开关（技能 / 沙地兽 / 装填 —— 见 <see cref="SfxPlayer"/>）。
    /// 和语音分开：语音是"台词"，音效是"动作反馈"，有人只要前者不要后者。
    /// </summary>
    public static bool SfxEnabled { get; set; } = true;

    /// <summary>
    /// 音效音量（0~1，设置页滑条）。作用于我们自己放的所有音效**和**角色的
    /// 攻击/施法/死亡/选角四条音（后四条走虚拟 FMOD 事件，注册时把音量传进去）。
    /// </summary>
    public static double SfxVolume { get; set; } = 0.7;

    // ── 战斗 HUD 计数器布局（2026-10-08）：存在同一个 cfg 里，HudLayout 只是它的代理 ──

    /// <summary>HUD 计数器左右偏移（像素，正数往右）。默认值 = 用户 2026-10-08 实机调出来的位置。</summary>
    public static double HudOffsetX { get; set; } = -48.0;

    /// <summary>HUD 计数器上下偏移（像素，正数往下）。默认值 = 用户 2026-10-08 实机调出来的位置。</summary>
    public static double HudOffsetY { get; set; } = -204.0;

    /// <summary>HUD 计数器缩放（1.0 = 图标 64px）。</summary>
    public static double HudScale { get; set; } = 1.0;

    /// <summary>
    /// <b>卡牌命中特效开关</b>（2026-10-08，模组设置 → 莱伊 · 界面）。
    /// 关掉时三个技能卡走游戏**原本的命中特效**（<c>vfx/vfx_attack_blunt</c>）——
    /// 也就是"做特效之前"的真实状态，方便当场 A/B 对比。
    /// 判定点在 <c>Patches\SkillHitVfxPatch</c>（每次命中读一次，改完立即生效，不用重启）。
    /// </summary>
    public static bool HitVfxEnabled { get; set; } = true;

    // ── 烘焙版命中特效的三个旋钮（2026-10-08 路线 B，模组设置 → 莱伊 · 界面）──
    //
    //  为什么做成滑条：烘焙帧的"多大 / 多亮 / 多久"三件事**只有玩家的眼睛能拍板**
    //  （方舟那边靠 HDR + 泛光，本地渲出来的线性值必然偏暗偏小）。
    //  与其反复重烘重部署，不如把三个旋钮交给实机当场调。

    /// <summary>命中特效大小（1.0 = 按烘焙的 80 像素/世界单位原样播）。</summary>
    public static double HitVfxScale { get; set; } = 1.0;

    /// <summary>命中特效亮度（1.0 = 烘焙出来的原样，加法混合下越亮越"过曝"）。</summary>
    public static double HitVfxBright { get; set; } = 1.0;

    /// <summary>
    /// 命中特效播放速度（1.0 = 30fps 原速）。<b>默认 0.6</b> ——
    /// 方舟原版靠动画曲线把这一发撑到 ~1 秒，而"数据直渲"只有 0.5~0.9 秒，
    /// 按 0.6 倍速播正好补回那个时长（用户 2026-10-08 反馈"一闪就没"）。
    /// </summary>
    public static double HitVfxSpeed { get; set; } = 0.6;

    // ── ★★ 特效开关（2026-10-13 用户："在设置里面加一个特效开关的按钮"）────────────
    //    ★ 用户随后纠正了我的第一版设计："太细了。到时候卡牌一多难道每个都有写开关吗"
    //    ⇒ **只按"类别"开关，绝不按"卡"开关**：新卡自动归入下面的类别，永远不用再加开关。
    //      ① 总开关 = 这个 mod 的所有自绘战斗演出
    //      ② 出牌特效 = **所有卡牌触发的场上演出**（出牌层/命中层/弹幕/巨兽巨眼/无限 HUD …）
    //      （"命中特效"本来就在「莱伊 · 界面」页有它自己的开关 + 三条滑条，不动它。）
    /// <summary>特效总开关。关 = 这个 mod 所有自绘战斗演出都不放（游戏照常）。</summary>
    public static bool VfxEnabled { get; set; } = true;

    /// <summary>出牌特效（**类别**开关：所有卡牌触发的场上演出）。</summary>
    public static bool FieldVfxEnabled { get; set; } = true;

    /// <summary>总开关：三个大类全关时等于静音（设置页仍可逐项打开）。</summary>
    public static bool IsEnabled(VoiceCategory category) => category switch
    {
        VoiceCategory.Card => CardEnabled,
        VoiceCategory.Combat => CombatEnabled,
        VoiceCategory.System => SystemEnabled,
        _ => false,
    };

    /// <summary>
    /// 读盘。可重复调用，只在第一次真正读。
    /// <b>失败一律回退默认值</b>：语音只是锦上添花，绝不能因为读配置失败影响进游戏。
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        // 映射条数也留痕：排查"某张卡没声音/播错"时先看这个数对不对（应为 31）。
        Log.Info($"[语音] 卡牌映射 {VoiceCatalog.MappedCardCount} 张");

        try
        {
            var config = new ConfigFile();
            if (config.Load(ConfigPath) != Error.Ok)
            {
                Log.Info("[语音] 没有配置文件，用默认值（日文 / 三类全开）");
                return;
            }

            Lang = ParseLang(config.GetValue(Section, "lang", "jp").AsString());
            CardEnabled = config.GetValue(Section, "card", true).AsBool();
            CombatEnabled = config.GetValue(Section, "combat", true).AsBool();
            SystemEnabled = config.GetValue(Section, "system", true).AsBool();
            SfxEnabled = config.GetValue(Section, "sfx", true).AsBool();
            SfxVolume = Math.Clamp(config.GetValue(Section, "sfx_volume", 0.7).AsDouble(), 0.0, 1.0);
            HudOffsetX = Math.Clamp(config.GetValue(Section, "hud_offset_x", 0.0).AsDouble(), -400.0, 400.0);
            HudOffsetY = Math.Clamp(config.GetValue(Section, "hud_offset_y", 0.0).AsDouble(), -400.0, 400.0);
            HudScale = Math.Clamp(config.GetValue(Section, "hud_scale", 1.0).AsDouble(), 0.5, 2.0);
            HitVfxEnabled = config.GetValue(Section, "hit_vfx", true).AsBool();
            HitVfxScale = Math.Clamp(config.GetValue(Section, "hit_vfx_scale", 1.0).AsDouble(), 0.5, 3.0);
            HitVfxBright = Math.Clamp(config.GetValue(Section, "hit_vfx_bright", 1.0).AsDouble(), 0.4, 2.0);
            HitVfxSpeed = Math.Clamp(config.GetValue(Section, "hit_vfx_speed", 0.6).AsDouble(), 0.3, 1.5);
            VfxEnabled = config.GetValue(Section, "vfx", true).AsBool();
            FieldVfxEnabled = config.GetValue(Section, "vfx_field", true).AsBool();

            Log.Info($"[语音] 已读盘：语种={Lang} 卡牌={CardEnabled} 战斗={CombatEnabled} " +
                     $"系统={SystemEnabled} 音效={SfxEnabled} 音量={SfxVolume:F2} " +
                     $"HUD偏移=({HudOffsetX:0},{HudOffsetY:0}) 缩放={HudScale:F2} 命中特效={HitVfxEnabled} " +
                     $"特效大小={HitVfxScale:F2} 亮度={HitVfxBright:F2} 速度={HitVfxSpeed:F2} " +
                     $"特效总开关={VfxEnabled} 出牌特效={FieldVfxEnabled}");
        }
        catch (System.Exception e)
        {
            Log.Warn($"[语音] 读盘失败，用默认值：{e.Message}");
        }
    }

    /// <summary>落盘。设置页每次改动都会调它（和皮肤设置一样，改一次存一次）。</summary>
    public static void Save()
    {
        try
        {
            var config = new ConfigFile();
            config.SetValue(Section, "lang", Lang == VoiceLang.Cn ? "cn" : "jp");
            config.SetValue(Section, "card", CardEnabled);
            config.SetValue(Section, "combat", CombatEnabled);
            config.SetValue(Section, "system", SystemEnabled);
            config.SetValue(Section, "sfx", SfxEnabled);
            config.SetValue(Section, "sfx_volume", SfxVolume);
            config.SetValue(Section, "hud_offset_x", HudOffsetX);
            config.SetValue(Section, "hud_offset_y", HudOffsetY);
            config.SetValue(Section, "hud_scale", HudScale);
            config.SetValue(Section, "hit_vfx", HitVfxEnabled);
            config.SetValue(Section, "hit_vfx_scale", HitVfxScale);
            config.SetValue(Section, "hit_vfx_bright", HitVfxBright);
            config.SetValue(Section, "hit_vfx_speed", HitVfxSpeed);
            config.SetValue(Section, "vfx", VfxEnabled);
            config.SetValue(Section, "vfx_field", FieldVfxEnabled);

            Error err = config.Save(ConfigPath);
            if (err != Error.Ok)
            {
                Log.Warn($"[语音] 落盘失败：{err}");
            }
        }
        catch (System.Exception e)
        {
            Log.Warn($"[语音] 落盘异常：{e.Message}");
        }
    }

    private static VoiceLang ParseLang(string raw) =>
        raw.Trim().ToLowerInvariant() == "cn" ? VoiceLang.Cn : VoiceLang.Jp;
}
