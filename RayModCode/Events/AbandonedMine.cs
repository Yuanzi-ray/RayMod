using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（和 MineRelic / DepthPower 一致）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Events;

/// <summary>
/// 共享事件「废弃矿井」（2026-10-12 新建，<b>本工程第一个事件</b>）。
///
/// <para><b>主题</b>：莱伊是雷姆必拓的<b>探井人（pitsinker）</b>，日常就是下井查瓦斯与塌方；
/// 天赋「巡逻伙伴」是放<b>沙地兽</b>下去侦查区域。所以这个事件的两个选项正是她的两套做法：
/// 放沙地兽 / 自己系绳下去 —— 后者有代价（档案里她被塌方埋过、被"从井口吊回地面"）。</para>
///
/// <para><b>注册</b>：<c>[RegisterSharedEvent]</c> = RitsuLib 的自动注册（扫描 attribute），
/// 追加进 <c>ModelDb.AllSharedEvents</c>（RitsuLib 的 <c>AllSharedEventsPatch</c>），
/// 再被 <c>ActModel.GenerateRooms</c> 并进<b>每一幕</b>的事件池
/// （<c>ActModel.cs:334</c>：<c>AllEvents.Concat(ModelDb.AllSharedEvents)</c>）。
/// ⚠️ 事件池是<b>跑团生成时</b>就定下来的 ⇒ <b>改动要新开一局才生效</b>。</para>
///
/// <para><b>Id.Entry</b>（本地化键前缀）= <c>RAY_MOD_EVENT_ABANDONED_MINE</c> ——
/// 由 RitsuLib 的 <c>ModContentRegistry.GetFixedPublicEntry</c> 按
/// <c>{模组id}_{类别}_{类名}</c> 拼出来（与卡/遗物同一套规则）。
/// ★ <b>注意类别是 <c>EVENT</c></b>（不是 <c>EVENTS</c>）：它来自
/// <c>ModelId.SlugifyCategory(GetCategoryType(类型).Name)</c>，而 <c>GetCategoryType</c> 一路走到
/// <c>EventModel</c>、再被 <c>SlugifyCategory</c> 砍掉 <c>_MODEL</c> 后缀。</para>
///
/// <para><b>文案键格式</b>（表 = <c>events</c>，即 <c>localization\{zhs,eng}\events.json</c>）：
/// <code>
/// RAY_MOD_EVENT_ABANDONED_MINE.title                                  ← 事件名
/// RAY_MOD_EVENT_ABANDONED_MINE.pages.INITIAL.description              ← 开场正文
/// RAY_MOD_EVENT_ABANDONED_MINE.pages.INITIAL.options.&lt;名&gt;.title    ← 选项标题
/// RAY_MOD_EVENT_ABANDONED_MINE.pages.INITIAL.options.&lt;名&gt;.description← 选项副文字
/// RAY_MOD_EVENT_ABANDONED_MINE.pages.&lt;结果页&gt;.description           ← 结果页正文
/// RAY_MOD_EVENT_ABANDONED_MINE.loss                                   ← 在事件里死掉的跑团史文字
/// </code>
/// ⚠️ <b>选项的 <c>.title</c> 与 <c>.description</c> 【两条都必须有】</b> ——
/// <c>NEventOptionButton._Ready</c>（<c>:285-288</c>）对两者直接调
/// <c>GetFormattedText()</c>，<b>没有 null 检查</b>，缺一条就是空引用异常。</para>
///
/// <para><b>诊断日志（铁律 56）</b>：构造函数打一条 <c>[事件] 已注册 …</c>
/// （证明 attribute 自动注册真的把本类型建出来了），开场选项生成时再打一条
/// （证明它真的被排进了地图、并且本地化键对得上）。</para>
///
/// <para>⛔ <b>2026-10-15 用户要求：「先禁用我们自己加的事件」</b>（创意工坊发布前）——
/// 见下方 <c>#if</c> 那道开关与 <c>RayMod.csproj</c> 里 <c>IncludeAbandonedMineEvent</c> 的说明。</para>
///
/// <para>★★ <b>禁用踩到的坑（务必先读）</b>：第一版做法是只把 <c>[RegisterSharedEvent]</c> 注释掉，
/// <b>实测无效</b> —— 摘掉 attribute 之后游戏日志里<b>事件照样被注册</b>，只是 Id 从
/// <c>RAY_MOD_EVENT_ABANDONED_MINE</c> 变成了 <c>ABANDONED_MINE</c>
/// （2026-10-15 20:28 实机日志逐条对比得出）⇒ 说明 RitsuLib 对本类型还有<b>另一条
/// 不靠该 attribute 的注册路径</b>（<c>ModEventTemplate</c> 子类的兜底注册）。</para>
///
/// <para>⇒ <b>唯一可靠的禁用方式是让这个类型根本不进程序集</b>：把
/// <c>RayModCode/Events/AbandonedMine.cs</c> 从编译里排除
/// （<c>RayMod.csproj</c> 的 <c>IncludeAbandonedMineEvent</c>，<b>默认 false</b>），
/// 并在这里加同名的 <c>#if</c> 守卫。</para>
///
/// <para><b>怎么恢复</b>：把 csproj 里的 <c>AddAbandonedMineEvent</c> 改成 <c>true</c>
/// （或 <c>dotnet build /p:IncludeAbandonedMineEvent=true</c>）重新部署即可，代码一行没删、
/// 本地化表也留着。<b>恢复后要【新开一局】才会再遇到它</b>（老存档地图里没有它，不是 bug）。</para>
///
/// <para>⚠️ 恢复前请先确认两件事（否则上架会翻车）：
/// ① <c>images/events/abandoned_mine.png</c> 还是<b>程序化占位图</b>，正式立绘没做；
/// ② <b>缺立绘会让整个事件房渲染不出来</b>（见下面 <see cref="PortraitPath"/> 的说明，铁律 61）。</para>
/// </summary>
#if RAYMOD_ABANDONED_MINE_EVENT
[RegisterSharedEvent]
public sealed class AbandonedMine : ModEventTemplate
{
    private static readonly Logger Log = new("RayMod.Events", LogType.Generic);

    /// <summary>选项 A「放沙地兽下去」的报酬金币。</summary>
    private const int ScoutGold = 75;

    /// <summary>选项 B「自己系绳下井」的报酬金币（更高，但要挨伤害）。</summary>
    private const decimal MineGold = 250m;

    /// <summary>选项 B 的代价伤害（无格挡减免、不吃任何增伤/减伤）。</summary>
    private const decimal DescendDamage = 6m;

    /// <summary>
    /// ★★ <b>事件立绘 —— 这不是"锦上添花"，是【必需项】。</b>
    ///
    /// <para><b>2026-10-12 实机踩到</b>：立绘缺失时 <c>NEventLayout.InitializeVisuals()</c> 会调
    /// <c>EventModel.CreateInitialPortrait()</c>，而基类默认路径是
    /// <c>res://images/events/&lt;id 小写&gt;.png</c>（mod 里当然不存在）⇒ 抛
    /// <c>AssetLoadException</c>（"Asset previously failed to load … The game installation may be corrupted"），
    /// 于是 <c>NEventRoom.SetupLayout()</c> <b>整条中断</b>
    /// ⇒ **标题 / 正文 / 选项全都不显示，事件房一片空白**（只有"前进"那种独立节点还画着）。
    /// 症状看起来像"本地化没生效"，其实**一个字都没错，是图少了**。</para>
    ///
    /// <para><b>为什么写在 AssetProfile 里</b>：RitsuLib 的 <c>EventInitialPortraitPatch</c> 会把
    /// <c>CreateInitialPortrait</c> 换成读 <c>CustomInitialPortraitPath</c>，而
    /// <c>EventGetAssetPathsPatch</c> 在"自定义立绘**存在**"时会把**原版那条路径从预加载列表里摘掉**
    /// （<c>:91-96</c>）⇒ 才不会再抛那个异常。所以这个路径**必须指向真实存在、且已被 Godot 导入**的文件
    /// （新增 PNG 后要跑一次 <c>godot --headless --path &lt;项目&gt; --import</c>，见 §S6.6）。</para>
    ///
    /// <para>⚠️ 当前是**程序化占位图**（`_tools\make_event_placeholder.py` 出的"竖井 + 光"），
    /// 正式美术要等主题拍板后另出。尺寸照基础游戏立绘 <b>3440x1616</b>。</para>
    /// </summary>
    private const string PortraitPath = Entry.ResPath + "/images/events/abandoned_mine.png";

    /// <summary>资源覆盖（目前只给立绘）。见 <see cref="PortraitPath"/> 的说明。</summary>
    public override EventAssetProfile AssetProfile => new(InitialPortraitPath: PortraitPath);

    /// <summary>
    /// 卡面/文案里能用的动态数值。
    /// ★ <c>DamageVar</c> / <c>GoldVar</c> 的默认名就是 <c>Damage</c> / <c>Gold</c>
    /// ⇒ 文案里写 <c>{Damage}</c> / <c>{Gold}</c> 即可（键名必须与这里逐字一致，铁律 12）。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars => new DynamicVar[]
    {
        new GoldVar(ScoutGold),
        new DynamicVar("MineGold", MineGold),
        new DamageVar(DescendDamage, ValueProp.Unblockable | ValueProp.Unpowered),
    };

    /// <summary>
    /// ★ <b>"已挂上"确认日志（铁律 56）</b>：RitsuLib 的自动注册是<b>反射扫 attribute</b>，
    /// 写错命名空间 / 类型不可实例化时**编译、部署全绿，但什么都不发生**。
    /// 构造函数在 ModelDb 建规范实例时跑一次 ⇒ 这条日志出现 = 事件真的注册进去了。
    /// 全部包 try/catch：日志绝不能影响模型构建。
    /// </summary>
    public AbandonedMine()
    {
        try
        {
            Log.Info($"[事件] 已注册：{Id.Entry}（{GetType().FullName}，共享事件）");
        }
        catch (Exception e)
        {
            Log.Warn($"[事件] 注册日志打印失败: {e.Message}");
        }
    }

    /// <summary>
    /// 开场页：两个选项。
    /// ⚠️ 选项键一律走 <see cref="ModEventTemplate.InitialOptionKey"/>（= <c>Id.Entry.pages.INITIAL.options.名</c>），
    /// 不要手写字符串 —— 手写容易和本地化表里的前缀对不上，而那种错误<b>只会在实机上显示成空白</b>。
    /// </summary>
    protected override IReadOnlyList<EventOption> GenerateInitialOptions()
    {
        Log.Info($"[事件] 废弃矿井：开场选项生成（Id={Id.Entry}，选项=SCOUT/DESCEND）");

        return new EventOption[]
        {
            new EventOption(this, Scout, InitialOptionKey("SCOUT")),
            // .ThatDoesDamage 只负责让按钮在"这一下会打死你"时闪红，伤害本体仍要自己在处理函数里结算。
            new EventOption(this, Descend, InitialOptionKey("DESCEND")).ThatDoesDamage(DescendDamage),
        };
    }

    /// <summary>选项 A：放沙地兽下去 —— 零代价，报酬少。</summary>
    private async Task Scout()
    {
        Player owner = Owner!;

        await PlayerCmd.GainGold(DynamicVars["Gold"].BaseValue, owner);

        SetEventFinished(PageDescription("SCOUT"));
    }

    /// <summary>
    /// 选项 B：自己系绳下井 —— 挨伤害换更多的金币。
    ///
    /// <para>⚠️ 这一下<b>可能直接打死玩家</b>（<c>ThatDoesDamage</c> 已经让按钮闪红提示）。
    /// 致死时<b>不再发钱、也不再设结果页</b>：跑团已经结束，房间的 ModelId 会被记进跑团史
    /// （<c>RunHistoryUtilities</c> → <c>KilledByEvent</c>），死亡界面走的是事件死亡那一条分支
    /// （<c>NRunHistory</c> 读 <c>&lt;Id&gt;.loss</c> 作为跑团史文字）。</para>
    /// </summary>
    private async Task Descend()
    {
        Player owner = Owner!;

        await CreatureCmd.Damage(
            new ThrowingPlayerChoiceContext(),
            owner.Creature,
            DescendDamage,
            ValueProp.Unblockable | ValueProp.Unpowered,
            null,
            null);

        if (!owner.Creature.IsAlive)
        {
            Log.Info("[事件] 废弃矿井：玩家在井下死亡（不发报酬，交给死亡流程）");
            return;
        }

        await PlayerCmd.GainGold(DynamicVars["MineGold"].BaseValue, owner);

        SetEventFinished(PageDescription("DESCEND"));
    }
}
#endif
