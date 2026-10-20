using System;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using STS2RitsuLib;
using STS2RitsuLib.Interop;
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod;

[ModInitializer(nameof(Initialize))]
public partial class Entry
{
    // ModId 需要和 RayMod.json 里的 id 保持一致。
    // res://RayMod/... 里的 RayMod 是 PCK 资源目录，不是 C# namespace。
    public const string ModId = "RayMod";
    public const string ResPath = $"res://{ModId}";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // 以下示例默认已经在 Entry.Initialize() 中调用了
        // RitsuLibFramework.EnsureGodotScriptsRegistered(...) 和
        // ModTypeDiscoveryHub.RegisterModAssembly(...)，否则自动注册不会生效。
        //
        // Godot C# 脚本注册只负责让 pck 中的脚本类型能被 Godot 找到。
        // 这一步和 RitsuLib 的内容自动注册不是同一件事，两个都需要保留。
        RitsuLibFramework.EnsureGodotScriptsRegistered(assembly, Logger);

        // 自动注册扫描会读取当前程序集里的 RegisterCard/RegisterRelic 等 attribute。
        // 新增内容类后，只要 attribute 写对，通常不需要在入口里手动逐个注册。
        ModTypeDiscoveryHub.RegisterModAssembly(ModId, assembly);

        // ── 卡牌手牌发光规则（2026-10-07）──
        // 条件生效时让卡发光（金=加成已满足 / 红=有代价），详见 Cards/CardGlowRules.cs。
        // 这一步是**显式**注册：ModCardHandGlowRegistry 不参与 attribute 自动扫描。
        Cards.CardGlowRules.Register();
        Logger.Info($"Card glow rules registered.");

        // ── 换肤（2026-10-07）──
        // 读皮肤选择必须**早于第一场战斗**：形象场景实例化的那一刻就要知道装哪套骨架
        // （Art/RaySkinApplier 在场景进树前把 skeleton_data_res 塞进去）。
        // 读盘失败/首次运行都会回退默认皮肤，不会影响游戏。
        Art.RaySkinState.EnsureLoaded();

        // ── 语音（2026-10-07）──
        // 同样要早于第一场战斗：战斗开始钩子会立刻用到语种/开关。
        // 读盘失败一律回退默认值（日文 / 三类全开），绝不影响进游戏。
        Audio.VoiceState.EnsureLoaded();

        // ── 角色音频（2026-10-08）──
        // 把 AttackSfx/CastSfx/DeathSfx/CharacterSelectSfx 换成本工程的 wav
        // （RitsuLib 的虚拟 FMOD 事件）。不注册的话会退回 PlaceholderCharacterId
        // ——也就是**铁甲战士**的音（用户实机抓到"攻击牌响别人的音"）。
        Audio.CharacterSfx.RegisterVirtualEvents();

        // ★ 主题曲（2026-10-19 用户指定）：游玩莱伊时循环播放，设置页可开关。
        //   挂一个常驻轮询节点（幂等），开关变化下一帧生效。
        Audio.RayMusic.Attach();

        // 设置页（模组设置 → RayMod → 莱伊 · 皮肤）。内部已经包了 try/catch，
        // 注册失败只少一个界面，不影响换肤本身。
        RayMod.Settings.RayModSkinSettings.Register();

        // 设置页（模组设置 → RayMod → 莱伊 · 语音与音效）：语种 + 按大类开关 + 音效开关/音量。
        RayMod.Settings.RayModVoiceSettings.Register();

        // 设置页（模组设置 → RayMod → 莱伊 · 界面）：HUD 计数器的位置与大小。
        // ⚠️ 单独一页 —— 它跟语音没关系（用户 2026-10-08 指出"为什么 ui 设置放在语音里"）。
        RayMod.Settings.RayModHudSettings.Register();

        // 设置页（模组设置 → RayMod → 莱伊 · 特效）：**特效总开关** + 三个分类开关。
        // ★ 2026-10-13 用户："在设置里面加一个特效开关的按钮"。纯开关、不碰任何数值与规则。
        RayMod.Settings.RayModVfxSettings.Register();

        // ── 唯一的 Harmony 补丁（2026-10-04）──
        // Patches/BarlessMonsterHealthBarPatch.cs：MinionLib 在 NCreature.ToggleIsInteractable 上
        // 把参数 on 强改成 true，把"沙地兽声明自己不显示血条"的结果吃掉了；我们在它之后（Postfix）
        // 把血条压回去。PatchAll 只扫本程序集，不会碰别的 mod。
        var harmony = new HarmonyLib.Harmony("RayMod");
        harmony.PatchAll(assembly);

        // ★★ 补丁自检（2026-10-12）：**Harmony 的补丁失败是完全静默的** ——
        //    PatchClassProcessor.ReportException 只在 Harmony.DEBUG 或补丁类带 debug=true 时才写日志，
        //    而 PatchAll 又把异常吞在内部（它自己不抛）⇒ "目标方法没找到 / 参数类型对不上"
        //    这种现象的症状就是"功能像没实现"，日志里一个字都没有（BulletAmmoCostHintPatch 曾疑似如此）。
        //    所以这里对几个关键目标**显式数一遍我们自己的补丁**：进游戏第一次就能在日志里看到。
        LogHarmonySelfCheck(harmony);

        // ★ 反射型效果的启动自检（2026-10-20）：「无限」的世界层没有 Harmony 补丁，
        //   全靠反射找容器 / 音效命令 / 抖屏，同样会在"名字对不上"时**静默不出现**。
        RayMod.Art.CardFieldVfx.LogInfinityWorldProbe();

        // ★★ 常驻心跳（2026-10-20，修用户报的"SL 一次后特效消失"）：
        //   弹链 / 绕体光环 / 巨眼 / 弹幕 都是 `PowerModel.AfterApplied` 挂上去的，
        //   而 AfterApplied 在全游戏里只有 `PowerCmd.cs:159` 一个调用点；
        //   存档是**直接反序列化能力列表**、不走 PowerCmd ⇒ SL 之后这四个全都没了。
        //   这里往 SceneTree.Root 挂一个 0.5 秒对账的心跳（换房间 / SL 都回收不到它），
        //   缺什么补什么、并且**重新认人**（SL 后是新的 Player 实例）。
        RayMod.Art.CardFieldVfx.EnsurePersistBeat();

        Logger.Info("RayMod initialized.");
    }

    /// <summary>
    /// 关键补丁自检：目标方法解析到了吗？我们自己的补丁挂上了几个？
    /// <para>⚠️ 新加"静默失效"的补丁（尤其目标带重载 / 可选参数 / 私有类型的），
    /// 一定往 <c>probes</c> 里加一行 —— 这个日志是本工程唯一能证明补丁真的挂上了的地方。</para>
    /// </summary>
    private static void LogHarmonySelfCheck(HarmonyLib.Harmony harmony)
    {
        try
        {
            // ★ 卡面弹药提示补丁挂的是【私有 3 参重载】（不是 public 的 2 参那个 —— 那个只有 3 行转发，
            //   会被 JIT 内联，补丁静默不执行，见铁律 73）⇒ 自检也必须盯 3 参这个。
            var descriptionPreviewType =
                typeof(MegaCrit.Sts2.Core.Models.CardModel).GetNestedType(
                    "DescriptionPreviewType", BindingFlags.NonPublic);

            var probes = new (string Label, MethodBase? Target)[]
            {
                ("CardModel.GetDescriptionForPile(PileType,DescriptionPreviewType,Creature)",
                    descriptionPreviewType == null
                        ? null
                        : HarmonyLib.AccessTools.Method(
                            typeof(MegaCrit.Sts2.Core.Models.CardModel), "GetDescriptionForPile",
                            new[]
                            {
                                typeof(MegaCrit.Sts2.Core.Entities.Cards.PileType),
                                descriptionPreviewType,
                                typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),
                            })),
                ("CardModel.GetDescriptionForPile(PileType,Creature)［2 参，仅供参考：可能被内联］",
                    HarmonyLib.AccessTools.Method(
                        typeof(MegaCrit.Sts2.Core.Models.CardModel), "GetDescriptionForPile",
                        new[]
                        {
                            typeof(MegaCrit.Sts2.Core.Entities.Cards.PileType),
                            typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),
                        })),
                ("CardModel.get_HoverTips",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Models.CardModel), "get_HoverTips")),
                ("Hook.BeforeCardPlayed",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "BeforeCardPlayed")),
                ("CombatHistory.CardPlayStarted",
                    HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.History.CombatHistory") is { } history
                        ? HarmonyLib.AccessTools.Method(history, "CardPlayStarted")
                        : null),
                ("NCreature.ToggleIsInteractable",
                    HarmonyLib.AccessTools.Method(
                        HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Combat.NCreature"), "ToggleIsInteractable")),

                // ── ★ 2026-10-13 补齐：此前这 9 个目标**没有**进自检（本文件自己的纪律：
                //    "新加静默失效的补丁一定往 probes 里加一行"）。联机排障时，"特效/语音不出现"
                //    会被误判成"没人触发"，而实际病因常常是补丁没挂上。每个一行成本。
                ("Hook.AfterDamageGiven",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "AfterDamageGiven")),
                ("Hook.BeforeCombatStart",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "BeforeCombatStart")),
                ("Hook.AfterPlayerTurnStart",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "AfterPlayerTurnStart")),
                ("Hook.AfterSummon",
                    HarmonyLib.AccessTools.Method(typeof(MegaCrit.Sts2.Core.Hooks.Hook), "AfterSummon")),
                ("SfxCmd.Play(string,float)",
                    HarmonyLib.AccessTools.Method(
                        typeof(MegaCrit.Sts2.Core.Commands.SfxCmd), "Play", new[] { typeof(string), typeof(float) })),
                ("NCombatRoom._Ready",
                    HarmonyLib.AccessTools.Method(
                        HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom"),
                        "_Ready", Type.EmptyTypes)),
                ("AttackCommand.WithHitFx",
                    HarmonyLib.AccessTools.Method(
                        HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Commands.Builders.AttackCommand"),
                        "WithHitFx")),
                ("RunManager.UpdateRichPresence",
                    HarmonyLib.AccessTools.Method(
                        HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager"),
                        "UpdateRichPresence")),
                ("NTinyCard.SetCardBackColor",
                    HarmonyLib.AccessTools.Method(
                        HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.NTinyCard"),
                        "SetCardBackColor")),

                // ── ★ 2026-10-19「填装形态」的三个目标。**都是"小方法"（12~20 行），
                //    正是铁律 73 的高危区**（被调用方 JIT 内联 ⇒ 补丁静默不执行）。
                //    所以必须进自检，否则"没动画"会被误当成"逻辑没写到"。
                ("NCardFlyVfx.Create(NCard,PileType,bool,string)",
                    RayMod.Patches.CardReloadVfxTargets.CreateFour),
                ("NCardFlyVfx._Ready",
                    RayMod.Patches.CardReloadVfxTargets.Ready),
                ("NPlayerHand.Add(NCard,int)",
                    RayMod.Patches.CardReloadVfxTargets.HandAdd),

                // ★ 拖拽残影的入口。它是 async（状态机）⇒ 不被内联，但仍要进自检，
                //   否则"拖牌没残影"同样会被误当成"功能没写到"。
                ("NMouseCardPlay.LerpToMouse(NHandCardHolder)",
                    RayMod.Patches.CardReloadVfxTargets.DragLerp),
            };

            foreach ((string label, MethodBase? target) in probes)
            {
                if (target == null)
                {
                    Logger.Warn($"[补丁自检] {label}：❌ 目标方法没解析到 ⇒ 该补丁必然没挂上");
                    continue;
                }

                HarmonyLib.Patches? info = HarmonyLib.Harmony.GetPatchInfo(target);
                int mine = info == null
                    ? 0
                    : info.Prefixes.Count(p => p.owner == ModId)
                      + info.Postfixes.Count(p => p.owner == ModId)
                      + info.Finalizers.Count(p => p.owner == ModId)
                      + info.Transpilers.Count(p => p.owner == ModId);

                Logger.Info($"[补丁自检] {label}：目标 OK，RayMod 补丁 {mine} 个" +
                            $"（pre {info?.Prefixes.Count ?? 0} / post {info?.Postfixes.Count ?? 0}，含其它 mod）");
            }
        }
        catch (Exception e)
        {
            Logger.Warn($"[补丁自检] 自身失败（不影响游戏）：{e.GetType().Name}: {e.Message}");
        }
    }
}
