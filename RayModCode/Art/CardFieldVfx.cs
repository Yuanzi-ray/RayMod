using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

// Godot 里也有 Logger，会和 MegaCrit 的撞名（同 RichPresencePatch.cs）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★★ 2026-10-12 <b>莱伊·出牌特效【场上层】</b> —— 这一版是"能看见"优先，观感随后调。
///
/// <para><b>为什么换成这条路</b>（读两个参考 mod 得出的结论，不是我想偷懒）：
/// <list type="bullet">
///   <item><b>SilentFX</b>（创意工坊 3817850774）：68 处挂载，全是在战场上放特效；</item>
///   <item><b>RegentFX</b>（3747497501，"为储君的部分卡牌按照卡面还原了特效"）：
///         反编译里 <c>SubViewport</c> / <c>CanvasCullMask</c> / <c>CanvasTransform</c> / <c>VisibilityLayer</c>
///         <b>命中数全部为 0</b> —— 它<b>根本不把卡渲染进视口</b>，做法是
///         <c>PreloadManager.Cache.GetScene(路径).Instantiate&lt;Node2D&gt;()</c>
///         + <c>NCombatRoom.Instance.CombatVfxContainer.AddChildSafely(...)</c>。</item>
///   <item>⇒ <b>"把卡面渲染进 SubViewport 再烧"是只有 SilentFX 在用的偏门技术</b>；
///         主流做法是<b>场上层 + 游戏现成特效场景</b>。我在偏门上卡了 6 轮，改走主流。</item>
/// </list></para>
///
/// <para><b>本版做什么</b>：打「霰弹枪」（<b>对所有敌人造成伤害</b>）时 ——
/// <list type="number">
///   <item>在<b>每个敌人身上</b>播一次游戏的现成命中特效（霰弹 = 散射，所以用 <c>dagger_spray</c>，失败自动退回 <c>sandy_impact</c>）；</item>
///   <item>在<b>玩家自己身上</b>播一次"开火"特效（<c>vfx_sandy_impact</c>，暖色沙质，贴近莱伊的探井主题）。</item>
/// </list></para>
///
/// <para><b>用的是游戏官方 API</b>（<c>VfxCmd.cs</c>，全 public static，27 个现成场景）：
/// <c>PlayOnCreature</c> / <c>PlayOnCreatureCenter</c> / <c>PlayOnCreatureCenters</c> / <c>PlayOnSide</c> /
/// <c>PlayVfx</c> / <c>PlayFullScreenInCombat</c>。</para>
///
/// <para><b>安全策略</b>：与 <c>RichPresencePatch</c> 同一套 —— <b>全程反射，不引用游戏程序集类型</b>
/// （游戏改版时只是静默不生效，不会把模组带崩）。异常只记一次日志。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>出问题时的总开关。</summary>
    /// <summary>总开关。
    /// ★ 2026-10-13：从 <c>const</c> 改成**读设置页的开关**（用户："在设置里面加一个特效开关的按钮"）
    /// —— 模组设置 → RayMod → 莱伊 · 特效。关掉之后战斗逻辑一模一样，只是不放演出。</summary>
    internal static bool Enabled => RayMod.Audio.VoiceState.VfxEnabled && RayMod.Audio.VoiceState.FieldVfxEnabled;

    // ── ★★ 2026-10-12：从「只认一张卡」改成【每张卡一份配置】（用户："把特效接到更多卡"）──
    //    下面这组 static 字段 = "当前这张卡的配置"，由 SelectProfile 在每次出牌/命中时按类名刷新。
    //    ⚠️ 做成 static 字段而不是传参：文件里约 20 处引用它们，逐处改成参数只会放大改动面；
    //       而**判据仍然只有一处** = Profiles 表（铁律 50）。

    /// <summary>当前卡的显示名（只用于日志）。</summary>
    private static string CardName = "Shotgun";

    /// <summary>是否抖屏（克制型的卡关掉）。</summary>
    private static bool UseShake = true;

    /// <summary>是否打那道暖色沙尘弹幕（远程精准型的卡关掉，否则会糊）。</summary>
    private static bool UseSandJet = true;

    /// <summary>"命中点"那一层的颜色（原先是写死的暖橙）。</summary>
    private static Color CoreTint = new(1f, 0.82f, 0.55f);

    /// <summary>自己身上那层的染色（null = 不染，保持游戏原色）。</summary>
    private static Color? SelfTint;

    /// <summary>开火音效枚举名（null = 不自己放，交给游戏的角色攻击音）。</summary>
    private static string? SelfSfx = "Shot";

    /// <summary>命中时是否播自己画的那套特效（见 CardFieldVfx.SeeTheLight.cs）。</summary>
    private static bool CustomBurst;

    /// <summary>正在播的那套自绘特效（命中时由 <c>OnDamageGiven</c> 通知它引爆）。</summary>
    private static SeeLightVfx? _activeCustom;

    /// <summary>
    /// <b>这张卡是不是"自绘炸开"</b>（判据唯一出处 = <see cref="Profiles"/> 表）。
    /// <c>Patches\SkillHitVfxPatch</c> 用它决定"要不要把游戏自带的命中特效让出来" ——
    /// 这样"哪张卡自绘"只有一处判据（铁律 50）。
    /// </summary>
    internal static bool IsCustomBurstCard(string className) =>
        Profiles.TryGetValue(className, out Profile? p) && p != null && p.CustomBurst;

    /// <summary>
    /// 同上，但收"卡对象"。★ 2026-10-15：**必须用这个重载**（它带撞名门），
    /// 只有拿不到卡对象、手上确实已经有类名时才用上面那个字符串版。
    /// </summary>
    internal static bool IsCustomBurstCard(object? card) =>
        card != null && Cards.CardIdentity.IsOurs(card) && IsCustomBurstCard(card.GetType().Name);


    /// <summary>
    /// 一张卡的场上特效配置。**表里没有的卡 = 不做场上特效**（默认关闭，避免每张卡都炸一下）。
    /// 数值来源：<c>_tools\preview_batch_vfx.py</c> 里每张卡的预览设计（用户按预览挑的），
    /// 以及 §S27 用【特效测试台】实测挑出来的三层游戏特效。
    /// </summary>
    private sealed record Profile(
        string Name,
        string SelfVfx,
        float Exposure,
        float LeadIn,
        bool Shake,
        bool Jet,
        Color Tint,
        Color Core,
        string? EnemyA = null,
        string? EnemyB = null,
        string? EnemyC = null,
        string? FullScreen = null,
        Color? SelfTint = null,
        string? SelfSfx = null,
        bool CustomBurst = false);

    private static readonly Dictionary<string, Profile> Profiles = new()
    {
        // 霰弹枪（Attack/Rare）：★★ 2026-10-15 **已删除，且不要再加回来**。
        //   用户为创意工坊发布做的决定："删除霰弹枪的特效"。
        //   删之前它这套配置是：暖琥珀 + 三层（重钝击 `vfx_heavy_blunt` + 撕咬 `vfx_bite`
        //   + 钝击 `vfx_attack_blunt`）+ 全屏星爆 `vfx_starry_impact` + 抖屏 —— 而这一套
        //   本来就是**从「爆破」抄来的**（§S45.5 诊断："和爆破共用同一身份"），
        //   实战观感是"一把枪在演咬人"。删掉之后霰弹枪回到**原版普通打击**，
        //   而 `Cards\Shotgun.cs` 的伤害/弹药逻辑**一点都不受影响**（特效与结算是分开的）。
        //   ⚠️ 表里没有 = `SelectProfile` 返回 false = 这张卡不做场上特效，这是**预期行为**。

        // 爆破（Attack/Uncommon）：炮 + 重弹丸 → 落地炸开。比霰弹更闷更重：先静久一点、曝光更亮。
        ["Cannon"] = new Profile(
            "爆破", "vfx/vfx_sandy_impact", 1.45f, 0.24f, true, true,
            new Color(1.0f, 0.58f, 0.26f), new Color(1f, 0.72f, 0.40f),
            EnemyA: "vfx/vfx_heavy_blunt", EnemyB: "vfx/vfx_bite", EnemyC: "vfx/vfx_attack_blunt",
            FullScreen: "vfx/vfx_starry_impact"),

        // 狙击镜（Attack/Common）：细、硬、克制 —— 只留"命中点"一层，不要全屏爆、不要抖屏、不要沙尘。
        ["SniperScope"] = new Profile(
            "狙击镜", "vfx/vfx_sandy_impact", 1.10f, 0.10f, false, false,
            new Color(0.86f, 0.92f, 1.0f), new Color(0.92f, 0.96f, 1.0f),
            EnemyC: "vfx/vfx_attack_blunt", SelfTint: new Color(0.86f, 0.92f, 1.0f)),

        // 脱身矢（Attack/Rare）：箭飞出去 → 命中散成多道。冷蓝 + 全屏层，但没有霰弹那么暖。
        //
        // ★ 2026-10-21：<b>去掉原版全屏层和曝光脉冲</b>（原来是 true / vfx_starry_impact）。
        //   原因：自绘的全屏冲击是"读屏幕纹理再色散"，而原版全屏层在<b>更早</b>的时候
        //   就把画面压暗了；等我们的 quad 去采样时背景已经是一团暗色 ——
        //   <b>没有亮部就没有色散可言</b>，整屏只会看起来"黑了一下"。
        //   现在这一下的全屏改由 <c>SpawnScreenImpact</c> 独占，抖屏仍保留。
        ["EscapeArrow"] = new Profile(
            "脱身矢", "vfx/vfx_sandy_impact", 1.20f, 0.14f, true, false,
            new Color(0.72f, 0.86f, 1.0f), new Color(0.82f, 0.92f, 1.0f),
            EnemyA: "vfx/vfx_heavy_blunt", EnemyC: "vfx/vfx_attack_blunt",
            SelfTint: new Color(0.78f, 0.90f, 1.0f)),

        // ★★ 得见光芒（Attack/Rare，单体）：**光点从不同方向收束 → 聚成一粒 → 停一拍 → 从敌人身上炸开**。
        //    编排与设计稿见 _tools\make_vfx_gif.py + CardFieldVfx.SeeTheLight.cs（2026-10-12 用户定稿）。
        //    命中层不调游戏特效（三层都留空）—— 炸开由我们自己的节点画，所以 CustomBurst = true。
        //    ⚠️ 它也是唯一带整个特效随机旋转的卡（用户要求，见 SeeLightVfx.Spin）。
        ["SeeTheLight"] = new Profile(
            "得见光芒", "vfx/vfx_sandy_impact", 1.22f, 0.10f, true, false,
            new Color(1.0f, 0.97f, 0.92f), new Color(1f, 1f, 1f),
            SelfTint: new Color(0.94f, 0.97f, 1.0f), CustomBurst: true),

        // 巨兽（Power/Rare）：★★★ 2026-10-13 **这条旧配置已移除**。
        //   原来它借游戏原生的 `vfx/vfx_sandy_impact`（+ tint + 震屏 + 曝光脉冲）当"虚影升起"。
        //   用户当天定了新设计："用烟雾粒子组成巨兽眼睛的影子 + 鼠标靠近的地方粒子散开"，
        //   而且**眼睛是常驻的**（能力在就一直在）⇒ 那套"出牌演出"的位置本来就放不下它。
        //   现在它走 **`Art\CardFieldVfx.Feranmuts.cs` 的常驻巨眼**（挂 FeranmutsPower.AfterApplied）。
        //   ⚠️ 所以这里**故意不登记 Profile**（`SelectProfile` 返回 false = 这张卡没有出牌演出），
        //   别再把它加回来，否则会跟常驻巨眼撞车。
    };

    /// <summary>
    /// 按类名切到这张卡的配置。**返回 false = 这张卡没有专属演出，直接不做任何特效。**
    /// </summary>
    private static bool SelectProfile(object card)
    {
        // ★★ 2026-10-15 撞名门：本表按**简单类名**查，必须确认这张卡是我们自己的
        //    （原版有同名卡：铁甲战士 `Feed` / 静默猎手 `Darkness`）。判据见 `Cards\CardIdentity.cs`。
        if (!Cards.CardIdentity.IsOurs(card))
        {
            return false;
        }

        if (!Profiles.TryGetValue(card.GetType().Name, out Profile? p) || p == null)
        {
            return false;
        }

        CardName = p.Name;
        SelfVfxPath = p.SelfVfx;
        EnemyVfxA = p.EnemyA;
        EnemyVfxB = p.EnemyB;
        EnemyVfxC = p.EnemyC;
        FullScreenVfxPath = p.FullScreen;
        ExposurePeak = p.Exposure;
        LeadIn = p.LeadIn;
        UseShake = p.Shake;
        UseSandJet = p.Jet;
        WarmTint = p.Tint;
        CoreTint = p.Core;
        SelfTint = p.SelfTint;
        SelfSfx = p.SelfSfx;
        CustomBurst = p.CustomBurst;
        SandColor = p.Tint;

        return true;
    }

    // ── 分层配置（"质感"= 多层叠加；单层永远是"廉价"的那味）──
    //   层次参照两个参考 mod 的做法：自己身上（开火）→ 敌人身上（命中）→ 全屏（重量）→ 抖屏/顿帧。
    //   ★ 特效名是 2026-10-12 用【特效测试台】实测挑出来的（15 个候选各 5 帧连拍 + 逐像素量化，
    //     见 handoff §S27）。判据：变化像素占比 + 持续帧数 + 形状可辨识度 + 是否暖色。

    /// <summary>① 自己身上：开火。沙质冲击（暖色，贴莱伊的探井主题）。</summary>
    private static string SelfVfxPath = "vfx/vfx_sandy_impact";

    /// <summary>② 敌人身上（第 1 层）：<b>白蓝大光爆</b> —— 实测变化 14.9%、是其它特效的 14 倍，
    /// 唯一能单独撑起"重击"的一层（全部候选里最强）。</summary>
    private static string? EnemyVfxA = "vfx/vfx_heavy_blunt";

    /// <summary>③ 敌人身上（第 2 层）：<b>橙白锯齿爆</b> —— 暖色，最贴莱伊的配色。</summary>
    private static string? EnemyVfxB = "vfx/vfx_bite";

    /// <summary>④ 敌人身上（第 3 层）：<b>橙红星爆带亮核</b> —— 形状干脆，负责"命中点"的辨识度。</summary>
    private static string? EnemyVfxC = "vfx/vfx_attack_blunt";

    /// <summary>⑤ 全屏：<b>大团白光 → 收缩成紫色环爆</b>，持续最久（实测 5 帧都在）。</summary>
    private static string? FullScreenVfxPath = "vfx/vfx_starry_impact";

    /// <summary>★ 全屏曝光脉冲的峰值（1.0 = 不改变）。这是"明显 vs 不明显"的关键层。</summary>
    private static float ExposurePeak = 1.35f;

    /// <summary>曝光上升时长 / 回落时长（秒）。短促才有"闪"的感觉。</summary>
    private const float ExposureIn = 0.06f;
    private const float ExposureOut = 0.30f;

    /// <summary>抖屏强度 / 时长（枚举名，和游戏自己用的一致）。</summary>
    private const string ShakeStrengthName = "Strong";
    private const string ShakeDurationName = "Normal";

    /// <summary>是否抖屏/顿帧（"重量感"的主要来源，可单独关掉做 A/B 对比）。</summary>
    private static bool UseHitStop = true;

    // ── ★ 打击感：出牌后【先静一拍】再爆（用户 2026-10-12 提议，这是打击感的标准手法）──
    /// <summary>出牌到特效触发之间的停顿（秒）。0 = 立即触发（回到上一版行为）。</summary>
    private static float LeadIn = 0.18f;

    // ── ★★ 调试模式（2026-10-12 用户要求："直接渲染成它出现了一定能看到的样子"）──
    /// <summary>
    /// <b>哨兵色调试</b>：把所有特效染成刺眼的洋红/纯白，并放大、延长。
    /// <para>目的：一眼判定"到底有没有东西被画出来"。
    /// 判定完把它改回 <c>false</c> 即可恢复莱伊的配色。</para>
    /// <para>⚠️ 也把三层游戏特效的 <c>SelfModulate/Modulate</c> 染成哨兵色 ——
    /// 因为不这样做，你看不出"游戏那个特效到底播没播"。</para>
    /// </summary>
    private const bool SentinelMode = false;

    // ── ★ 莱伊的身份：沙色 / 黄铜 / 暖白（探井人 + 霰弹）──
    //    哨兵模式下会被 SentinelColor 顶掉（见 SentinelMode）。
    /// <summary>沙尘的暖色（琥珀）。</summary>
    private static Color SandColor = new(0.95f, 0.62f, 0.26f);

    /// <summary>哨兵色：刺眼洋红（暗色战场上绝不可能看漏）。</summary>
    private static readonly Color SentinelColor = new(1.0f, 0.0f, 1.0f);

    /// <summary>★ 莱伊的暖色：命中特效整体染成"暖琥珀" —— 这是"注入身份"的第一步
    /// （游戏原版特效偏冷白/蓝，染暖之后一眼是我们的）。</summary>
    private static Color WarmTint = new(1.0f, 0.72f, 0.42f);

    /// <summary>实际用色：哨兵模式 → 洋红；否则 → 莱伊的暖琥珀。</summary>
    private static Color ActualSandColor => SentinelMode ? SentinelColor : SandColor;

    /// <summary>沙尘粒子用的贴图（模组里既有，零新素材）。</summary>
    private const string SparkTexturePath = "res://RayMod/images/vfx/ray_sk_08_star_dot.png";

    private static readonly Logger Log = new("RayMod.CardVfx", LogType.Generic);

    private static bool _loggedOnce;

    // ===================== ★★ 联机门控（2026-10-13）=====================
    //
    //  背景（这三条决定了下面每个判据的存在理由）：
    //  ① STS2 的联机是**确定性锁步**：每个客户端都把**所有玩家**的钩子链各跑一遍
    //     （官方注释原文 `ActionQueueSynchronizer.cs:182-189`："Since hooks are executed
    //      on all peers…"）。所以"队友打出霰弹枪"这件事，在**我的客户端上也会跑到这里**。
    //  ② 本文件的演出**全部是按"我就是主角"写的**：抖屏 / 顿帧 / 全屏曝光脉冲 /
    //     自我层（在出牌者身上）/ 全局静态的命中序号 `_hitIndex`。
    //     不判归属 ⇒ 队友出牌会**震我的屏**、还会把我自己的"主击全套"降级成"连击单层"。
    //  ③ 参考实现就是这么做：`RegentFX` 的每一个特效补丁都以
    //     `if (!LocalContext.IsMe(card.Owner)) return;` 开头；`SilentFX` 更进一步，
    //     用 `MultiplayerFx.Active`（场上玩家 >1）识别联机，本地牌演全套、
    //     远程牌走一套单独的"held"演出。本工程先取前者（最省事、也最不容易出错）。
    //
    //  设计口径：**"本地玩家自己触发的才演"**。
    //    · 单机下判据恒为真（`NetId = 1` 且玩家 NetId = 1 ⇒ `IsMe` 恒真）⇒ **行为零变化**；
    //    · 判不了的情况（反射失败 / owner 为 null）一律**放行**（fail-open），
    //      绝不因为一个判据把演出整个弄没（工程铁律：宁可多演，不可静默消失）。
    //  ⚠️ 只用于**表现层**。绝不可用它去挡"改游戏状态"的代码 —— 那会造成一端改一端不改
    //     ⇒ 校验和不匹配 ⇒ 客机被踢（`ChecksumTracker.cs:151`）。

    private static MethodInfo? _isMePlayerMethod;
    private static MethodInfo? _isMeCreatureMethod;
    private static bool _isMeLookupDone;

    /// <summary>
    /// ★★ <b>这张卡是不是"本地玩家自己打出的"</b>（联机门控）。
    /// <b>判不了（拿不到卡 / 拿不到 Owner）返回 true</b>。
    ///
    /// <para>★★ 2026-10-15 追加<b>第二条</b>：本地玩家本人还得<b>是莱伊</b>（§S47）。
    /// 只判"是不是我的"在单机下恒为真 ⇒ 装了本 mod 玩别的角色时，
    /// 命中音（<c>PlayRaySfx</c>）与"抖屏/顿帧/曝光脉冲"也会跟着响/跟着抖。</para>
    /// </summary>
    internal static bool IsMyCard(object? card)
    {
        if (card == null)
        {
            return true;
        }

        object? owner = GetProperty(card, "Owner");

        if (!IsLocalPlayer(owner))
        {
            return false;
        }

        // owner 是莱伊才演 —— 判不了（拿不到 Character）时 fail-open 放行。
        object? character = owner == null ? null : GetProperty(owner, "Character");

        if (character == null)
        {
            return true;
        }

        return character.GetType().Name == "RayModCharacter";
    }

    /// <summary>
    /// ★★ <b>这个玩家/生物是不是"本地玩家"</b>（联机门控）。<b>判不了返回 true</b>。
    ///
    /// <para>本文件刻意**不引用游戏程序集的任何类型**（见 `CardPlayVfxPatch` 的安全策略），
    /// 所以 `LocalContext.IsMe` 走反射拿 —— 与 `Patches\RichPresencePatch.cs` 取
    /// `LocalContext.GetMe` 是同一套做法。游戏改版找不到它时**只是失去门控**（回到旧行为），
    /// 不会让整个 mod 起不来。</para>
    /// </summary>
    internal static bool IsLocalPlayer(object? who)
    {
        try
        {
            if (who == null)
            {
                return true;
            }

            EnsureIsMeLookup();

            MethodInfo? m = null;

            if (_isMePlayerMethod != null
                && _isMePlayerMethod.GetParameters()[0].ParameterType.IsInstanceOfType(who))
            {
                m = _isMePlayerMethod;
            }
            else if (_isMeCreatureMethod != null
                     && _isMeCreatureMethod.GetParameters()[0].ParameterType.IsInstanceOfType(who))
            {
                m = _isMeCreatureMethod;
            }

            if (m == null)
            {
                return true;   // 找不到判据 ⇒ 放行
            }

            return m.Invoke(null, new[] { who }) is true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 只挑出 <c>LocalContext</c> 里**单参数**的 <c>IsMe</c> 重载（<c>Player</c> / <c>Creature</c>）。
    /// ⚠️ 必须按**参数类型**挑：`LocalContext` 有 6 个同名 <c>GetMe</c> 先例，中文文档记过这个坑。
    /// </summary>
    private static void EnsureIsMeLookup()
    {
        if (_isMeLookupDone)
        {
            return;
        }

        _isMeLookupDone = true;

        try
        {
            Type? t = AccessTools_TypeByName("MegaCrit.Sts2.Core.Context.LocalContext");

            if (t == null)
            {
                LogOnce("[联机门控] 找不到 LocalContext ⇒ 场上特效不做本地分派（行为回到适配前）");
                return;
            }

            foreach (MethodInfo m in HarmonyLib.AccessTools.GetDeclaredMethods(t))
            {
                if (!m.IsStatic || m.Name != "IsMe")
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length != 1)
                {
                    continue;
                }

                string typeName = ps[0].ParameterType.Name;

                if (typeName == "Player")
                {
                    _isMePlayerMethod = m;
                }
                else if (typeName == "Creature")
                {
                    _isMeCreatureMethod = m;
                }
            }
        }
        catch
        {
            // 反射失败 ⇒ 保持 null ⇒ 放行。
        }
    }

    /// <summary>
    /// 出牌钩子的入口（场上层）。任何一步不满足就<b>静默返回</b>。
    /// </summary>
    /// <param name="card">打出的 <c>CardModel</c>（只读 <c>GetType().Name</c>）。</param>
    /// <param name="play">那一笔 <c>CardPlay</c>（读 <c>Player</c> / <c>Target</c>）。</param>
    /// <param name="combatState">钩子第一个参数 <c>ICombatState</c>（用来枚举敌人）。</param>
    internal static void Play(object? card, object? play, object? combatState)
    {
        if (!Enabled || card == null || play == null)
        {
            return;
        }

        // ★★ 联机门控（2026-10-13）：**只演本地玩家自己打出的牌**。
        //    这一道门同时挡住：弹幕分发（BulletModeOnCardPlay）、得见光芒的起手收束、
        //    自我层、全屏层、抖屏/顿帧（DoHitStop）、全屏曝光脉冲（TweenExposure）、
        //    以及莱伊的开火音等 —— 它们是"以出牌者为主角"的一整套。
        if (!IsMyCard(card))
        {
            return;
        }

        try
        {
            // ★★ 2026-10-20：**这几个"本张牌"的计数必须在 SelectProfile 之前清零**。
            //
            //   用户报的 bug 原话："霰弹枪 sl 之后它牌面特效消失了"。
            //   真因不是 SL，而是**这两个计数器一辈子只清零一次**——
            //   原来 `_hitIndex = 0` 写在下面的 `if (!SelectProfile(card) …) return;` **之后**，
            //   那条分支只对"有 vfx 配置的牌"才走到；而霰弹枪 / 狙击这些枪械卡**没有配置**
            //   ⇒ `_shotgunHitIndex` 从进程启动开始一直加，`i == 0` 只在**全进程第一发**成立
            //   ⇒ 牌面烫穿（`BurnPlayedCard` 挂在那句 `if (i == 0)` 里）**一辈子只演一次**。
            //   SL 只是打断了玩家的连续测试，让人以为是"SL 之后坏的"。
            //
            //   `_burnCardId` 也要清：`NCard` 是**池化对象**（`IPoolable`），
            //   同一张牌再打一次实例 id 可能一样，不清就会被 1.2 秒去重挡掉。
            _hitIndex = 0;
            _shotgunHitIndex = 0;
            _burnCardId = 0UL;
            _burnAtMs = 0UL;

            // 弹环也必须"这张牌归这张牌"：上一轮的环可能还剩几颗没打出去，
            // 不清的话这次出牌会接着消耗上一轮那圈（视觉上是"弹从旧的环里飞出来"）。
            _shotgunRing = null;
            _shotgunRingSlot = 0;

            // ★★ 「子弹模式」的常驻弹幕与"这张卡有没有自己的场上特效"**无关**
            //    （弹药牌大多没有配置文件）⇒ 必须放在 SelectProfile 的早退**之前**。
            //    见 CardFieldVfx.BulletMode.cs。
            BulletModeOnCardPlay(card, play, combatState);

            // 「灵异录像带」：牌面磁带 + 本回合手牌东倒西歪。
            // 同样必须在 SelectProfile 早退**之前** —— 这张牌没有 vfx 配置文件。
            TapeCardOnPlay(card, play);

            if (!SelectProfile(card) || TestModeIsOn())
            {
                return;
            }

            // ★ 2026-10-15：原来这里挂过"特效测试台"（VfxTestHarness，开发用，进战斗按 F9 连拍落盘）。
            //   发布版已整个删除该文件 —— 发布物里不该有一个"按键就往玩家存档目录写 200MB PNG"的开关。

            // ★★ 自绘特效（得见光芒）的**起手收束放在这里 —— 同步执行，绝不能放进 LeadIn 延迟**。
            //    实机踩到（2026-10-12）：伤害钩子**先到**、起手反而被 LeadIn 推迟 ⇒
            //    引爆信号到达时节点还不存在 ⇒ 引爆被丢弃 ⇒ 只能等 2.6s 兜底（"卡回手牌好久才炸开"）。
            //    锚在**莱伊自己**身上（出牌那一刻的 play.Target 的 GetCreatureNode() 会返回 null）。
            if (CustomBurst)
            {
                object? meSelf = GetProperty(play, "Player");
                meSelf = meSelf == null ? null : GetProperty(meSelf, "Creature");

                // ★ 还要把**目标**传进去：光柱要从莱伊头上指向敌人（用户 2026-10-12），
                //   而敌人坐标在出牌这一刻就查得到（原版 HandOfGreed 在 OnPlay 里就是这么取的）。
                _activeCustom = SpawnSeeLightGather(meSelf, GetProperty(play, "Target"));
            }

            // ★ 每次出牌重置"本张牌"的计数（多段结算按序分级，见 OnDamageGiven）
            _hitIndex = 0;
            MainScreenLayerDone = false;

            // ★ 先静一拍（用户提议）：把整段演出往后推 LeadIn 秒，打击感会明显好于"一打就爆"。
            //   用 SceneTree 计时器（游戏流程里最安全的延迟方式，不需要 async/await 连接点）。
            if (LeadIn > 0.001f)
            {
                SceneTree? tree = (Engine.GetMainLoop() as SceneTree);

                if (tree == null)
                {
                    Fire(card, play, combatState);
                    return;
                }

                var timer = tree.CreateTimer(LeadIn, processAlways: false);
                timer.Timeout += () => Fire(card, play, combatState);

                return;
            }

            Fire(card, play, combatState);
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效] 失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>真正放特效（延迟之后再走这里）。</summary>
    private static void Fire(object card, object play, object? combatState)
    {
        try
        {
            object? player = GetProperty(play, "Player");
            object? me = player == null ? null : GetProperty(player, "Creature");

            // ① 自己身上：开火 + ★ 莱伊自己的开火音（p_atk_sndshotgun_n）
            if (me != null)
            {
                if (SentinelMode)
                {
                    SpawnTintedOnCreature(me, SelfVfxPath, SentinelColor);
                }
                else if (SelfTint is { } selfTint)
                {
                    // 有专属配色的卡（冷蓝 / 钢白…）走染色这条路；霰弹枪保持原样不染。
                    SpawnTintedOnCreature(me, SelfVfxPath, selfTint);
                }
                else
                {
                    InvokeVfx("PlayOnCreature", me, SelfVfxPath);
                }

                if (SelfSfx != null)
                {
                    PlayRaySfx(SelfSfx);
                }
            }

            // ①b ★★ 自绘特效（得见光芒）：**这里就起手收束**，命中那一刻才引爆。
            //     用户 2026-10-12 实测反馈："伤害应该在特效炸开的瞬间结算"
            //     —— 所以收束必须提前到出牌这一拍，引爆由 OnDamageGiven 触发（两边对齐）。

            // ①b ★★ 自绘特效（得见光芒）：**这里就起手收束**，命中那一刻才引爆。
            //     用户 2026-10-12 实测反馈："伤害应该在特效炸开的瞬间结算"
            //     —— 所以收束必须提前到出牌这一拍，引爆由 OnDamageGiven 触发（两边对齐）。

            // ② ★ 命中 / 沙尘 / 命中音【不在这里】—— 它们挂在真正造成伤害的那一刻
            //    （Hook.AfterDamageGiven → OnDamageGiven），否则必然和结算对不齐。
            //    这里只放"不依赖目标"的层：全屏 + 屏幕反馈。

            // ③ 全屏层：给整次出牌"重量"（克制型的卡可以在 Profiles 里关掉）
            if (FullScreenVfxPath != null)
            {
                InvokeVfx("PlayFullScreenInCombat", FullScreenVfxPath, me);
            }

            // ④ 抖屏 / 顿帧 —— "重量感"的主力（游戏自己打重型攻击就是这条）
            bool shook = UseHitStop && UseShake && DoHitStop();

            // ⑤ ★ 全屏曝光脉冲 —— 这一层才是"明显 vs 不明显"的分水岭
            //    （手法来自参考实现 RegentFX 的 WorldEnvironmentUtil.TweenExposure：
            //      NGame.Instance.ActivateWorldEnvironment() → 补间 "environment:tonemap_exposure"）
            bool flashed = TweenExposure();

            LogAlways($"[场上特效·开火] 卡={CardName} 延迟={LeadIn}s 自己={SelfVfxPath}"
                      + $" 全屏={FullScreenVfxPath} 抖屏={shook} 曝光脉冲={flashed}"
                      + $" ｜命中层由 Hook.AfterDamageGiven 触发");
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效] 触发失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★★ <b>每次真正造成伤害时调用</b>（由 <see cref="Patches.CardHitVfxPatch"/> 从
    /// <c>Hook.AfterDamageGiven</c> 转进来）。
    ///
    /// <para><b>★ 2026-10-12 用户实测反馈："出了一大堆效果"</b> —— 因为霰弹枪是<b>多段结算</b>
    /// （"每层弹药使其再结算一次"），每一段都重播全套（三层 + 全屏）⇒ 糊成一团。
    /// 现在按<b>本张牌的命中序号</b>分级：
    /// <list type="bullet">
    ///   <item><b>第 1 次命中（主击）</b>：全套 —— 大光爆 + 锯齿爆 + 星爆 + 全屏层；</item>
    ///   <item><b>后续命中（连击）</b>：<b>只演最干脆的一层</b> + 沙尘 + 命中音，
    ///         并且之间留一点间隔，读起来像"一枪打出好几发"，而不是"叠在一起"。</item>
    /// </list></para>
    /// </summary>
    internal static void OnDamageGiven(object cardSource, object? dealer, object target)
    {
        if (!Enabled)
        {
            return;
        }

        // ★★ 联机门控（2026-10-13）：命中层同样**只演本地玩家自己的牌造成的伤害**。
        //    不判的话有两条实际后果：① 队友的攻击在我的屏幕上播命中层（而我这边
        //    压根没出过牌）；② `_hitIndex` 是全局静态的 —— 队友那一击会把它推上去，
        //    把我自己的"主击全套"降级成"连击单层"（见下面 index==0 的分级逻辑）。
        if (!IsMyCard(cardSource))
        {
            return;
        }

        // ★★ 2026-10-20「狙击」自成一套：每一段伤害 = 一发真子弹（枪口火 + 弹道 + 弹壳 + 命中）。
        //    它原来用的是 vfx_attack_slash（一刀刀光）—— 一把枪打出 8 道刀光。
        //    返回 true 表示已经接管：绝不能再叠通用命中层，否则子弹和刀光同时出现。
        //    `main` 沿用通用层同一套判据（同一张牌的第 1 段伤害）⇒ 连发时只有第 1 发抖屏。
        if (SnipeShot(cardSource, dealer, target, main: _hitIndex == 0))
        {
            _hitIndex++;
            return;
        }

        // ★★ 2026-10-20「霰弹枪」：每一点伤害 = 一记<b>从随机方向</b>打进来的命中
        //    （用户口径："有多少子弹就从不同方向触发击中敌人特效多少次"）。
        //    它原来用的是 vfx_attack_blunt + blunt_attack.mp3 —— 霰弹枪打出了锤子闷响。
        if (ShotgunHit(cardSource, dealer, target))
        {
            return;
        }

        // ★★ 2026-10-20「灵异录像带」：被打的那个敌人头上先浮出「R.I.P」，
        //    然后一块墓碑飞过来落在它脚边立住，一直留到本回合结束。
        if (TapeGraveOnHit(cardSource, target))
        {
            return;
        }

        // ★★ 2026-10-20「脱身矢」：一根银蓝矛飞过去扎在敌人身上，命中那一瞬
        //    她被反作用甩出去、0.65 秒后弹回来；敌人也会被推出 30px 再回位。
        //    它原来用的是 vfx_attack_slash（一刀横向刀光）—— 和"掷矛"没关系。
        if (EscapeArrowHit(cardSource, dealer, target))
        {
            return;
        }

        try
        {
            if (!SelectProfile(cardSource) || TestModeIsOn())
            {
                return;
            }

            int index = _hitIndex++;
            bool main = index == 0;

            if (main)
            {
                // 主击：三层 + 暖色（哪层没配就不放，见 Profiles）
                if (EnemyVfxA != null) SpawnTintedOnCreature(target, EnemyVfxA, WarmTint);
                if (EnemyVfxB != null) SpawnTintedOnCreature(target, EnemyVfxB, WarmTint);
                if (EnemyVfxC != null) SpawnTintedOnCreature(target, EnemyVfxC, CoreTint);

                // ★ 得见光芒这类自己画的卡：命中那一刻在目标身上播自己那套（见 SeeTheLight.cs）
                // ★★ 命中那一刻**引爆**（收束早在出牌时就起手了）
                if (CustomBurst) ExplodeActiveCustom(target);

                // 主击的沙尘 + 命中音 + 全屏层（都按配置）
                if (UseSandJet) SpawnSandJetTo(dealer, target);
                if (SelfSfx != null) PlayRaySfx("Hit");
                if (FullScreenVfxPath != null) FullScreenLayer(dealer);

                LogAlways($"[场上特效·命中] 第 {index + 1} 次（主击）→ 全套；目标={target.GetType().Name}");
                return;
            }

            // 连击：延后一点点、只演一层最干脆的，避免糊在一起
            float delay = 0.10f * index;
            ScheduleComboHit(target, dealer, delay);

            LogAlways($"[场上特效·命中] 第 {index + 1} 次（连击）→ 延后 {delay:F2}s 单层；目标={target.GetType().Name}");
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效·命中] 失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>连击段：延后一点点再演"单层"，读起来像一枪多发的节奏。</summary>
    private static void ScheduleComboHit(object target, object? dealer, float delay)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            var timer = tree.CreateTimer(Mathf.Max(delay, 0.01f), processAlways: false);
            timer.Timeout += () =>
            {
                if (EnemyVfxC != null) SpawnTintedOnCreature(target, EnemyVfxC, CoreTint);
                if (UseSandJet) SpawnSandJetTo(dealer, target);
            };
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效·连击] 排程失败：{e.GetType().Name}");
        }
    }

    /// <summary>每张牌的主击只演一次的全屏层（原本每段伤害都会重播一遍）。</summary>
    private static void FullScreenLayer(object? me)
    {
        if (!MainScreenLayerDone)
        {
            MainScreenLayerDone = true;
            InvokeVfx("PlayFullScreenInCombat", FullScreenVfxPath, me);
        }
    }

    /// <summary>本张牌的命中计数 / 全屏层是否已演过（在出牌时重置）。</summary>
    private static int _hitIndex;
    private static bool MainScreenLayerDone;

    // ───────────────────────── ⓪ 莱伊的身份：沙尘弹幕 ─────────────────────────

    /// <summary>
    /// 沙尘弹幕（**对齐伤害时刻**用的版本，由 <see cref="Patches.CardHitVfxPatch"/> 调用）：
    /// 从 <paramref name="fromCreature"/> 朝 <paramref name="toCreature"/> 喷一束暖琥珀沙尘。
    ///
    /// <para>★ <b>为什么挂在"造成伤害"而不是"出牌"上</b>（用户 2026-10-12 指出）：
    /// 玩家说"特效延迟触发，卡也要延迟触发，不然对不齐"。正解是 ——
    /// <b>开火层挂在出牌瞬间（有意图）</b>，而<b>命中/沙尘层挂在真正造成伤害的那一刻</b>
    /// （<c>Hook.AfterDamageGiven</c>，<c>CreatureCmd.cs:411</c>）。
    /// 这样特效永远跟着伤害走，不存在"对不齐"。</para>
    ///
    /// <para>⚠️ <b>坐标系</b>：粒子加在<b>生物自己的 VfxContainer</b> 里、局部位置 0 ——
    /// 这样不管那个容器是什么坐标系，都必然出现在该生物身上。
    /// （上一版用 <c>CombatVfxContainer</c> + 世界坐标，实测**看不见** —— 十有八九就是坐标系不一致。）</para>
    /// </summary>
    internal static bool SpawnSandJetTo(object? fromCreature, object? toCreature)
    {
        try
        {
            if (fromCreature == null || toCreature == null)
            {
                return false;
            }

            Texture2D? tex = ResourceLoader.Load<Texture2D>(SparkTexturePath);

            if (tex == null)
            {
                LogAlways($"[场上特效·沙尘] 贴图加载失败：{SparkTexturePath}");
                return false;
            }

            Node? container = VfxParentForEffects();

            if (container == null)
            {
                LogAlways("[场上特效·沙尘] 拿不到目标的 VfxContainer");
                return false;
            }

            Vector2 from = WorldPos(fromCreature);
            Vector2 to = WorldPos(toCreature);
            Vector2 dir = (to - from);

            if (dir.LengthSquared() < 1f)
            {
                dir = new Vector2(1f, 0f);   // 拿不到坐标就朝右
            }

            float dist = dir.Length();

            var jet = new SandJet();
            jet.Configure(dir.Normalized(), Mathf.Clamp(dist, 200f, 1400f), tex);
            container.AddChild(jet);

            return true;
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效·沙尘] 失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>取生物自己的 VFX 容器（<c>Creature.GetVfxContainer()</c>，游戏自己就用它）。</summary>
    private static Node? CreatureOwnVfxContainer(object creature)
    {
        try
        {
            return creature.GetType().GetMethod("GetVfxContainer", Type.EmptyTypes)?.Invoke(creature, null) as Node;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从玩家位置朝每个敌人喷一束<b>暖琥珀色沙尘</b>（模组里既有贴图 + 程序化粒子，零新素材）。
    ///
    /// <para>这是"莱伊的霰弹枪"的签名动作 —— 游戏的原版特效没有这一层，
    /// 参考实现的同类做法是 <c>FitVFX</c>（让特效节点对齐"起点→终点"的转角与长度）。</para>
    ///
    /// <para>⚠️ 粒子必须<b>延后一帧点火</b>（本项目实测：同帧点火点不着，见 <c>SkillHitVfx.cs</c>）。</para>
    /// </summary>
    private static int SpawnSandBurst(object? me, List<object> enemies)
    {
        int made = 0;

        foreach (object enemy in enemies)
        {
            if (SpawnSandJetTo(me, enemy))
            {
                made++;
            }
        }

        return made;
    }

    // ⚠️ 2026-10-12 订正（别照旧结论走）：这里原来写的是"只用 GlobalPosition、**不用** VfxSpawnPosition"——
    //    **那是错的**。往 CombatVfxContainer（Control）里放节点，就该用 VfxSpawnPosition。
    //    下面三个定位方法（CreatureNodeOf / VfxPosOf / ChestPos）共用紧跟着的这一份说明。
    /// <summary>
    /// <b>取生物的"胸口"坐标 —— 专门用来当 <c>CombatVfxContainer</c> 里节点的 <c>GlobalPosition</c>。</b>
    ///
    /// <para>★ 口径出处 = 参考 mod <b>SilentFX</b>（工坊 3817850774）的写法：
    /// <c>CardFxManager</c> 里 <c>Chest = creatureNode.VfxSpawnPosition</c>，
    /// 然后 <c>frontParent.AddChild(node); node.GlobalPosition = c.Chest;</c>
    /// （<c>frontParent</c> = <c>combatRoom.CombatVfxContainer</c>）。
    /// 反编译实证：<c>NCombatRoom.cs:292</c> 是 <c>public Control CombatVfxContainer</c> —— <b>容器是 Control</b>，
    /// 而 <c>VfxSpawnPosition</c> 正是"往这个容器里放"该用的坐标。</para>
    ///
    /// <para>⚠️ 我们原来用 <see cref="WorldPos"/>（<c>GetGlobalTransform().Origin</c>）—— 那是**另一个画布分支**的坐标，
    /// 直接塞进容器当 GlobalPosition 会整体偏（2026-10-12 实机：得见光芒整套跑到画布左上角）。
    /// 本方法优先 <c>VfxSpawnPosition</c>，读不到才退回 <see cref="WorldPos"/>。</para>
    /// </summary>
    /// <summary>
    /// ★★ <b>用"房间的注册表"取生物节点</b> —— <c>NCombatRoom.Instance.GetCreatureNode(creature)</c>。
    ///
    /// <para><b>为什么它比 <c>Creature.GetCreatureNode()</c> 可靠</b>（2026-10-12 找到真凶）：
    /// 反编译实证 <c>Creature.cs:758</c> 那个方法开头就是 <c>if (TestMode.IsOn) return null;</c>，
    /// 之后才走 <c>NCombatRoom.Instance.GetCreatureNode(this)</c>；而房间那张表是
    /// <c>CreatureNodes.FirstOrDefault(c =&gt; c.Entity == creature)</c>（**按实例比对**）
    /// ⇒ 传进来的若不是同一个实例（可变克隆）就查不到。
    /// <b>原版卡是直接问房间的</b>（<c>HandOfGreed.cs:53</c>），所以我们照抄原版。</para>
    /// </summary>
    internal static object? CreatureNodeOf(object? creature)
    {
        if (creature == null)
        {
            return null;
        }

        try
        {
            Type? t = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            object? room = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            if (room == null)
            {
                return null;
            }

            foreach (MethodInfo m in room.GetType().GetMethods())
            {
                if (m.Name == "GetCreatureNode" && m.GetParameters().Length == 1)
                {
                    return m.Invoke(room, new[] { creature });
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效·定位] 房间注册表取节点失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }

        return null;
    }

    /// <summary>从生物节点读 <c>VfxSpawnPosition</c>（= 往 VFX 容器里放东西该用的坐标）。</summary>
    internal static Vector2 VfxPosOf(object? node)
    {
        if (node == null)
        {
            return Vector2.Zero;
        }

        try
        {
            object? v = node.GetType().GetProperty("VfxSpawnPosition")?.GetValue(node);
            return v is Vector2 pos ? pos : Vector2.Zero;
        }
        catch
        {
            return Vector2.Zero;
        }
    }

    internal static Vector2 ChestPos(object creature)
    {
        // ★ 第一优先：房间注册表（原版卡 HandOfGreed 的写法）—— 见 CreatureNodeOf 的注释。
        Vector2 room = VfxPosOf(CreatureNodeOf(creature));

        if (room != Vector2.Zero)
        {
            return room;
        }

        try
        {
            System.Reflection.MethodInfo? m = creature.GetType().GetMethod("GetCreatureNode", Type.EmptyTypes);
            object? node = m?.Invoke(creature, null);

            if (node != null)
            {
                System.Reflection.PropertyInfo? p = node.GetType().GetProperty("VfxSpawnPosition");
                object? v = p?.GetValue(node);

                if (v is Vector2 pos && pos != Vector2.Zero)
                {
                    return pos;
                }

                LogAlways($"[场上特效·定位] {node.GetType().Name}.VfxSpawnPosition 读不到，退回 WorldPos");
            }
            else
            {
                LogAlways("[场上特效·定位] GetCreatureNode() 为 null，退回 WorldPos");
            }
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效·定位] ChestPos 失败：{e.GetType().Name}: {e.Message}");
        }

        return WorldPos(creature);
    }

    private static Vector2 WorldPos(object creature)
    {
        try
        {
            string tName = creature.GetType().FullName ?? creature.GetType().Name;
            System.Reflection.MethodInfo? m = creature.GetType().GetMethod("GetCreatureNode", Type.EmptyTypes);

            if (m == null)
            {
                LogAlways($"[场上特效·定位诊断] {tName} 上没有 GetCreatureNode()");
                return Vector2.Zero;
            }

            object? node = m.Invoke(creature, null);

            if (node == null)
            {
                LogAlways($"[场上特效·定位诊断] {tName}.GetCreatureNode() 返回 null");
                return Vector2.Zero;
            }

            // ★★★ 2026-10-12 真凶：NCreature 【不是 Node2D】（它是 Node/CanvasItem 系），
            //   所以 `is Node2D` 判断把它挡掉了 —— 三个命中特效因此一次都没被创建。
            //   改用对任何 CanvasItem 都成立的口径：CanvasItem.GetGlobalTransform().Origin。
            //   （函数返回类型是 Variant，取出后是 Transform2D。）
            System.Reflection.MethodInfo? gt = node.GetType().GetMethod("GetGlobalTransform", Type.EmptyTypes);

            if (gt != null)
            {
                object? tr = gt.Invoke(node, null);

                if (tr is Transform2D t)
                {
                    return t.Origin;
                }
            }

            // 退路：Node2D / Control 各自的位置属性（有哪个用哪个）
            System.Reflection.PropertyInfo? gp = node.GetType().GetProperty("GlobalPosition");

            if (gp?.GetValue(node) is Vector2 v)
            {
                return v;
            }

            LogAlways($"[场上特效·定位诊断] {tName} 的节点（{node.GetType().Name}）拿不到全局坐标");
            return Vector2.Zero;
        }
        catch (Exception e)
        {
            Exception inner = e is TargetInvocationException { InnerException: { } i } ? i : e;

            LogAlways($"[场上特效·定位诊断] 取世界坐标异常：{inner.GetType().Name}: {inner.Message}");
            return Vector2.Zero;
        }
    }

    /// <summary>
    /// 沙尘弹幕的父节点 = <c>room.CombatVfxContainer</c>（<b>世界坐标容器</b>），
    /// 与命中特效保持同一套坐标系（2026-10-12 统一，别再混用生物自己的 VfxContainer）。
    /// </summary>
    private static Node? VfxParentForEffects() => CombatVfxContainer();

    private static Node? CombatVfxContainer()
    {
        try
        {
            Type? t = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            object? room = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            return room?.GetType().GetProperty("CombatVfxContainer")?.GetValue(room) as Node;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// ★★ <b>生物**后**面的特效容器</b> —— <c>room.BackCombatVfxContainer</c>
    /// （<c>NCombatRoom.cs:290</c> 是 public 属性，<c>:347</c> 取自 <c>%BackCombatVfxContainer</c>）。
    ///
    /// <para><b>为什么要它</b>（2026-10-19）：子弹模式改成「伪 3D 环轨绕她转」之后，
    /// 环的**后半段必须沉到她的精灵后面**，否则读不出"绕着转"，只会读成"贴在她身上"。
    /// 前半段仍挂在 <see cref="CombatVfxContainer"/>（生物前）。
    /// <b>两个容器都是 <c>Control</c>，但坐标原点不一定相同</b> —— 挂之前要用
    /// <c>GlobalPosition</c> 之差把后层对齐到前层的原点（见 <c>BulletSwarm.Configure</c>）。</para>
    /// </summary>
    private static Node? BackVfxParentForEffects()
    {
        try
        {
            Type? t = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom");
            object? room = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            return room?.GetType().GetProperty("BackCombatVfxContainer")?.GetValue(room) as Node;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>放一个莱伊音效（走本模组自己的 <c>SfxPlayer</c>；开关/音量由玩家设置控制）。</summary>
    private static void PlayRaySfx(string which)
    {
        try
        {
            Audio.Sfx sfx = which == "Hit" ? Audio.Sfx.Hit : Audio.Sfx.Shot;
            Audio.SfxPlayer.Play(sfx);
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效] 音效 {which} 失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// ★ 一束"沙尘弹幕"：从 <c>from</c> 朝向 <c>to</c> 的锥形粒子。
    ///
    /// <para><b>为什么自己写这一层</b>：游戏的原版特效里没有"朝敌人喷一束沙"这个动作 ——
    /// 它是莱伊的签名，必须由我们造。配色用<span>暖琥珀 + 暖白</span>，和她的探井人身份一致。</para>
    ///
    /// <para>⚠️ 粒子<b>必须延后一帧点火</b>（本项目实测：同帧点火点不着，见 <c>SkillHitVfx.cs</c>）。</para>
    /// <para>自身到点销毁 —— 这个节点不在卡池里，没人替我们回收（铁律 47）。</para>
    /// </summary>
    private sealed partial class SandJet : Node2D
    {
        private GpuParticles2D? _particles;
        private float _life;

        internal void Configure(Vector2 direction, float distance, Texture2D tex)
        {
            const float life = 0.55f;

            var mat = new ParticleProcessMaterial
            {
                Direction = new Vector3(direction.X, direction.Y, 0f),
                Spread = 9f,                                        // 窄锥 = 霰弹
                InitialVelocityMin = distance / life * 0.72f,       // 主束
                InitialVelocityMax = distance / life * 1.25f,       // 更快的那部分负责"打到"
                Gravity = new Vector3(0f, 180f, 0f),                // 稍微下坠 = 沙的重量
                DampingMin = 0.6f,
                DampingMax = 1.4f,
                ScaleMin = 0.16f + distance * 0.00020f,
                ScaleMax = 0.34f + distance * 0.00040f,
                Color = ActualSandColor,
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point,
            };

            _particles = new GpuParticles2D
            {
                Texture = tex,
                // ⚠️ 加法混合：沙色叠在暗底色上才"发亮"
                //   哨兵模式下改成【正常混合】—— 加法混合对洋红不友好（会被底色冲淡）
                Material = new CanvasItemMaterial
                {
                    BlendMode = SentinelMode
                        ? CanvasItemMaterial.BlendModeEnum.Mix
                        : CanvasItemMaterial.BlendModeEnum.Add,
                },
                ProcessMaterial = mat,
                Amount = SentinelMode ? 220 : 110,
                Lifetime = life,
                OneShot = true,
                Explosiveness = 0.95f,     // 一次性炸出去，不是持续喷
                LocalCoords = false,       // 粒子发射后留在世界里
                Emitting = false,          // ★ 延后一帧点火
                // ★ 局部位置 0 = 正好在该生物的锚点上（父节点是它自己的 VfxContainer）
                Position = Vector2.Zero,
            };

            AddChild(_particles);
        }

        public override void _Ready()
        {
            CallDeferred(nameof(Ignite));
        }

        private void Ignite()
        {
            if (_particles != null && GodotObject.IsInstanceValid(_particles))
            {
                _particles.Emitting = true;
                _particles.Restart();
            }
        }

        public override void _Process(double delta)
        {
            _life += (float)delta;

            if (_life >= 1.3f)
            {
                QueueFree();
            }
        }
    }

    // ───────────────────────── ① 调游戏官方 VfxCmd ─────────────────────────

    /// <summary><c>VfxCmd.PlayOnCreature(Creature, string path)</c> —— 打在生物身上（用 GlobalPosition）。</summary>
    private static bool PlayOnCreature(object creature, string path)
    {
        return InvokeVfx("PlayOnCreature", creature, path);
    }

    /// <summary>
    /// ★ <b>自己实例化特效</b>（哨兵/染色模式必需）：照抄 <c>VfxCmd.PlayOnCreatureCenter</c> 的两行核心
    /// （<c>PreloadManager.Cache.GetScene(SceneHelper.GetScenePath(path))</c> → 加进容器 → 设位置），
    /// 差别是<b>我们拿到了那个节点</b>，于是能染色。
    ///
    /// <para>★★ <b>坐标系统一（2026-10-12 实测修正）</b>：
    /// 用户实测"白色光爆出现在屏幕正中央，而不是敌人身上" ⇒ 说明位置传错了。
    /// 现在三条都统一到<b>世界坐标</b>：
    /// <list type="bullet">
    ///   <item>父节点 = <c>room.CombatVfxContainer</c>（房间级的世界坐标容器）；</item>
    ///   <item>位置 = <c>NCreature.GlobalPosition</c>（<b>确定</b>是世界坐标，不再用
    ///         <c>VfxSpawnPosition</c> —— 那个很可能是<b>生物自己坐标系</b>里的点，
    ///         当世界坐标用就会跑到别处）；</item>
    ///   <item>染色用 <c>SelfModulate</c>（只染这个节点，不继承父节点的其它影响）。</item>
    /// </list></para>
    /// </summary>
    private static bool SpawnTintedOnCreature(object creature, string path, Color tint)
    {
        try
        {
            Vector2 worldPos = ChestPos(creature);

            if (worldPos == Vector2.Zero)
            {
                LogAlways($"[场上特效·定位] 拿不到 {path} 的目标世界坐标");
                return false;
            }

            Node? container = CombatVfxContainer();

            if (container == null)
            {
                LogAlways("[场上特效·定位] 拿不到 CombatVfxContainer");
                return false;
            }

            PackedScene? scene = LoadGameScene(path);

            if (scene == null)
            {
                return false;
            }

            Node2D vfx = scene.Instantiate<Node2D>(PackedScene.GenEditState.Disabled);

            if (tint.A > 0f)
            {
                vfx.SelfModulate = tint;
            }

            container.AddChild(vfx);
            vfx.GlobalPosition = worldPos;

            LogAlways($"[场上特效·定位] {path} 世界坐标={worldPos}");
            return true;
        }
        catch (Exception e)
        {
            Exception inner = e is TargetInvocationException { InnerException: { } i } ? i : e;

            LogAlways($"[场上特效·哨兵] 自制特效失败 {path}：{inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    /// <summary>加载游戏包里的特效场景（<c>vfx/xxx</c> → <c>res://scenes/vfx/xxx.tscn</c>）。</summary>
    private static PackedScene? LoadGameScene(string innerPath)
    {
        try
        {
            string path = MegaCrit.Sts2.Core.Helpers.SceneHelper.GetScenePath(innerPath);

            return ResourceLoader.Load<PackedScene>(path);
        }
        catch (Exception e)
        {
            LogAlways($"[场上特效·哨兵] 场景加载失败 {innerPath}：{e.GetType().Name}");
            return null;
        }
    }

    /// <summary><c>VfxCmd.PlayOnCreatureCenter(Creature, string path)</c> —— 打在生物中心（<c>VfxSpawnPosition</c>）。</summary>
    private static bool PlayOnCreatureCenter(object creature, string path)
    {
        return InvokeVfx("PlayOnCreatureCenter", creature, path);
    }

    /// <summary>
    /// ★ <c>NGame.Instance.DoHitStop(ShakeStrength, ShakeDuration)</c> —— <b>抖屏 + 顿帧</b>。
    ///
    /// <para>这是本项目"重量感"的<b>主力</b>，依据：游戏自己在重型攻击里就是这么调的
    /// （<c>CeremonialBeast.cs:199</c> / <c>MechaKnight.cs:117</c>：
    /// <c>NGame.Instance?.DoHitStop(ShakeStrength.Strong, ShakeDuration.Normal)</c>）。</para>
    ///
    /// <para>两个枚举值按<b>名字</b>从游戏程序集里取，不引用类型：
    /// <c>ShakeStrength { None, VeryWeak, Weak, Medium, Strong, TooMuch }</c> ·
    /// <c>ShakeDuration { None, Short, Normal, Long, Forever }</c>。</para>
    /// </summary>
    private static bool DoHitStop(string strengthName = ShakeStrengthName, string durationName = ShakeDurationName)
    {
        try
        {
            Type? nGame = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");

            if (nGame == null)
            {
                return false;
            }

            object? instance = nGame.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            if (instance == null)
            {
                return false;
            }

            MethodInfo? target = null;

            foreach (MethodInfo m in nGame.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "DoHitStop" && m.GetParameters().Length == 2)
                {
                    target = m;
                    break;
                }
            }

            if (target == null)
            {
                return false;
            }

            ParameterInfo[] ps = target.GetParameters();
            object? strength = Enum.Parse(ps[0].ParameterType, strengthName);
            object? duration = Enum.Parse(ps[1].ParameterType, durationName);

            target.Invoke(instance, new[] { strength, duration });
            return true;
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效] DoHitStop 失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// ★ 全屏曝光脉冲：<c>NGame.Instance.ActivateWorldEnvironment()</c> 拿世界环境，
    /// 然后把 <c>environment:tonemap_exposure</c> 补间到峰值、再回落 1.0。
    ///
    /// <para><b>手法来源</b>：参考实现 RegentFX 的 <c>WorldEnvironmentUtil.TweenExposure</c>
    /// （反编译可见）—— 它给每张卡配了 <c>ExposurePeak</c> / <c>ExposureInDuration</c> /
    /// <c>ExposureOutDuration</c>，出特效时同时推一次曝光。</para>
    ///
    /// <para>这是通用的 Godot 用法（<c>WorldEnvironment</c> + <c>Environment.tonemap_exposure</c>），
    /// 不是谁的创意。本类只用反射拿 <c>NGame</c>，不引用游戏类型。</para>
    /// </summary>
    private static bool TweenExposure()
    {
        try
        {
            Type? nGame = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.NGame");

            if (nGame == null)
            {
                return false;
            }

            object? instance = nGame.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

            if (instance == null)
            {
                return false;
            }

            object? env = nGame.GetMethod("ActivateWorldEnvironment", BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(instance, null);

            if (env is not WorldEnvironment worldEnv)
            {
                return false;
            }

            // 先推到峰值（短促），再拉回 1.0
            Tween tween = worldEnv.CreateTween();
            tween.TweenProperty(worldEnv, "environment:tonemap_exposure", ExposurePeak, ExposureIn)
                 .SetEase(Tween.EaseType.Out)
                 .SetTrans(Tween.TransitionType.Cubic);
            tween.TweenProperty(worldEnv, "environment:tonemap_exposure", 1.0f, ExposureOut)
                 .SetEase(Tween.EaseType.InOut)
                 .SetTrans(Tween.TransitionType.Cubic);

            return true;
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效] 曝光脉冲失败：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 反射调用 <c>VfxCmd</c> 上的指定方法，支持 0 / 1 / 2 个参数。
    /// <para>参数类型按<b>是否能把实参装进去</b>来判断（<c>IsInstanceOfType</c>），
    /// 避免本文件引用游戏程序集。</para>
    /// </summary>
    private static bool InvokeVfx(string methodName, params object?[] args)
    {
        try
        {
            Type? vfxCmd = AccessTools_TypeByName("MegaCrit.Sts2.Core.Commands.VfxCmd");

            if (vfxCmd == null)
            {
                LogOnce("[场上特效] 找不到 VfxCmd（游戏改版？），本补丁静默失效");
                return false;
            }

            foreach (MethodInfo m in vfxCmd.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != methodName)
                {
                    continue;
                }

                ParameterInfo[] ps = m.GetParameters();

                if (ps.Length != args.Length)
                {
                    continue;
                }

                bool ok = true;

                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;

                    if (args[i] == null)
                    {
                        // 允许 null 传给引用类型 / 可空值类型
                        if (pt.IsValueType && Nullable.GetUnderlyingType(pt) == null)
                        {
                            ok = false;
                            break;
                        }

                        continue;
                    }

                    if (pt == typeof(string))
                    {
                        if (args[i] is not string)
                        {
                            ok = false;
                            break;
                        }

                        continue;
                    }

                    if (!pt.IsInstanceOfType(args[i]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (!ok)
                {
                    continue;
                }

                m.Invoke(null, args);
                return true;
            }

            LogOnce($"[场上特效] VfxCmd 上没有匹配的 {methodName}({args.Length} 参) 重载");
            return false;
        }
        catch (Exception e)
        {
            // ★ 反射调用的异常被包了一层，必须把 InnerException 也打出来，
            //   否则只会看到没用的 "TargetInvocationException"（2026-10-12 实测踩到）。
            Exception inner = e is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : e;

            LogAlways($"[场上特效] 调用 {methodName} 失败：{inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

    // ───────────────────────── ② 取敌人 ─────────────────────────

    /// <summary>
    /// 从 <c>ICombatState</c> 取可命中的敌人：<c>GetCreaturesOnSide(CombatSide.Enemy)</c> + <c>IsHittable</c>。
    /// <para>⚠️ 用 <b>状态</b>里的敌人列表，而不是 <c>CardPlay.Target</c> ——
    /// 霰弹枪是"对所有敌人"，<c>Target</c> 在出牌起点可能还没定。</para>
    /// </summary>
    private static List<object> EnemiesOf(object? combatState)
    {
        var list = new List<object>();

        if (combatState == null)
        {
            return list;
        }

        try
        {
            MethodInfo? getSide = null;

            foreach (MethodInfo m in combatState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "GetCreaturesOnSide" && m.GetParameters().Length == 1)
                {
                    getSide = m;
                    break;
                }
            }

            if (getSide == null)
            {
                return list;
            }

            object? side = EnumSide(getSide.GetParameters()[0].ParameterType, "Enemy");
            object? result = getSide.Invoke(combatState, new[] { side });

            if (result is not IEnumerable seq)
            {
                return list;
            }

            foreach (object? c in seq)
            {
                if (c != null && IsHittable(c))
                {
                    list.Add(c);
                }
            }
        }
        catch (Exception e)
        {
            LogOnce($"[场上特效] 枚举敌人失败：{e.GetType().Name}: {e.Message}");
        }

        return list;
    }

    private static object? EnumSide(Type enumType, string name)
    {
        try
        {
            return Enum.Parse(enumType, name);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsHittable(object creature)
    {
        try
        {
            return GetProperty(creature, "IsHittable") is true;
        }
        catch
        {
            return true;   // 读不到就别拦着，宁可多打一个
        }
    }

    // ───────────────────────── ③ 小工具 ─────────────────────────

    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool TestModeIsOn()
    {
        try
        {
            Type? t = AccessTools_TypeByName("MegaCrit.Sts2.Core.TestSupport.TestMode");

            return t?.GetProperty("IsOn")?.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }

    private static Type? AccessTools_TypeByName(string name)
    {
        return Type.GetType(name) ?? AccessTools.TypeByName(name);
    }

    /// <summary>
    /// 诊断：把"造成伤害的钩子被调到 + cardSource 是什么"打出来（同一个来源名只打一次，避免刷屏）。
    /// <para>用来判定"钩子没挂上" vs "cardSource 不是霰弹枪"——这两者修法完全不同。</para>
    /// </summary>
    internal static void NoteDamageHook(string sourceName)
    {
        if (_seenDamageSources.Contains(sourceName))
        {
            return;
        }

        _seenDamageSources.Add(sourceName);
        LogAlways($"[场上特效·伤害钩子] 已触发，cardSource={sourceName}");
    }

    private static readonly HashSet<string> _seenDamageSources = new();

    private static void LogOnce(string message)
    {
        if (_loggedOnce)
        {
            return;
        }

        _loggedOnce = true;
        Log.Info(message);
    }

    /// <summary>
    /// ★ <b>永远打</b>的日志（摘要行用它）。
    /// <para>为什么要分开：<see cref="LogOnce"/> 会在第一条之后静音整个类 ——
    /// 2026-10-12 实测踩到：第一条恰好是错误行，于是"已播：…"的成功摘要被永久吞掉，
    /// 看起来像"什么都没发生"。</para>
    /// </summary>
    private static void LogAlways(string message) => Log.Info(message);
}
