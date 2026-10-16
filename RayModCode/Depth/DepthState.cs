using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace RayMod.Depth;

/// <summary>
/// 层数的战斗内状态。
///
/// <para><b>⚠️ 2026-10-03 设计变更（用户确认）：层数每场战斗归零。</b></para>
/// 早期版本按"层数跨战斗累积"实现，理由是旧设计稿写着"累积 100 层"。
/// 但用户明确澄清：<b>层数和弹药都是每场战斗归零的</b>。而且实际上层数在单场战斗内
/// 就能冲到 100+（「塌方」每 5 层 +3 是 1.6 倍几何增长，「这是对的！」直接翻倍），
/// 所以"连续 3 回合 &gt;100 层直接死亡"完全不需要跨战斗累积。
///
/// <para>因此本类现在的定位是：<b>单场战斗内的层数容器</b>。
/// <see cref="ResetForCombat"/> 在每场战斗开始时把它清回初始值。</para>
///
/// <para>为什么还留在这里而不是直接塞进 <see cref="DepthPower"/>.Amount：
/// 原版 <c>Amount</c> 被 <c>Math.Max(层数, 1)</c> 夹过（0 会被
/// <c>ShouldRemoveDueToAmount</c> 判掉），拿它当真实层数会让"层数&gt;9"这类
/// 条件判断在 0 层时误判成 1。所以真实值单独放这里，<c>Amount</c> 只做显示。</para>
/// </summary>
public sealed class DepthState
{
    /// <summary>当前层数。战斗开始时归零。</summary>
    public int Depth { get; set; }

    /// <summary>本场战斗累计召唤过的沙地兽数量（战斗开始时重置）。</summary>
    public int MinionsSummonedThisCombat { get; set; }

    /// <summary>本场战斗是否已经触发过"大于 60 层给遗物"。</summary>
    public bool RelicGrantedThisCombat { get; set; }

    /// <summary>
    /// <b>本场战斗是否"曾到过"金币档（30-60）</b>（2026-10-11 新增，修"跳档吃不到收益"）。
    ///
    /// <para><b>为什么需要它</b>：金币原先只在 <c>AfterCombatEnd</c> 按<b>结束时的层数快照</b>
    /// 判定（<c>data.Depth ∈ [30,60]</c>）。于是任何"一步跨过整档"的写入都吃不到金币 ——
    /// 灵异录像带把层数直接设到 61+、「这是对的！」从 15~19 翻到 30~38 之后又继续涨过 60、
    /// 「积累」把 63 层带进新战斗（开局 0→63），全都会在结算时落在档外。</para>
    ///
    /// <para><b>语义</b>：本场战斗中<b>只要某一刻</b>层数落在 [30,60]，或某次<b>升档</b>跨越了
    /// 30 层门槛（<c>old &lt; 30 ≤ new</c>），就置为 true。结算时"结束在档内"或"曾到过"都给金币
    /// —— 两者是<b>或</b>关系，一次战斗只发一次。</para>
    ///
    /// <para><b>复位时机与 <see cref="RelicGrantedThisCombat"/> 同源</b>：
    /// <see cref="ResetForCombat"/>（每场战斗第一次由 <c>DepthCmd.EnsurePower</c> 触发）
    /// + <c>DepthPower.BeforeCombatStart</c> 兜底。</para>
    /// </summary>
    public bool GoldTierReachedThisCombat { get; set; }

    /// <summary>
    /// 本场战斗的遗物档<b>掷中了、但奖励还没发出去</b>（等战斗奖励界面来领）。
    ///
    /// <para>2026-10-04 新增。掷骰在 <c>DepthPower.AfterCombatEnd</c>（那时 Power 还活着），
    /// 发放点在 <c>MineRelic.TryModifyRewards</c>（那时已经切到奖励界面）——
    /// 中间靠这个字段传递。理由见 <c>MineRelic.TryModifyRewards</c> 的注释。</para>
    /// </summary>
    public bool PendingRelicReward { get; set; }

    /// <summary>
    /// ★ 2026-10-09：稀有攻击「源源不断」<b>斩杀了敌人</b>，本场战斗结束要给一个遗物。
    ///
    /// <para>与 <see cref="PendingRelicReward"/>（&gt;60 层的遗物档）<b>刻意分开</b>：
    /// 两条是独立来源，同时命中就该给两个遗物奖励项 —— 合成一个字段会**静默吞掉一个**。
    /// 发放点同样是 <c>MineRelic.TryModifyRewards</c>（那里能拿到战斗奖励列表）。</para>
    /// </summary>
    public bool PendingKillRelicReward { get; set; }

    /// <summary>「积累」没有待接收的层数时用这个值（层数本身可以合法地为 0）。</summary>
    public const int NoCarry = -1;

    /// <summary>
    /// <b>跨战斗保留的层数</b>（稀有能力牌「积累」写，2026-10-04 新增）。
    ///
    /// <para>这是"层数每场战斗归零"的<b>唯一自愿例外</b>：<see cref="ResetForCombat"/>
    /// 会把层数清 0，但 <c>DepthCmd.EnsurePower</c> 在清 0 之前先把这个值读出来，
    /// 有值就用它当本场的起始层数，然后立刻消费掉（只带一场，不滚雪球）。</para>
    ///
    /// <para><see cref="NoCarry"/>（-1）= 没有待接收的值。用 -1 而不是 0 是因为
    /// <b>0 是合法层数</b>：带 0 层进来和"没带"必须区分开（虽然效果上一样，
    /// 但日志与语义上必须分得清）。</para>
    ///
    /// <para><b>⚠️ 本字段是 <see cref="DepthState"/> 里唯一必须落盘的字段</b>
    /// （写入在战斗 N、消费在战斗 N+1，"退出→继续"恰好落在中间）。
    /// 它的落盘由 <see cref="DepthRunData.Store"/> 的
    /// <c>WritePolicy = AlwaysWhenRegistered</c> 保证 —— 别把那句删掉。</para>
    /// </summary>
    public int CarriedDepth { get; set; } = NoCarry;

    /// <summary>
    /// 每场战斗开始时调用：层数归零，并重置那些"每场战斗一次"的状态。
    ///
    /// 调用点：<c>MineRelic.EnsurePowers</c>（在回合开始第一次需要层数/弹药时触发，
    /// 此时 <c>DepthPower</c> 还不存在，是绝佳的重置时机）。
    ///
    /// <para>⚠️ <b>顺序要求</b>：调用方必须<b>先读 <see cref="CarriedDepth"/>、再调本方法</b>
    /// （本方法会把它复位成 <see cref="NoCarry"/>）。这条由
    /// <c>DepthCmd.EnsurePower</c> 保证，改动那里时别把顺序弄反。</para>
    /// </summary>
    public void ResetForCombat()
    {
        Depth = 0;
        MinionsSummonedThisCombat = 0;
        RelicGrantedThisCombat = false;
        GoldTierReachedThisCombat = false;
        PendingRelicReward = false;
        PendingKillRelicReward = false;
        CarriedDepth = NoCarry;
    }

    /// <summary>记下"下场战斗要带的层数"（「积累」用）。</summary>
    public void SetCarry(int depth)
    {
        CarriedDepth = Math.Max(0, depth);
    }

    /// <summary>
    /// 取走待接收的层数（取一次就没了）。没有待接收值（或值非法）时返回 <see cref="NoCarry"/>。
    /// </summary>
    public int TakeCarry()
    {
        int carry = CarriedDepth;
        CarriedDepth = NoCarry;
        return carry < 0 ? NoCarry : carry;
    }
}

/// <summary>
/// 层数状态的存储入口。
/// </summary>
public static class DepthRunData
{
    private static PlayerRunSavedData<DepthState>? _store;

    /// <summary>
    /// 取（并在首次调用时注册）层数状态。
    /// RitsuLib 内部按 modId + key 去重，重复调用是安全的。
    ///
    /// <para>注意：虽然底层仍是跑团级存档容器，但层数的语义已经是
    /// <b>战斗内资源</b>（战斗开始归零），存档容器只是拿它来存一个 per-player 对象。</para>
    ///
    /// <para><b>★ 2026-10-12：显式声明写盘策略，否则
    /// <see cref="DepthState.CarriedDepth"/>（「积累」的跨战斗层数）永远不会进存档。</b></para>
    ///
    /// <para><b>为什么会丢</b>：<c>RunSavedDataWritePolicy</c> 的默认值（=0）是
    /// <c>WhenSet</c>，而 <c>RunSavedDataSlot.ShouldWrite</c> 对 <c>WhenSet</c> 走的是
    /// 「槽位被 API 标脏过吗」（<c>bag.IsDirty(SlotKey)</c>）这一支 ——
    /// 只有调 <c>PlayerRunSavedData.Modify/Set</c> 才会标脏
    /// （<c>GetOrCreate</c> 走的是 <c>bag.Set(..., dirty: false)</c>）。
    /// 而本类的全部读写路径（<c>DepthCmd</c> / <c>DepthPower</c> / <c>AccumulatePower</c> /
    /// <c>MineRelic</c> / <c>Continuous</c>）都是<b>直接改字段</b>，
    /// 全仓<b>一次 <c>Modify/Set</c> 都没调</b> ⇒ 槽位永不落盘。</para>
    ///
    /// <para><b>后果（「积累」专有）</b>：<c>AccumulatePower</c> 在战斗 N 写下
    /// <see cref="DepthState.CarriedDepth"/>，<c>DepthCmd.EnsurePower</c> 在战斗 N+1 的
    /// 第一次回合开始才 <c>TakeCarry()</c> 消费它。这中间只要"退出→继续"，
    /// 存档里没有 <c>depth_state</c>，读回来就是默认值 <c>NoCarry</c>(-1)，
    /// 跨战斗保留的层数<b>静默消失</b>（不报错、日志也不打）。</para>
    ///
    /// <para><b>实证（2026-10-12）</b>：<c>%APPDATA%\SlayTheSpire2\steam\&lt;steamid&gt;</c>
    /// 下 295 个 <c>.save/.run</c> 文件里 <b>0 个</b>含 RitsuLib 的 <c>_ritsulib</c> 段
    /// （唯二命中的是 <c>settings.save</c> 里的 mod 清单条目 <c>"id": "STS2-RitsuLib"</c>，
    /// 不是跑团数据）。反编译已部署 DLL 对照：<c>HoloRunData</c> 是
    /// <c>set_WritePolicy((RunSavedDataWritePolicy)2)</c>，而本类与
    /// <c>AmmoRunData</c> / <c>BeastRunData</c> 都仍是 <c>(RunSavedDataOptions)null</c>。</para>
    ///
    /// <para><b>为什么选 <c>AlwaysWhenRegistered</c></b>（与 <see cref="Holo.HoloRunData"/> 同一范式）：
    /// 它的语义是"只要槽位已注册且能为一局游戏解析，就写入"（<c>ShouldWrite</c> 只判
    /// <c>value != null</c>），<b>不依赖脏标记</b>，与"直接改字段"的写法天然兼容。
    /// 相比之下 <c>WhenNonDefault</c> 每次写盘都要把当前值与默认值各序列化一遍做比较
    /// （<c>IsDefaultValue</c>），成本更高且无必要 —— 本类的默认值判据也没有额外语义。
    /// 枚举值已核对：<c>WhenSet=0 / WhenNonDefault=1 / AlwaysWhenRegistered=2</c>
    /// （<c>_decomp\ritsu_full\STS2RitsuLib.RunData\RunSavedDataWritePolicy.cs</c>）。</para>
    /// </summary>
    public static PlayerRunSavedData<DepthState> Store =>
        _store ??= RitsuLibFramework.GetRunSavedDataStore(Entry.ModId)
            .RegisterPerPlayer<DepthState>(
                "depth_state",
                null,
                new RunSavedDataOptions { WritePolicy = RunSavedDataWritePolicy.AlwaysWhenRegistered });
}
