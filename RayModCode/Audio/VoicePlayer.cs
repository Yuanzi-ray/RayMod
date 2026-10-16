using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Audio;

/// <summary>
/// <b>语音播放器</b>：负责加载音频、按设置决定播不播、以及<b>频率控制</b>。
///
/// <para><b>为什么不用游戏的 <c>SfxCmd</c> / <c>NDebugAudioManager</c></b>：
/// 那两条路分别只吃 FMOD 事件路径（<c>event:/...</c>）和 <c>res://debug_audio/</c> 下的调试音频，
/// 而我们的音频是<b>自己打进 pck</b> 的（<c>res://RayMod/audio/voice/...</c>）。
/// 自己建一个 <see cref="AudioStreamPlayer"/> 挂在场景树根上最干净 ——
/// 不依赖游戏内部机制，也不怕它哪天改实现。</para>
///
/// <para><b>频率控制</b>（用户 2026-10-07 要求"确实需要控制"）：
/// ① <b>全局冷却</b> <see cref="GlobalCooldownSeconds"/> 秒 —— 任何两句之间至少隔这么久；
/// ② <b>同一句每场战斗只播一次</b> —— 一张牌打三次不会念三遍。
/// 胜利/失败这类本身每场只触发一次，不受②影响，但仍受①约束（避免和最后一张牌的台词撞在一起）。</para>
///
/// <para>⚠️ <b>①的量法是"从上一句【开始】算"</b>（<c>距上句 ≥ 5s</c> 才允许下一句）——
/// 所以<b>真正的静音 = 5 秒 − 上一句长度</b>：短句（017 只有 3.1 秒）之间只静 1.9 秒，
/// 长句则会被下一句<b>掐断</b>。2026-10-07 用户报"语音之间并不会至少间隔 5 秒"，
/// 查清就是这条量法造成的（不是冷却没生效 —— 那次会话日志里有 148 次"全局冷却中"跳过）。
/// 当时给了三个改法：改成"上一句念完再等 5 秒"（代价：长句占场，日语 009 长达 37.5 秒）/
/// 顺带把长句截到 4 秒 + 淡出 / 保持现状 —— <b>用户拍板：保持现状</b>（不想丢语音、也不想截台词）。
/// <b>别再擅自改这个语义</b>；真要改，先解决长句占场的问题。</para>
/// </summary>
public static class VoicePlayer
{
    /// <summary>两句之间的最小间隔（秒）。</summary>
    private const float GlobalCooldownSeconds = 5f;

    private static readonly Logger Log = new("RayMod.Voice", LogType.Generic);

    /// <summary>
    /// 音频缓存，<b>键是完整资源路径（含语种），不是句子</b>。
    /// ⚠️ 2026-10-07 修的真实 bug：原来键是 <see cref="VoiceLine"/>，而路径里才带语种
    /// → <b>第一次播放时用的那个语种被永久缓存</b>，之后改语种仍然放旧文件
    /// （日志还会照 <c>VoiceState.Lang</c> 打出假语种 —— 现场就是这么被误导的）。
    /// 改成按路径做键后，中/日两套可以同时缓存，互不覆盖。
    /// </summary>
    private static readonly Dictionary<string, AudioStream> StreamCache = new();

    private static readonly HashSet<VoiceLine> PlayedThisCombat = new();

    private static AudioStreamPlayer? _player;
    private static ulong _lastPlayMsec;

    /// <summary>本场战斗已经播过的句子数（诊断用）。</summary>
    public static int PlayedThisCombatCount => PlayedThisCombat.Count;

    /// <summary>
    /// 播一句。所有判断都在这儿：
    /// 大类开关 → 频率（全局冷却 + 每场一次）→ 取资源 → 播。
    /// <b>任何一步失败都静默返回</b>，绝不抛异常（语音不能影响游戏）。
    /// </summary>
    /// <param name="line">要播的句子。</param>
    /// <param name="category">这句话属于哪个大类（决定被哪个开关管）。</param>
    /// <param name="oncePerCombat">是否"同一句每场只播一次"。默认是。</param>
    public static void Play(VoiceLine line, VoiceCategory category, bool oncePerCombat = true)
    {
        try
        {
            if (line == VoiceLine.None || !VoiceState.IsEnabled(category))
            {
                return;
            }

            if (oncePerCombat && PlayedThisCombat.Contains(line))
            {
                Log.Info($"[语音] – 跳过 {Describe(line)}：本场已播过（每场每句只播一次）");
                return;
            }

            ulong now = Time.GetTicksMsec();
            ulong sinceLast = _lastPlayMsec == 0 ? 0 : now - _lastPlayMsec;
            if (_lastPlayMsec != 0 && sinceLast < (ulong)(GlobalCooldownSeconds * 1000f))
            {
                Log.Info($"[语音] – 跳过 {Describe(line)}：全局冷却中（距上句 {sinceLast / 1000f:F1}s）");
                return;
            }

            // 路径里带语种，所以**先取路径**：既喂给 Load（缓存键），也写进日志（日志才算数）。
            string path = PathOf(line);
            AudioStream? stream = Load(path);
            AudioStreamPlayer? player = EnsurePlayer();
            if (stream == null || player == null)
            {
                Log.Info($"[语音] – 跳过 {Describe(line)}：资源或播放器取不到");
                return;
            }

            player.Stream = stream;
            player.Play();

            _lastPlayMsec = now;
            PlayedThisCombat.Add(line);

            // ★ 每次播放都留痕：这句是"哪个编号、什么语种、<b>实际加载的哪个文件</b>"。
            // ⚠️ 早先只打 VoiceState.Lang（设置值），语种错乱时日志会跟着一起骗人 —— 所以**必须打文件路径**。
            // 距上句也一起打：这是唯一能事后证明"间隔够不够"的数据（游戏日志本身没有时间戳）。
            string gap = sinceLast == 0 ? "首句" : $"{sinceLast / 1000f:F1}s";
            Log.Info($"[语音] ▶ 播放 {Describe(line)} 大类={category} 语种={VoiceState.Lang} 文件={path} 句长={stream.GetLength():F1}s 距上句={gap}");
        }
        catch (System.Exception e)
        {
            Log.Warn($"[语音] 播放失败（{line}）：{e.Message}");
        }
    }

    /// <summary>日志用的一句话描述："017 编入队伍"。</summary>
    private static string Describe(VoiceLine line) => $"{(int)line:D3} {line}";

    /// <summary>新战斗开始时清空"本场已播"记录（由战斗开始钩子调用）。</summary>
    public static void ResetCombat()
    {
        PlayedThisCombat.Clear();
    }

    /// <summary>当前语种对应的资源路径。</summary>
    private static string PathOf(VoiceLine line)
    {
        string lang = VoiceState.Lang == VoiceLang.Cn ? "cn" : "jp";
        // 文件名固定 cn_0NN.wav，只有目录分语种（这是 PRTS 的存放方式）
        return $"{Entry.ResPath}/audio/voice/{lang}/cn_{(int)line:D3}.wav";
    }

    /// <summary>缓存键 = 完整路径（含语种）。中/日两套各自缓存，改语种立刻换到另一套。</summary>
    private static AudioStream? Load(string path)
    {
        if (StreamCache.TryGetValue(path, out AudioStream cached) && GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }

        var stream = ResourceLoader.Load<AudioStream>(path);
        if (stream == null)
        {
            // ⚠️ 每次失败都记（早先写成"只记第一条"，结果第二个坏文件就静默了 —— 排查时白费时间）。
            Log.Warn($"[语音] 加载不到音频：{path}（音频没打进 pck？检查 res://RayMod/** 导出过滤）");
            return null;
        }

        StreamCache[path] = stream;
        return stream;
    }

    /// <summary>
    /// 语种被改时调用（设置页回调）。
    /// ①「本场已播过」的记录清空 —— 否则刚改完语种，这一场里已经念过的句子<b>一句都不会再响</b>，
    ///    用户会以为"改语种没生效"（这就是 2026-10-07 报的那个 bug 的另一半）。
    /// ②缓存<b>不用清</b>：它按路径做键，另一语种本来就没被缓存过。
    /// </summary>
    public static void OnLanguageChanged()
    {
        PlayedThisCombat.Clear();
        Log.Info($"[语音] 语种已切换 → {VoiceState.Lang}（本场已播记录已清空，下一句按新语种播）");
    }

    private static AudioStreamPlayer? EnsurePlayer()
    {
        if (_player != null && GodotObject.IsInstanceValid(_player) && _player.IsInsideTree())
        {
            return _player;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return null;
        }

        var player = new AudioStreamPlayer
        {
            Name = "RayModVoicePlayer",
            Bus = ResolveSfxBus(),
        };

        tree.Root.AddChild(player);
        _player = player;

        Log.Info($"[语音] 播放器已挂到场景树根（总线={player.Bus}）");
        return _player;
    }

    /// <summary>挑音效总线：优先 SFX，取不到就退回 Master（总线不存在时 Godot 会报错）。</summary>
    private static string ResolveSfxBus()
    {
        foreach (string candidate in new[] { "SFX", "Sfx", "sfx", "Sound", "Master" })
        {
            if (AudioServer.GetBusIndex(candidate) >= 0)
            {
                return candidate;
            }
        }

        return "Master";
    }
}
