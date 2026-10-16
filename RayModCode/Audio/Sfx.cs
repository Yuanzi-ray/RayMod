using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Audio;

/// <summary>
/// <b>莱伊的战斗音效</b>（明日方舟原音）。枚举值 == <c>res://RayMod/audio/sfx/&lt;文件名&gt;.wav</c>。
///
/// <para><b>★ 这套文件名不是猜的，是从游戏自己的音效绑定表里查出来的</b>
/// （<c>anon\e3aa6228….bin</c> → LZHAM 解压 → 12517 条 <c>音频文件 → battle.事件.干员</c> 绑定）。
/// 莱伊（<c>char_4117_ray</c>）的武器族是 <b>sndshotgun</b>（sand shotgun）；对应关系：</para>
/// <list type="table">
/// <item><term>Skill1</term><description><c>p_atk_sndshotgun_d</c> ← <c>ON_ABILITY_ON.skchr_ray_1</c>（一技能「脱身矢」）</description></item>
/// <item><term>Skill2</term><description><c>p_imp_sndshotgun_h</c> ← <c>ON_PROJECTILE_HIT.projectile_chr_ray_s2</c>（二技能「广域警觉」）</description></item>
/// <item><term>Skill3</term><description><c>p_imp_sndshotgun_s</c> ← <c>ON_PROJECTILE_HIT.projectile_chr_ray_s3</c>（三技能「得见光芒」）</description></item>
/// <item><term>Reload</term><description><c>p_atk_sndshotgun_nreload</c> ← <c>ON_ABILITY_START.char_4117_ray.attack.*.3</c>（装填）</description></item>
/// </list>
/// <para>⚠️ 三个技能共用的"技能开启"音 <c>b_char_atkboost</c> 在本机没有（<c>sound_beta_2\Battle\b_char\</c>
/// 是按需下载的，这台机器上不存在），所以技能音用的是**各自的弹道音**。</para>
///
/// <para><b>为什么和 <see cref="VoicePlayer"/> 分开</b>：音效是"动作反馈"，不该被语音的
/// <b>5 秒全局冷却</b>吃掉（打一堆牌时音效必须每次都响）；反过来语音的"每场每句只念一次"
/// 也不适用于音效。所以两套各有自己的播放器和节流规则。</para>
/// </summary>
public enum Sfx
{
    None = 0,

    /// <summary>一技能「脱身矢」。</summary>
    Skill1,

    /// <summary>二技能「广域警觉」。</summary>
    Skill2,

    /// <summary>三技能「得见光芒」。</summary>
    Skill3,

    /// <summary>装填（弹药增加）。</summary>
    Reload,

    /// <summary>开火（弹药被消耗）。</summary>
    Shot,

    /// <summary>命中（普通弹）。</summary>
    Hit,

    /// <summary>放下沙地兽。</summary>
    BeastSet,

    /// <summary>收回 / 退场沙地兽。</summary>
    BeastUnset,

    /// <summary>沙地兽取弹（弹药由场上的沙地兽补充时）。</summary>
    BeastTakeBullet,
}

/// <summary>
/// <b>角色音频档案（走 RitsuLib 的"虚拟 FMOD 事件"）</b> —— 2026-10-08。
///
/// <para><b>为什么必须做这一步</b>：基础游戏的角色攻击/施法/死亡音是
/// <c>CharacterModel.AttackSfx</c> 这类属性，值是 <b>FMOD 事件路径</b>
/// （<c>event:/sfx/characters/&lt;角色id&gt;/&lt;角色id&gt;_attack</c>）。
/// 而 RitsuLib 的 <c>CharacterAssetProfiles.Resolve</c> 会拿
/// <c>PlaceholderCharacterId</c>（本工程 = <b>"ironclad"</b>）去**补全**这些路径
/// —— 所以在我们接管之前，<b>打攻击牌响的是铁甲战士的攻击音</b>（用户实机发现的）。
/// 我们又没有 FMOD bank，光靠 <c>AssetProfile.Audio</c> 指不过去。</para>
///
/// <para><b>解法</b>：RitsuLib 提供 <c>VirtualFmodEventRegistry.RegisterOneShot(事件路径, wav 路径)</c>
/// —— 让一个 <b>虚拟 FMOD 事件</b>由我们 pck 里的 wav 支撑。于是把上面三条换成本工程的路径，
/// 游戏自己在"攻击动画那一刻"就会放我们的音（不用 Harmony 改任何触发点）。</para>
///
/// <para>⚠️ <b>谁在什么时候放</b>：<c>CreatureCmd.cs:977-991</c> 在角色动画触发器
/// （Attack/Cast/PowerUp）时播 <c>AttackSfx</c>/<c>CastSfx</c>。所以这是我们**唯一**该在
/// "攻击牌"上发声的地方 —— <see cref="SfxCatalog"/> 里**不要再给 Attack 类卡兜底**，否则会响两声。</para>
/// </summary>
public static class CharacterSfx
{
    private static readonly Logger Log = new("RayMod.Sfx", LogType.Generic);

    // 路径里的 "raymod" 只是我们自己的命名空间，跟铁甲战士的 placeholder 无关（别再撞回 ironclad）。
    private const string Root = "event:/sfx/characters/raymod";

    /// <summary>普攻（每次攻击动画响一次）→ 莱伊的霰弹枪普通射击。</summary>
    public const string AttackEvent = $"{Root}/raymod_attack";

    /// <summary>施法（技能牌/能力牌的 Cast 动画）→ 装填音。</summary>
    public const string CastEvent = $"{Root}/raymod_cast";

    /// <summary>死亡 → 用命中音（原来的 ironclad_die 是男声，很出戏）。</summary>
    public const string DeathEvent = $"{Root}/raymod_die";

    /// <summary>选角界面 → <b>普攻音</b>（用户 2026-10-08 指定：选中莱伊就放她那声枪响）。</summary>
    public const string SelectEvent = $"{Root}/raymod_select";

    private static bool _registered;

    /// <summary>
    /// 注册虚拟事件。由 <c>Entry.Initialize()</c> 调用；音量变了要再调一次
    /// （<c>RegisterOneShot</c> 是"注册或**替换**"，所以重复注册就是刷新音量）。
    /// 任何失败都只记日志 —— 声音绝不能挡住启动。
    /// </summary>
    public static void RegisterVirtualEvents()
    {
        _registered = true;

        try
        {
            float volume = (float)VoiceState.SfxVolume;

            STS2RitsuLib.Audio.VirtualFmodEventRegistry.RegisterOneShot(
                AttackEvent, $"{Entry.ResPath}/audio/sfx/p_atk_sndshotgun_n.wav", volume: volume);

            STS2RitsuLib.Audio.VirtualFmodEventRegistry.RegisterOneShot(
                CastEvent, $"{Entry.ResPath}/audio/sfx/p_atk_sndshotgun_nreload.wav", volume: volume);

            STS2RitsuLib.Audio.VirtualFmodEventRegistry.RegisterOneShot(
                DeathEvent, $"{Entry.ResPath}/audio/sfx/p_imp_sndshotgun_n.wav", volume: volume);

            STS2RitsuLib.Audio.VirtualFmodEventRegistry.RegisterOneShot(
                SelectEvent, $"{Entry.ResPath}/audio/sfx/p_atk_sndshotgun_n.wav", volume: volume);

            Log.Info($"[音效] 已注册 4 个虚拟 FMOD 事件（攻击/施法/死亡/选角），音量={volume:F2}");
        }
        catch (System.Exception e)
        {
            Log.Warn($"[音效] 虚拟事件注册失败（会退回铁甲战士的音）：{e.Message}");
        }
    }

    /// <summary>这条 FMOD 路径是不是"我们的角色音"（给抑制补丁用）。</summary>
    public static bool IsOurEvent(string? path) =>
        !string.IsNullOrEmpty(path) && path!.StartsWith(Root, System.StringComparison.Ordinal);
}

/// <summary>
/// <b>卡牌 → 音效</b>映射：只有莱伊的三个技能对应卡（用户 2026-10-07 定的口径）。
///
/// <para>用<b>类名</b>查（本地化键/中文名会随文案改动而变，类名不会）—— 与 <c>VoiceCatalog</c> 同规矩。</para>
///
/// <para>⚠️ **弹药类音效不在这里**：那样会和 <c>AmmoCmd.AddAmmo</c> 里的播放撞成两声。
/// 弹药的音只有 <c>AddAmmo</c> 一个出口（装填/取弹/开火）。</para>
/// </summary>
public static class SfxCatalog
{
    /// <summary>一技能「脱身矢」/ 二技能「广域警觉」/ 三技能「得见光芒」。</summary>
    private static readonly Dictionary<string, Sfx> CardMap = new()
    {
        ["EscapeArrow"] = Sfx.Skill1,
        ["WideareaVigilance"] = Sfx.Skill2,
        ["SeeTheLight"] = Sfx.Skill3,
    };

    /// <summary>没有配音效的卡返回 <see cref="Sfx.None"/>。</summary>
    public static Sfx ForCardClass(string className) =>
        CardMap.TryGetValue(className, out Sfx sfx) ? sfx : Sfx.None;

    /// <summary>配了音效的卡数（诊断用；应为 3）。</summary>
    public static int MappedCardCount => CardMap.Count;

    /// <summary>
    /// 当前正在结算的这张牌是不是"自带技能音"的那三张。
    ///
    /// <para>给抑制补丁用：这三张卡都是 <c>CardType.Attack</c>，所以它们**既**会被游戏按
    /// <c>AttackSfx</c> 放普攻音、**又**由我们在出牌瞬间放技能音 —— 会撞成两声。
    /// 于是"当前卡自带技能音"时，把那条**普攻**事件压掉（只压我们自己的，不动别人的）。</para>
    ///
    /// <para>★★ 2026-10-15 <b>撞名门</b>：<c>CurrentCard</c> 是"此刻正在结算的卡"，
    /// 而查表按**简单类名** —— 必须确认它是**我们的卡**，否则原版同名卡（别的角色的
    /// <c>Feed</c> / <c>Darkness</c>）会被当成我们的技能卡、把那一击的普攻音压掉。</para>
    /// </summary>
    public static bool CurrentCardHasOwnSfx =>
        Art.SpineAnimDispatch.CurrentCard is { } card
        && Cards.CardIdentity.IsOurs(card)
        && CardMap.ContainsKey(card.GetType().Name);
}

/// <summary>
/// 音效播放器：加载 / 节流 / 播放。<b>所有失败都静默</b> —— 音效绝不能影响战斗结算。
/// </summary>
public static class SfxPlayer
{
    private static readonly Logger Log = new("RayMod.Sfx", LogType.Generic);

    /// <summary>枚举 → 音频文件名（不含扩展名）。文件名保持明日方舟原样，方便回溯。</summary>
    private static readonly Dictionary<Sfx, string> Files = new()
    {
        [Sfx.Skill1] = "p_atk_sndshotgun_d",
        [Sfx.Skill2] = "p_imp_sndshotgun_h",
        [Sfx.Skill3] = "p_imp_sndshotgun_s",
        [Sfx.Reload] = "p_atk_sndshotgun_nreload",
        [Sfx.Shot] = "p_atk_sndshotgun_n",
        [Sfx.Hit] = "p_imp_sndshotgun_n",
        [Sfx.BeastSet] = "p_atk_sndbstset",
        [Sfx.BeastUnset] = "p_atk_sndbstunset",
        [Sfx.BeastTakeBullet] = "p_atk_sndbsttakebullet",
    };

    /// <summary>
    /// 同一个音效的最小间隔（毫秒）。防止"一次动作触发多条路径"把同一个音叠成机关枪
    /// （例如一张牌既召唤沙地兽又加弹药）。<b>不同音效之间不互相节流</b> ——
    /// 音效是动作反馈，密集是正常的，这和语音的 5 秒冷却完全是两回事。
    /// </summary>
    private const ulong SameSfxIntervalMsec = 90;

    private static readonly Dictionary<string, AudioStream> Cache = new();
    private static readonly Dictionary<Sfx, ulong> LastPlayed = new();

    private static AudioStreamPlayer? _player;

    /// <summary>
    /// ★★★ <b>带归属的音效播放 —— 所有"属于某个玩家"的音效都必须用这个重载</b>（2026-10-15，§S47）。
    ///
    /// <para>为什么需要它：光判"是不是我的"不够 —— 单机下 <c>LocalContext.IsMe</c> <b>恒为真</b>，
    /// 所以"装了本 mod 但选了别的角色"时，<c>AddAmmo</c>（装填音）/ <c>ConsumeAmmo</c>（开火音）/
    /// <c>BeastCmd.Summon</c>（放沙地兽）/ <c>Retire</c>（收回）这些音效照样会响。</para>
    ///
    /// <para>判据统一在 <see cref="Patches.VoiceGate.MuteFor"/>：<b>本机 + 本地玩家是莱伊</b>才放音。</para>
    /// </summary>
    /// <param name="sfx">要播的音效。</param>
    /// <param name="owner">这次动作的归属玩家。</param>
    public static void Play(Sfx sfx, MegaCrit.Sts2.Core.Entities.Players.Player? owner)
    {
        // 门控本身也要兜异常：它抛了就当"该响"，绝不能因为判据把音效整个吞掉。
        try
        {
            if (Patches.VoiceGate.MuteFor(owner))
            {
                return;
            }
        }
        catch
        {
            // fail-open，见上。
        }

        Play(sfx);
    }

    /// <summary>
    /// 播一个音效。开关关掉、枚举非法、资源缺失、播放器拿不到 —— 一律静默返回。
    ///
    /// <para>⚠️ 这个无归属的重载<b>只在"确实没有哪个玩家该为此负责"时</b>才用；
    /// 只要拿得到玩家，就用上面那个带 <c>owner</c> 的重载（否则玩别的角色也会响，§S47）。</para>
    /// </summary>
    public static void Play(Sfx sfx)
    {
        try
        {
            if (sfx == Sfx.None || !VoiceState.SfxEnabled || !Files.TryGetValue(sfx, out string? file))
            {
                return;
            }

            // 音量为 0 = 静音，连资源都不用取。
            float volume = (float)VoiceState.SfxVolume;

            if (volume <= 0.001f)
            {
                return;
            }

            ulong now = Time.GetTicksMsec();

            if (LastPlayed.TryGetValue(sfx, out ulong last) && now - last < SameSfxIntervalMsec)
            {
                return;
            }

            string path = $"{Entry.ResPath}/audio/sfx/{file}.wav";

            AudioStream? stream = Load(path);
            AudioStreamPlayer? player = EnsurePlayer();

            if (stream == null || player == null)
            {
                return;
            }

            player.Stream = stream;
            player.VolumeDb = Mathf.LinearToDb(volume);
            player.Play();

            LastPlayed[sfx] = now;
        }
        catch (System.Exception e)
        {
            Log.Warn($"[音效] 播放失败（{sfx}）：{e.Message}");
        }
    }

    /// <summary>缓存键 = 完整路径（和 <see cref="VoicePlayer"/> 同一个教训：键要含所有会变的维度）。</summary>
    private static AudioStream? Load(string path)
    {
        if (Cache.TryGetValue(path, out AudioStream cached) && GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }

        var stream = ResourceLoader.Load<AudioStream>(path);

        if (stream == null)
        {
            // 每次失败都记（漏记会让第二个坏文件静默，排查时白费时间）。
            Log.Warn($"[音效] 加载不到：{path}（音频没打进 pck？检查 res://RayMod/** 导出过滤）");
            return null;
        }

        Cache[path] = stream;
        return stream;
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
            Name = "RayModSfxPlayer",
            Bus = ResolveSfxBus(),
        };

        tree.Root.AddChild(player);
        _player = player;

        Log.Info($"[音效] 播放器已挂到场景树根（总线={player.Bus}）");
        return _player;
    }

    /// <summary>挑音效总线：优先 SFX，取不到退回 Master（总线不存在时 Godot 会报错）。</summary>
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
