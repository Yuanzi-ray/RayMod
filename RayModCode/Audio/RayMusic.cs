using System;
using Godot;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Audio;

/// <summary>
/// ★★ <b>主题曲</b>（2026-10-19 用户指定）：游玩莱伊时**循环播放**，设置页可开关。
///
/// <para><b>素材</b>：`res://RayMod/audio/music/raymod_theme.mp3`（用户给的
/// `m_sys_act16mini_combine.mp3`，4.27 MB）。<b>没有转格式</b> —— Godot 4 原生支持
/// <see cref="AudioStreamMP3"/>，转 wav 会把 pck 顶大 40 MB 以上（现在已 83 MB）。</para>
///
/// <para><b>为什么用一个常驻轮询节点</b>：这版没有现成的"战斗开始/角色确认"钩子可挂
/// （那些钩子都在别的补丁里，且和本需求无关），而"要不要放音乐"是个**状态**不是**事件** ——
/// 每 0.5s 看一眼 <see cref="VoiceState.MusicEnabled"/> 就能同时覆盖
/// 开关切换、进入/离开、以及中途被别的东西停了这几种情况，而且**不会漏事件**。</para>
///
/// <para><b>拿到树的方式</b>：`((SceneTree)Engine.GetMainLoop()).Root` ——
/// 不依赖游戏自己的 NGame 反射，所以游戏版本变动也不容易连带崩。</para>
/// </summary>
internal static partial class RayMusic
{
    private const string TrackPath = Entry.ResPath + "/audio/music/raymod_theme.mp3";

    /// <summary>音乐音量（dB）。-14 ≈ 原 BGM 的六成，**刻意压在游戏音乐下面**，不抢。</summary>
    private const float VolumeDb = -14f;

    private static bool _attached;

    /// <summary>★ 必须用工程自己的 Logger —— `GD.Print` 只在 stdout，**不进 godot.log**，
    /// 上一版就是因此"连诊断行都没有"，白猜一轮（用户："你不看日志吗"）。</summary>
    private static readonly Logger Log = new("RayMod.Music", LogType.Generic);

    /// <summary>挂上轮询节点（幂等）。在 <see cref="Entry.Initialize"/> 里调一次。</summary>
    internal static void Attach()
    {
        if (_attached)
        {
            return;
        }

        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            {
                return;
            }

            tree.Root.AddChild(new MusicDriver { Name = "RayModThemeMusic" });
            _attached = true;
        }
        catch (Exception e)
        {
            Log.Info($"[巨兽·音乐] 挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>循环播放器本体。载入失败不刷屏（只报一次），也绝不抛到主循环。</summary>
    private sealed partial class MusicDriver : Node
    {
        private AudioStreamPlayer? _player;
        private bool _reported;
        private float _poll;                 // 角色判据的轮询计时（0.5s 一次）
        private bool? _isRai;                // true=莱伊局 / false=确定不是 / null=在局内但角色读不到
        private float _duck;                 // 压制原音乐的节流计时（1 秒一次）
        private static bool _loggedId;       // 角色 id 只打印一次（诊断用）
        private static bool _loggedDiag;     // 两条反射路径的诊断只打印一次

        /// <summary>把一个对象的**真实属性名**打出来（只打一次）——
        /// 反射猜名字是这一路最贵的坑：与其一轮一轮试，不如让日志把 API 念给我听。</summary>
        private static void LogNames(object? target, string what)
        {
            if (target == null || _loggedNames)
            {
                return;
            }

            _loggedNames = true;

            try
            {
                var sb = new System.Text.StringBuilder();

                foreach (System.Reflection.PropertyInfo p in target.GetType()
                             .GetProperties(System.Reflection.BindingFlags.Public
                                            | System.Reflection.BindingFlags.Instance
                                            | System.Reflection.BindingFlags.Static))
                {
                    sb.Append(p.Name).Append(' ');
                }

                Log.Info($"[巨兽·音乐] {what}（{target.GetType().Name}）：{sb}");
            }
            catch (Exception e)
            {
                Log.Info($"[巨兽·音乐] {what} 属性读不出：{e.GetType().Name}");
            }
        }

        private static bool _loggedNames;

        /// <summary>把一个类型的**公开静态成员**（属性 + 方法）念出来，只打一次。
        /// <para>这是本轮最有效的诊断：`RunManager` 那次就是靠它发现"根本没有 CurrentRun 这个属性"，
        /// 省掉了无穷轮的猜名字。</para></summary>
        private static void LogMembers(Type? type, string what)
        {
            if (type == null || _loggedNames)
            {
                return;
            }

            _loggedNames = true;

            try
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

                var props = new System.Text.StringBuilder();
                foreach (System.Reflection.PropertyInfo p in type.GetProperties(F))
                {
                    props.Append(p.Name).Append(' ');
                }

                var methods = new System.Text.StringBuilder();
                foreach (System.Reflection.MethodInfo m in type.GetMethods(F))
                {
                    if (!m.IsSpecialName)
                    {
                        methods.Append(m.Name).Append(' ');
                    }
                }

                Log.Info($"[巨兽·音乐] {what}（{type.Name}）属性：{props}｜方法：{methods}");
            }
            catch (Exception e)
            {
                Log.Info($"[巨兽·音乐] {what} 读不出：{e.GetType().Name}");
            }
        }

        /// <summary>归一化：只留字母数字、转小写 —— 用来抹平 `RAY_MOD…` 与 `RayMod` 的写法差。</summary>
        private static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);

            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }

            return sb.ToString();
        }

        public override void _Ready()
        {
            try
            {
                AudioStream? stream = ResourceLoader.Load<AudioStream>(TrackPath);

                if (stream == null)
                {
                    if (!_reported)
                    {
                        _reported = true;
                        Log.Info($"[巨兽·音乐] 读不到 {TrackPath}（mp3 没进 pck？）");
                    }

                    return;
                }

                // 循环：mp3 / ogg 各有自己的 Loop 开关
                if (stream is AudioStreamMP3 mp3)
                {
                    mp3.Loop = true;
                }
                else if (stream is AudioStreamOggVorbis ogg)
                {
                    ogg.Loop = true;
                }

                _player = new AudioStreamPlayer
                {
                    Stream = stream,
                    VolumeDb = VolumeDb,
                    Autoplay = false,
                };

                AddChild(_player);
            }
            catch (Exception e)
            {
                if (!_reported)
                {
                    _reported = true;
                    Log.Info($"[巨兽·音乐] 载入失败：{e.GetType().Name}: {e.Message}");
                }
            }
        }

        public override void _Process(double delta)
        {
            if (_player == null)
            {
                return;
            }

            // ★ 2026-10-19 用户："应该是游玩莱伊才播放" ⇒ 先判当前局是不是莱伊。
            //   判据读不到时 **fail-open**（照放）—— 判据失败导致"本来该有音乐却静音"
            //   比"多放一段音乐"糟得多。判据每 0.5s 算一次（反射不便宜，不必每帧）。
            _poll += (float)delta;

            if (_poll >= 0.5f)
            {
                _poll = 0f;
                _isRai = IsRaiRun();
            }

            // ★ 2026-10-19 修：**只有"确认在莱伊的局里"才播**。
            //   原来 `_isRai` 默认 true、读不到就播 ⇒ 主菜单（压根没有局）也开始放
            //   —— 用户："进入游戏就开始播放"。现在默认 false，判据必须**正面确认**。
            bool want = VoiceState.MusicEnabled && _isRai == true;

            if (want)
            {
                if (!_player.Playing)
                {
                    _player.Play();
                }

                // ★ 2026-10-19 用户："游戏自己的音乐还在播放" ⇒ 压制原 BGM。
                //   节流 1 秒：游戏在换房间/进战斗时会**重新起音乐**，停一次不干净。
                _duck -= (float)delta;

                if (_duck <= 0f)
                {
                    _duck = 1.0f;
                    StopGameMusic();
                }
            }
            else if (_player.Playing)
            {
                _player.Stop();
            }
        }

        /// <summary>
        /// ★ 压掉**游戏自己的音乐**（2026-10-19 用户："游戏自己的音乐还在播放"）。
        ///
        /// <para><b>★★ 2026-10-19 崩溃修复（重要，别再改回去）</b>：
        /// 这里原来<b>还</b>调了 <c>NRun.Instance.RunMusicController.StopMusic()</c>，
        /// 而那个方法的实现是（<c>NRunMusicController.cs</c>）：
        /// <code>
        /// _proxy.Call("update_global_parameter", "Progress", 0);
        /// _proxy.Call("stop_music");
        /// _proxy.Call("stop_ambience");
        /// _currentTrack = null; _failedTrack = null;
        /// UnloadActBanks();          // ← ★ 把 FMOD 的 act bank 整个卸载
        /// </code>
        /// 而本类<b>每 1 秒</b>调一次它 ⇒ <b>每秒卸载一次音频 bank</b>。
        /// 同时 <c>NRunMusicController.UpdateTrack()</c> 里有一段<b>只在休息点</b>走：
        /// <code>
        /// if (_runState.CurrentRoom is RestSiteRoom)
        ///     _proxy.Call("update_campfire_ambience", 0);   // ← 引用已被卸载的 bank
        /// </code>
        /// 结果：<b>一进休息点就 <c>0xc0000005</c> 访问冲突、进程直接被杀</b>
        /// （Windows 事件日志实证：<c>NRunMusicController.UpdateTrack</c> →
        /// <c>RunManager.EnterRoomInternal</c> → <c>RestSiteRoom.EnterInternal</c>）。
        /// 战斗/地图不走那条分支，所以只在这个房间必崩。</para>
        ///
        /// <para>⇒ <b>只用 <c>NAudioManager.StopMusic()</c></b>：它的实现是
        /// <c>_audioNode.Call("stop_music")</c>（<c>NAudioManager.cs:217</c>）——
        /// <b>只停音乐，不碰 bank、不停环境音</b>。压 BGM 的效果一样，但不会崩。</para>
        /// </summary>
        private static void StopGameMusic()
        {
            try
            {
                object? audio = Prop(FindType("NAudioManager"), "Instance", isStatic: true);

                if (audio != null)
                {
                    CallInstance(audio, "StopMusic");
                }
            }
            catch
            {
                // 压不住就算了，别影响播我们自己的
            }
        }

        /// <summary>
        /// 当前这一局是不是**莱伊**：反射走 <c>RunManager.Instance</c> → 局 → 玩家 → 角色 → <c>Id.Entry</c>，
        /// 和 mod id 比（大小写不敏感）。
        ///
        /// <para><b>2026-10-19 第二轮修正</b>：改成<b>只有正面确认才返回 true</b>。
        /// 任何一步读不到（主菜单没有局、RunManager 找不到、玩家列表空、id 是空）
        /// 一律 <b>false</b> —— 原来的 fail-open 会让主菜单就开始放音乐（用户实机抓到）。</para>
        /// </summary>
        private static bool IsRaiRun()
        {
            try
            {
                // ★★★ 2026-10-19 第八轮（**读源码定的**，不再反射试错）：
                //   `_decomp\sts2_full\MegaCrit.Sts2.Core.Context\LocalContext.cs` 写明
                //   **GetMe 的 5 个重载全部需要参数**（playerCollection / combatState / 玩家列表 / 生物列表），
                //   **没有无参版** ⇒ 前七轮"GetMe → null"是因为我传不了参数。
                //   游戏自己的标准写法（CombatRoomHandler.cs:42 等多处一致）：
                //       LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState())
                Type? ctx = FindType("LocalContext");
                object? mgr = Prop(FindType("RunManager"), "Instance", isStatic: true);
                object? state = CallInstance(mgr, "DebugOnlyGetState");
                object? me = CallStaticArg(ctx, "GetMe", state);

                if (TryMatch(me) is bool direct)
                {
                    return direct;
                }

                if (!_loggedDiag)
                {
                    _loggedDiag = true;
                    Log.Info($"[巨兽·音乐] 诊断C：RunManager → {(mgr == null ? "null" : "ok")}" +
                             $" · State → {(state == null ? "null" : state.GetType().Name)}" +
                             $" · GetMe(State) → {(me == null ? "null" : me.GetType().Name)}");
                }

                if (me != null)
                {
                    LogNames(me, "玩家对象属性");
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读一个"玩家对象"的角色 id 并比对；读不到返回 <c>null</c>。</summary>
        private static bool? TryMatch(object? player)
        {
            string? id = Prop(Prop(Prop(player, "Character"), "Id"), "Entry") as string;

            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            // ★ 不能直接 Contains：游戏 id 形如 `RAY_MOD_CHARACTER`（大写+下划线），
            //   而 `Entry.ModId` 是 `RayMod`（驼峰）⇒ 直接比永远 false。归一化后比。
            bool hit = Norm(id!).Contains(Norm(Entry.ModId));

            if (!_loggedId)
            {
                _loggedId = true;
                Log.Info($"[巨兽·音乐] 角色 id = `{id}` · modId = `{Entry.ModId}` ⇒ 匹配 = {hit}");
            }

            return hit;
        }

        /// <summary>
        /// 调一个**静态重载方法**（按参数个数选）。
        ///
        /// <para>★★★ 2026-10-19 第七轮（**这就是根因**）：`LocalContext.GetMe` 有 <b>5 个重载</b>，
        /// 而 `Type.GetMethod(name, flags)` 在**有多个同名重载时会抛 `AmbiguousMatchException`**
        /// —— 被我的 `catch` 吞成 null ⇒ 前六轮全部"类型找到了但取不到玩家"。
        /// 现在按参数个数逐个挑，不再用会歧义的 `GetMethod(name, flags)`。</para>
        /// </summary>
        private static object? CallStatic(Type? type, string name, int argc = 0)
        {
            if (type == null)
            {
                return null;
            }

            try
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

                foreach (System.Reflection.MethodInfo m in type.GetMethods(F))
                {
                    if (m.Name != name || m.GetParameters().Length != argc)
                    {
                        continue;
                    }

                    object?[] argv = new object?[argc];

                    for (int i = 0; i < argc; i++)
                    {
                        argv[i] = null;
                    }

                    return m.Invoke(null, argv);
                }
            }
            catch (Exception e)
            {
                Log.Info($"[巨兽·音乐] 调 {name}/{argc} 失败：{e.GetType().Name}: {e.Message}");
            }

            return null;
        }

        /// <summary>调**实例**上的无参方法（`RunManager.Instance.DebugOnlyGetState()`）。</summary>
        private static object? CallInstance(object? target, string name)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                foreach (System.Reflection.MethodInfo m in target.GetType().GetMethods(F))
                {
                    if (m.Name == name && m.GetParameters().Length == 0)
                    {
                        return m.Invoke(target, null);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Info($"[巨兽·音乐] 调实例方法 {name} 失败：{e.GetType().Name}: {e.Message}");
            }

            return null;
        }

        /// <summary>调**静态**方法，带 1 个参数；同名重载逐个试，谁的参数类型能吃下 <paramref name="arg"/> 就调谁
        /// （`LocalContext.GetMe` 有 5 个重载，参数类型各不相同）。</summary>
        private static object? CallStaticArg(Type? type, string name, object? arg)
        {
            if (type == null || arg == null)
            {
                return null;
            }

            const System.Reflection.BindingFlags F =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

            foreach (System.Reflection.MethodInfo m in type.GetMethods(F))
            {
                if (m.Name != name)
                {
                    continue;
                }

                System.Reflection.ParameterInfo[] ps = m.GetParameters();

                if (ps.Length != 1 || !ps[0].ParameterType.IsInstanceOfType(arg))
                {
                    continue;
                }

                try
                {
                    return m.Invoke(null, new[] { arg });
                }
                catch (Exception e)
                {
                    Log.Info($"[巨兽·音乐] 调 {name}({ps[0].ParameterType.Name}) 失败：{e.GetType().Name}: {e.Message}");
                }
            }

            return null;
        }

        private static object? Prop(object? target, string name)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                return target.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static object? Prop(Type? type, string name, bool isStatic)
        {
            if (type == null)
            {
                return null;
            }

            try
            {
                return type.GetProperty(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static Type? FindType(string simpleName)
        {
            foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // ★★ 2026-10-19 第五轮修（**这就是一直不播的真因**）：
                //    原来 `try` 包在**整个循环外面**，而 `asm.GetTypes()` 在 Godot 的程序集上
                //    会抛 `ReflectionTypeLoadException` ⇒ **第一个抛异常的程序集就把整个搜索中断**，
                //    `FindType` 永远返回 null ⇒ `LocalContext` / `RunManager` 全部"找不到" ⇒ 永不播放。
                //    修法：**每个程序集单独兜**，抛了就跳过它继续找下一个。
                Type[] types;

                try
                {
                    types = asm.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    types = e.Types;                 // 部分能读就继续用能读的那部分
                }
                catch
                {
                    continue;
                }

                foreach (Type t in types)
                {
                    if (t != null && t.Name == simpleName)
                    {
                        return t;
                    }
                }
            }

            return null;
        }
    }
}
