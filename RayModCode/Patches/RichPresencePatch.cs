using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using Steamworks;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名（同 CardTagIconOverlay.cs）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Patches;

/// <summary>
/// ★★★ 2026-10-11 重写（第四轮）：<b>让 Steam 好友看到「铁装战士 - 密林 进阶10 · 莱伊」</b>。
///
/// <para><b>一、裁决依据（Steam 客户端自己的渲染器，已确证，本节不再论证）</b><br/>
/// Steam 客户端的 <c>webhelper_js.txt</c>（<c>C:\Program Files (x86)\Steam\logs\webhelper_js.txt</c>）
/// 加上客户端自带 JS（<c>steamui\chunk~2dcc5aaf7.js</c> 模块 36934）把富状态模板的求值规则全交代了：
/// <list type="number">
///   <item><c>m(e,t,r,i,n)</c>：<b><c>e</c> 不以 <c>#</c> 开头 ⇒ 直接
///         <c>console.log("Token doesn't start with #:", e, …)</c> 并返回空串 —— 无条件丢弃</b>；
///         以 <c>#</c> 开头才去后台 token 表查，查不到则 <c>"No loc found for appid"</c> + 返回空。</item>
///   <item><c>p(e,t,…)</c>：把模板里的 <c>{…}</c> 逐个当<b>嵌套 token</b> 再查一次；
///         <b>任一子项为空 ⇒ 整串返回空</b>（这一条决定了下面第四条的坑）。</item>
///   <item><c>A(e,t)</c>：把 <c>%name%</c> <b>原样替换成替换值</b> —— <b>不查表</b>。</item>
/// </list>
/// 日志里的三类实测记录：
/// <list type="bullet">
///   <item><c>Token doesn't start with #: 莱伊 - 暗港 进阶10 appid 2868840</c>
///         ⇒ <b>裸字面串（含中文）被完整收到，但不以 <c>#</c> 开头 ⇒ 丢弃</b>；
///         <b>中文本身没有问题</b>，这条日志同时否掉了"中文会被 Steam 吃掉"的猜测。</item>
///   <item><c>No loc found for appid 2868840 #RAY_MOD_CHARACTER_RAY_MOD_CHARACTER_title</c> /
///         <c>#Template Character_title</c> ⇒ mod 角色名拼出来的 token 在后台不存在</item>
///   <item>原版 token（<c>IRONCLAD</c> / <c>OVERGROWTH</c>）<b>全文 0 次失败</b>
///         ⇒ 后台表是配好的、原版能显示。</item>
/// </list>
/// 后台模板（游戏 pck 里的镜像）：<c>IN_RUN</c> = <c>{#%Character%_title} - {#%Act%_title} 进阶%Ascension%</c>。</para>
///
/// <para><b>二、最终方案 = 把要显示的中文塞进 <c>%Ascension%</c> 槽</b>（<b>单一实现，无模式开关</b>）：
/// <code>
/// steam_display = "#IN_RUN"                     // ★ 必须带 #（结论 1）
/// Character     = "IRONCLAD"                    // 原版角色占位 id（结论 2）
/// Act           = State.Act.Id.Entry            // 原版幕 id 原文：OVERGROWTH / UNDERDOCKS / HIVE / GLORY
/// Ascension     = $"{进阶} · {角色显示名}"        // ★ 唯一"原样替换、不查表"的槽（结论 3）
/// </code>
/// <b>为什么这样能显示（一句话机制）</b>：<c>steam_display</c> 以 <c>#</c> 开头 ⇒ 后台取到 <c>IN_RUN</c> 模板；
/// 模板里 <c>%Character%</c>/<c>%Act%</c> 会被拼成 <c>&lt;值&gt;_title</c> <b>再查表</b>（只有原版 id 能解析，故取原版 id），
/// 而 <c>%Ascension%</c> 由 <c>A()</c> <b>原样替换、不查表</b> ⇒ 中文可以放在这一格，
/// 且它不是嵌套 token，<b>为空也不会触发 <c>p()</c> 的"整串判空"</b>。</para>
///
/// <para><b>三、★ 已知局限（模板的既定损失，必须留着这段说明）</b>：角色那一格显示的是
/// <b>原版角色名</b>（如「铁装战士」），不是「莱伊」。原因：那一格在后台是
/// <c>{#%Character%_title}</c> —— 替换值会被拼成一个<b>后台 token 名</b>再去查表，
/// 而"莱伊"这个角色在后台<b>没有 token</b>（日志实锤：
/// <c>No loc found for appid 2868840 #RAY_MOD_CHARACTER_RAY_MOD_CHARACTER_title</c>）。
/// 写 mod 自己的 id ⇒ 那一格为空 ⇒ <c>p()</c>「任一子项为空 ⇒ 整串返回空」⇒ <b>整行消失</b>；
/// 写中文显示名 ⇒ 拼成 <c>{#莱伊_title}</c> ⇒ 同样查不到 ⇒ 同样整行消失。
/// 所以角色格只能用<b>必定解析成功</b>的原版 id 占位，莱伊的名字走 <c>Ascension</c> 尾格。</para>
///
/// <para><b>四、本轮删掉的旧模式与净化代码（都已被证伪，留此以备查）</b>
/// <list type="bullet">
///   <item><b>① <c>LiteralString</c>（<c>steam_display = "莱伊 - 暗港 进阶10"</c>）= 已证伪</b>：
///         正是那条 <c>Token doesn't start with #: 莱伊 - 暗港 进阶10</c> ⇒ 被无条件丢弃。</item>
///   <item><b>② <c>LiteralTemplate</c>（<c>steam_display = "{%Character%} - {%Act%} 进阶%Ascension%"</c>）= 同样已证伪</b>：
///         它<b>不以 <c>#</c> 开头</b> ⇒ 走的是同一个 <c>m()</c> ⇒ 一样被丢弃。
///         跟 ① 的区别（"Steam 会先做变量替换再显示"）纯属臆测，<b>不留</b>。</item>
///   <item><b>③ <c>TokenSubstitution</c>（上一轮）</b>：骨架已经对了（<c>#IN_RUN</c> + 三个替换值），
///         但把 <b>显示名</b>写进了 <c>Character</c>/<c>Act</c> 两个"查表槽"，还叠加了 ASCII 净化 ⇒ 整行变空。
///         本轮<b>修正为</b>：查表槽只喂原版 id，中文只走 <c>Ascension</c>。</item>
///   <item><b>④ ASCII 净化（<c>PurifyAsciiValues</c>/<c>AsciiSafeName</c>/十六进制名回退）全部删除</b>：
///         它<b>无用且有害</b> —— 日志已证明"中文传到 Steam 是好的"（<c>Token doesn't start with #</c> 那条里
///         「莱伊」「暗港」「进阶10」<b>一个不少地被收到了</b>，中文没问题），
///         真正的原因是<b>不以 <c>#</c> 开头</b>；而净化把「莱伊」变成 <c>Template Character</c> /
///         <c>Entry=526179</c> 这种东西，<b>只会更难看</b>，且掩盖真正的病因。
///         <c>PreferLocalizedNames</c> 也随之删除 —— 不再需要"退成 ASCII"这个动作。</item>
/// </list></para>
///
/// <para><b>五、安全性</b>（与前三轮相同，全部保留）：
/// <list type="bullet">
///   <item><b>全程反射找类型</b>（<c>AccessTools.TypeByName</c>），<b>不直接引用 <c>RunManager</c></b> ——
///         游戏改版时本补丁只是<b>静默不生效</b>（<c>HarmonyPrepare</c> 返回 false）。</item>
///   <item><b>只在 Steam 平台上动</b>：判据用游戏自己的同一个门
///         <c>PlatformUtil.PrimaryPlatform == PlatformType.Steam</c>（内部就是 <c>SteamInitializer.Initialized</c>）；
///         否则拿未初始化的原生接口指针调用会 AccessViolation，<c>catch (Exception)</c> 拦不住。
///         <c>steam_api64.dll</c> 里<b>没有</b> <c>SteamAPI_ISteamFriends_GetRichPresence</c> 导出（已 dump 导出表确认），
///         所以「读自己的 steam_display」只能借 <c>GetFriendRichPresence(selfSteamId)</c>，见日志段注释。</item>
///   <item><b>与游戏同款的 TestMode 门</b>：<c>UpdateRichPresence</c> 自己在 <c>TestMode.IsOn</c> 时整段不写，我们同样跳过。</item>
///   <item><b>原版局一概不碰</b>：只有"角色或幕属于本 mod"时才接管（原版那些 token 由 Steam 按【观看者】语言渲染，
///         覆盖是净损失）。<b><c>steam_player_group</c> / <c>steam_player_group_size</c> 一个字都不写</b>
///         （那是"加入游戏"用的，游戏用 <c>PlatformUtil.SetRichPresence("IN_RUN", …)</c> 写，我们只动 <c>steam_display</c>）。</item>
///   <item>异常一律 <c>catch</c>，且<b>只记一次</b>日志（本工程铁律：游戏流程里的异常不能往外扔）。</item>
/// </list></para>
/// </summary>
[HarmonyPatch]
internal static class RichPresencePatch
{
    /// <summary>目标方法名（<c>RunManager</c> 的 private 方法，<c>RunManager.cs:1398</c>）。</summary>
    private const string TargetName = "UpdateRichPresence";

    /// <summary><c>LocalContext.GetMe</c> 那个重载的参数类型全名。</summary>
    private const string PlayerCollectionType = "MegaCrit.Sts2.Core.Runs.IPlayerCollection";

    /// <summary>
    /// ★ 出问题时的总开关（同 <c>CardTagIconOverlay.Enabled</c> 的做法）。
    /// <c>false</c> ⇒ Postfix 直接早退，行为完全回到打补丁之前。
    /// </summary>
    internal const bool Enabled = true;

    private static readonly Logger Log = new("RayMod.RichPresence", LogType.Generic);

    /// <summary>缓存下来的目标方法（<c>HarmonyPrepare</c> 里解析一次）。</summary>
    private static MethodInfo? _target;

    /// <summary><c>RunManager.State</c> —— <b>private 属性</b>（<c>RunManager.cs:264</c>），反射读。</summary>
    private static PropertyInfo? _stateProp;

    /// <summary><c>LocalContext.GetMe(IPlayerCollection)</c> —— 游戏自己取"本地玩家"用的那个重载。</summary>
    private static MethodInfo? _getMe;

    /// <summary><c>PlatformUtil.PrimaryPlatform</c> —— 判"现在是不是 Steam 平台"。</summary>
    private static PropertyInfo? _primaryPlatform;

    /// <summary><c>TestMode.IsOn</c> —— 与游戏同款的测试门。</summary>
    private static PropertyInfo? _testModeIsOn;

    private static bool _loggedOnce;

    [HarmonyPrepare]
    private static bool Prepare()
    {
        try
        {
            Type? runManager = AccessTools.TypeByName("MegaCrit.Sts2.Core.Runs.RunManager");

            // 用 Type.EmptyTypes 把"无参重载"钉死，避免将来出现同名重载时挑错。
            _target = runManager == null ? null : AccessTools.Method(runManager, TargetName, Type.EmptyTypes);
            _stateProp = runManager == null ? null : AccessTools.Property(runManager, "State");

            _getMe = FindGetMe(AccessTools.TypeByName("MegaCrit.Sts2.Core.Context.LocalContext"));

            Type? platformUtil = AccessTools.TypeByName("MegaCrit.Sts2.Core.Platform.PlatformUtil");
            _primaryPlatform = platformUtil == null ? null : AccessTools.Property(platformUtil, "PrimaryPlatform");

            Type? testMode = AccessTools.TypeByName("MegaCrit.Sts2.Core.TestSupport.TestMode");
            _testModeIsOn = testMode == null ? null : AccessTools.Property(testMode, "IsOn");

            string missing =
                (_target == null ? "RunManager.UpdateRichPresence " : string.Empty)
                + (_stateProp == null ? "RunManager.State " : string.Empty)
                + (_getMe == null ? "LocalContext.GetMe(IPlayerCollection) " : string.Empty)
                + (_primaryPlatform == null ? "PlatformUtil.PrimaryPlatform " : string.Empty)
                + (_testModeIsOn == null ? "TestMode.IsOn " : string.Empty);

            if (missing.Length > 0)
            {
                // ★ 返回 false 是 Harmony 的"跳过本类"语义，**不是异常** —— 游戏改版时本补丁静默失效。
                WarnOnce($"[富状态] 结构不符，补丁跳过（缺：{missing.Trim()}）");
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            WarnOnce($"[富状态] Prepare 失败，补丁跳过：{e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    [HarmonyTargetMethod]
    private static MethodBase? TargetMethod() => _target;

    /// <summary>
    /// <c>LocalContext.GetMe(IPlayerCollection)</c> 的反射调用口 —— 拿 <c>RunState</c> 换"本地玩家"。
    /// <para>⚠️ 必须钉住 <c>IPlayerCollection</c> 这个重载：<c>LocalContext</c> 有 6 个同名 <c>GetMe</c>，
    /// 而 <c>RunState</c>（<c>RunState.cs:23</c>）只实现 <c>IRunState, ICardScope, IPlayerCollection</c>
    /// ⇒ 唯一适用的就是它。</para>
    /// </summary>
    internal static object? GetMe(object state) => _getMe?.Invoke(null, new[] { state });

    /// <summary><c>RunManager.State</c>（private 属性）的反射读口；读不到一律 <c>null</c>。</summary>
    internal static object? StateOf(object runManager)
    {
        try
        {
            return _stateProp?.GetValue(runManager);
        }
        catch
        {
            return null;
        }
    }

    /// <summary><c>PlatformUtil.PrimaryPlatform</c>（判 Steam 平台用）。</summary>
    internal static PropertyInfo? PrimaryPlatform => _primaryPlatform;

    /// <summary><c>TestMode.IsOn</c>（与游戏同款的测试门）。</summary>
    internal static PropertyInfo? TestModeIsOn => _testModeIsOn;

    /// <summary>按<b>参数类型全名</b>精确挑出 <c>GetMe(IPlayerCollection)</c>；找不到返回 <c>null</c>。</summary>
    private static MethodInfo? FindGetMe(Type? localContext)
    {
        if (localContext == null)
        {
            return null;
        }

        foreach (MethodInfo m in AccessTools.GetDeclaredMethods(localContext))
        {
            if (!m.IsStatic || m.Name != "GetMe")
            {
                continue;
            }

            ParameterInfo[] parameters = m.GetParameters();

            if (parameters.Length == 1
                && string.Equals(parameters[0].ParameterType.FullName, PlayerCollectionType, StringComparison.Ordinal))
            {
                return m;
            }
        }

        return null;
    }

    private static void WarnOnce(string message)
    {
        if (_loggedOnce)
        {
            return;
        }

        _loggedOnce = true;
        Log.Warn(message);
    }

    /// <summary>
    /// ★ <b>Postfix</b>：游戏自己那四行（<c>#IN_RUN</c> + 三个原始 Id）已经执行完，此刻覆盖才是最终值。
    ///
    /// <para>⚠️ <c>[HarmonyPriority(Priority.Last)]</c>：Harmony 的 Postfix 按优先级<b>从高到低</b>执行，
    /// <c>Last</c>(=0) 让我们排在所有默认优先级(400)的 Postfix <b>之后</b>。</para>
    ///
    /// <para><c>__instance</c> 声明成 <c>object</c>：本文件<b>不引用 <c>RunManager</c> 类型</b>（见类注释第五条）。</para>
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(object __instance) => RichPresenceText.Apply(__instance);
}

/// <summary>
/// 富状态的实际实现（与 <see cref="RichPresencePatch"/> 分开，判据与日志集中在这里）。
/// <para><b>只有一种实现</b>：<c>steam_display=#IN_RUN</c> + 原版 <c>Character</c>/<c>Act</c> 占位 + 中文走 <c>Ascension</c> 槽。
/// 旧的 <c>LiteralString</c>/<c>LiteralTemplate</c>/<c>TokenSubstitution</c> 三种模式与整套 ASCII 净化已删除，
/// 各自的实测结论见 <see cref="RichPresencePatch"/> 类注释第四节的四条。</para>
/// </summary>
internal static class RichPresenceText
{
    // ───────────────────────── ① 四个写入值的唯一来源 ─────────────────────────

    /// <summary>Steam 认的那个"显示模板"键名（游戏 <c>SteamPlatformUtilStrategy.cs:127</c> 用的就是它）。</summary>
    private const string DisplayKey = "steam_display";

    /// <summary>
    /// ★ <b>必须带 <c>#</c></b>：Steam 的 <c>m()</c> 对不以 <c>#</c> 开头的值<b>无条件丢弃</b>
    /// （<c>Token doesn't start with #: 莱伊 - 暗港 进阶10</c>）。带 <c>#</c> ⇒ 去后台取 <c>IN_RUN</c> 模板。
    /// <para>游戏原版写的就是这个值（<c>RunManager.cs:1402</c> → <c>SteamPlatformUtilStrategy.cs:127</c> 的 <c>"#" + token</c>）。</para>
    /// </summary>
    private const string RunToken = "#IN_RUN";

    /// <summary>替换值键名（游戏 <c>RunManager.cs:1403-1405</c> 用的三个）。</summary>
    private const string CharacterKey = "Character";
    private const string ActKey = "Act";
    private const string AscensionKey = "Ascension";

    /// <summary>
    /// ★ <b><c>Character</c> 槽的固定占位 id</b>（<b>选它 + 为什么</b>）：
    /// <list type="bullet">
    ///   <item>那一格在后台是 <c>{#%Character%_title}</c> ⇒ 替换值被拼成<b>后台 token 名</b>再去查表，
    ///         所以<b>只能喂原版 id</b>（<c>IRONCLAD</c> / <c>SILENT</c> / …）；喂 mod id 或中文 ⇒ 查不到 ⇒ 整行消失。</item>
    ///   <item><b>固定用一个常量</b>而不是"按语言挑"：模板渲染用的是 <b>观看者</b>的语言，
    ///         我们这边挑死的 id 在好友那边会自动变成他自己的语言（中文客户端看到「铁装战士」、英文客户端看到「Ironclad」）
    ///         —— 一个常量同时满足所有观看者，按语言挑只会掺进"我的语言"这个无关变量。</item>
    ///   <item>选 <c>IRONCLAD</c>（游戏第一角色）：日志里 <c>IRONCLAD</c> 这条 token <b>全文 0 次失败</b>，
    ///         是四个已知可解析 token 里最稳的一个。</item>
    /// </list>
    /// <para>⚠️ 代价：角色格显示的是原版角色名而不是「莱伊」（模板的既定损失，原因见类注释第三节）。</para>
    /// </summary>
    private const string CharacterSlotId = "IRONCLAD";

    /// <summary>
    /// <c>Act</c> 槽的兜底原版 id —— <b>只在"当前幕本身属于本 mod"时</b>才用到
    /// （那时 <c>State.Act.Id.Entry</c> 在后台必然没有 <c>{#…_title}</c>，照写会让 <c>p()</c> 判定整串为空 ⇒ 整行消失）。
    /// </summary>
    private const string FallbackActId = "OVERGROWTH";

    private static readonly Logger Log = new("RayMod.RichPresence", LogType.Generic);

    private static bool _loggedOnce;

    /// <summary>
    /// ★ 每次 <c>UpdateRichPresence()</c>（开局 + 每次换幕）执行一次。任何一步不如预期就
    /// <b>什么都不写</b>（保留游戏原值，行为回到打补丁之前）。
    /// </summary>
    internal static void Apply(object? runManager)
    {
        if (!RichPresencePatch.Enabled || runManager == null)
        {
            return;
        }

        try
        {
            // ① 与游戏同款的两道门（顺序也照游戏：先 TestMode 再读 State）。
            if (IsTestModeOn() || !IsOnSteam())
            {
                return;
            }

            object? state = RichPresencePatch.StateOf(runManager);

            if (state == null)
            {
                return;
            }

            object? player = RichPresencePatch.GetMe(state);
            object? character = Prop(player, "Character");
            object? act = Prop(state, "Act");

            if (character == null || act == null)
            {
                return;
            }

            // ② 只接管"后台必然没有 token"的那部分 = 本 mod 自己的角色/幕（理由见类注释第五条）。
            //    ⚠️ 莱伊用的幕是原版幕 ⇒ 实际总是"角色属于我们"这条路。
            if (!IsOurs(character) && !IsOurs(act))
            {
                return;
            }

            string? characterId = IdEntry(character);
            string? actId = IdEntry(act);

            if (string.IsNullOrEmpty(characterId) || string.IsNullOrEmpty(actId))
            {
                WarnOnce($"[富状态] 取 Id 失败，保持游戏原值（角色={Desc(character)} 幕={Desc(act)}）");
                return;
            }

            // ③ 三个替换值。
            //    ★ Character：恒定原版 id（见 CharacterSlotId 注释）。
            //    ★ Act：**必须是 State.Act.Id.Entry 原文**（OVERGROWTH / UNDERDOCKS / HIVE / GLORY），
            //      ⚠️ 绝不能写显示名 —— 上一版写「密林」⇒ 后台查 {#密林_title} ⇒ 查不到 ⇒
            //      因为 p() 的"任一子项为空 ⇒ 整串返回空"，整行会**直接消失**。
            //    ★ Ascension：模板里唯一"原样替换、不查表"的槽 ⇒ 中文只有这一个出口。
            string valueCharacter = CharacterSlotId;
            bool actFallback = IsOurs(act);
            string valueAct = actFallback ? FallbackActId : actId!;

            string characterName = LocalizedName("characters", characterId!);
            string actName = LocalizedName("acts", actId!);
            int ascension = Prop(state, "AscensionLevel") is int n ? n : 0;
            string valueAscension = BuildAscensionSlot(ascension, characterName);

            if (actFallback)
            {
                WarnOnce($"[富状态] 幕 {actId} 属于本 mod，后台没有对应的 {{#…_title}}，Act 槽退成原版 {FallbackActId}"
                         + "（照写会让 Steam 的「任一子项为空 ⇒ 整串为空」规则把整行吃掉）");
            }

            // ④ 写 + 记日志（顺序与游戏一致：先 steam_display，再三个替换值；最后再写一次 display，
            //    让后台用"刚更新的替换值"重新求值）。
            var codes = new List<string>(5);
            AddCode(codes, "display", Write(DisplayKey, RunToken));
            AddCode(codes, CharacterKey, Write(CharacterKey, valueCharacter));
            AddCode(codes, ActKey, Write(ActKey, valueAct));
            AddCode(codes, AscensionKey, Write(AscensionKey, valueAscension));
            AddCode(codes, "display#2", Write(DisplayKey, RunToken));

            string readBack = ReadSelfDisplay();

            Log.Info($"[富状态] display={RunToken} Character={valueCharacter} Act={valueAct} Ascension={valueAscension}"
                     + $"（角色名={characterName} 幕={actName} 进阶={ascension}）"
                     + $"｜Id={characterId}/{actId}｜返回={string.Join(",", codes)}｜读回={readBack}");
        }
        catch (Exception e)
        {
            WarnOnce($"[富状态] 覆盖失败（只记一次）：{e.GetType().Name}: {e.Message}");
        }
    }

    // ───────────────────────── ② Ascension 槽 = 唯一的显示出口 ─────────────────────────

    /// <summary>
    /// 拼 <c>Ascension</c> 槽的值：<c>"{进阶} · {角色显示名}"</c>（例：<c>10 · 莱伊</c>）；
    /// 进阶为 <c>0</c> 时只写角色显示名（例：<c>莱伊</c>）。
    /// <para>⚠️ <b>不要写"进阶"二字</b>：模板本身已经带了（<c>… 进阶%Ascension%</c>），
    /// 写成 <c>"进阶10 · 莱伊"</c> 会显示成「进阶进阶10 · 莱伊」。</para>
    /// <para>⚠️ 角色名<b>照旧从本地化表读</b>（<c>LocString.GetIfExists("characters", id + ".title")</c>
    /// ⇒ 中文「莱伊」/ 英文 <c>Ray</c>…），<b>不要再净化成 ASCII</b>（理由见类注释第四节 ④）。
    /// 取不到时用 <c>?</c> 占位（而不是退回原始 Id：<c>RAY_MOD_CHARACTER</c> 这种调试串给好友看更难看，
    /// 而日志那一行已经把 <c>角色名=</c> 打出来了，排障不受影响）。</para>
    /// </summary>
    private static string BuildAscensionSlot(int ascension, string characterName)
    {
        string name = characterName;

        if (name.Length == 0)
        {
            name = "?";
            WarnOnce("[富状态] 角色本地化名取不到（characters/<Id>.title），Ascension 槽用 ? 占位");
        }

        return ascension > 0 ? $"{ascension} · {name}" : name;
    }

    /// <summary>
    /// 写进替换值前的轻量清理（<b>不动非 ASCII、不动 <c>#</c></b>）：
    /// <list type="bullet">
    ///   <item><c>%</c> 剔除 —— 值是被原样插进模板的，值里若又出现 <c>%x%</c> 会被 <c>A()</c> 再当一次槽位处理（未验证路径，没必要冒险）；</item>
    ///   <item>控制字符（含换行）折成空格 —— 状态行是单行文本，换行只会把内容截断。</item>
    /// </list>
    /// <para><b>中文一个字都不删</b>：<c>Token doesn't start with #: 莱伊 - 暗港 进阶10</c> 已证明中文能被完整收到。</para>
    /// </summary>
    private static string SanitizeSlot(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (c == '%')
            {
                continue;
            }

            if (char.IsControl(c))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                {
                    sb.Append(' ');
                }

                continue;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// 从本地化表按 <c>&lt;Id&gt;.title</c> 读<b>当前语言</b>的显示名，再过 <see cref="SanitizeSlot"/>。
    /// <para>取不到 / 空串 ⇒ <c>""</c>。包 <c>try/catch</c>：<c>GetIfExists</c> 在<b>表本身不存在</b>时会抛
    /// <c>LocException</c>。</para>
    /// </summary>
    private static string LocalizedName(string table, string id)
    {
        try
        {
            string? name = LocString.GetIfExists(table, id + ".title")?.GetFormattedText();
            return string.IsNullOrWhiteSpace(name) ? string.Empty : SanitizeSlot(name);
        }
        catch (Exception e)
        {
            WarnOnce($"[富状态] 读本地化失败 table={table} id={id}（只记一次）：{e.GetType().Name}: {e.Message}");
            return string.Empty;
        }
    }

    // ───────────────────────── ③ 日志：读回自己的 steam_display ─────────────────────────

    /// <summary>
    /// 尽力读回<b>我们自己</b>的 <c>steam_display</c>，失败一律返回说明性文本（绝不抛）。
    ///
    /// <para><b>依据（已核对）</b>：<c>Steamworks.NET.dll</c> 里 <c>SteamFriends</c> 只有
    /// <c>SetRichPresence</c> / <c>ClearRichPresence</c> / <c>GetFriendRichPresence</c> /
    /// <c>GetFriendRichPresenceKeyCount</c> / <c>GetFriendRichPresenceKeyByIndex</c> /
    /// <c>RequestFriendRichPresence</c> —— <b>没有"读自己"的 SDK 包装</b>；并且
    /// <c>steam_api64.dll</c> 的导出表里也<b>没有</b> <c>SteamAPI_ISteamFriends_GetRichPresence</c>
    /// （已用字节搜索确认，只有 <c>Set</c> / <c>GetFriend</c> 系列）⇒ 没法自己 P/Invoke。
    /// 所以这里只能<b>拿自己的 SteamId 去问 <c>GetFriendRichPresence</c></b>，
    /// 它可能返回空（本地玩家不在"好友"列表里）——返回空只说明"这条路读不到"，<b>不能推断 Steam 没收到</b>。</para>
    /// </summary>
    private static string ReadSelfDisplay()
    {
        try
        {
            CSteamID me = SteamUser.GetSteamID();

            if (!me.IsValid())
            {
                return "<steamId 无效>";
            }

            string value = SteamFriends.GetFriendRichPresence(me, DisplayKey);
            return string.IsNullOrEmpty(value) ? $"<空｜selfId={me.m_SteamID}>" : $"{value}｜selfId={me.m_SteamID}";
        }
        catch (Exception e)
        {
            return $"<读不到：{e.GetType().Name}>";
        }
    }

    /// <summary>写一条 presence 并返回 <c>ok/false</c> 形态的短码（供日志拼接）。</summary>
    private static string Write(string key, string? value)
    {
        try
        {
            return SteamFriends.SetRichPresence(key, value) ? "ok" : "false";
        }
        catch (Exception e)
        {
            return e.GetType().Name;
        }
    }

    private static void AddCode(List<string> codes, string key, string code) => codes.Add($"{key}={code}");

    // ───────────────────────── ④ 判据工具 ─────────────────────────

    /// <summary>
    /// "这个模型是不是本 mod 自己的" —— 判据与 <c>CardTagIconOverlay.IsRayModCard</c> 一致：
    /// 类型所在程序集 == 本程序集，或命名空间以 <c>RayMod.</c> 开头。
    /// <para>莱伊的 <c>Player.Character</c> 运行时类型就是 <c>RayMod.Characters.RayModCharacter</c>
    /// （同款判据已被 <c>CombatIdleAnimationGuardPatch</c> 在实机验证有效）。</para>
    /// </summary>
    private static bool IsOurs(object? model)
    {
        if (model == null)
        {
            return false;
        }

        try
        {
            Type t = model.GetType();

            if (t.Assembly == typeof(RichPresenceText).Assembly)
            {
                return true;
            }

            string? ns = t.Namespace;
            return ns != null && ns.StartsWith("RayMod.", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>model.Id.Entry</c>（<c>ModelId.Entry</c>，见 <c>ModelId.cs</c>）—— 全是反射，任一步拿不到就 <c>null</c>。</summary>
    private static string? IdEntry(object? model) => Prop(Prop(model, "Id"), "Entry") as string;

    /// <summary>反射读一个属性；读不到（或抛）一律 <c>null</c>。</summary>
    private static object? Prop(object? target, string name)
    {
        if (target == null)
        {
            return null;
        }

        try
        {
            return AccessTools.Property(target.GetType(), name)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string Desc(object? model) => model == null ? "<null>" : model.GetType().Name;

    /// <summary>"现在是不是 Steam 平台" —— 用游戏自己的同一个门（<c>PlatformUtil.PrimaryPlatform</c>，
    /// 内部就是 <c>SteamInitializer.Initialized</c>）。判错的代价是拿未初始化的原生指针去调用 ⇒ 不可捕获的崩溃。</summary>
    private static bool IsOnSteam()
    {
        try
        {
            object? platform = RichPresencePatch.PrimaryPlatform?.GetValue(null);
            return platform != null && string.Equals(platform.ToString(), "Steam", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTestModeOn()
    {
        try
        {
            return RichPresencePatch.TestModeIsOn?.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }

    private static void WarnOnce(string message)
    {
        if (_loggedOnce)
        {
            return;
        }

        _loggedOnce = true;
        Log.Warn(message);
    }
}
