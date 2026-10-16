using STS2RitsuLib;
using STS2RitsuLib.RunData;

namespace RayMod.Holo;

/// <summary>
/// 「全息建模」的<b>跑团级</b>状态（不是战斗级！）。
///
/// <para><b>为什么必须放在跑团存档里</b>：这张牌的复制品要活到<b>下一场战斗结束</b>，
/// 而战斗内的 Power 在 <c>Player.AfterCombatEnd()</c>（<c>CombatManager.cs:1299</c> →）
/// 就被清空了 —— 把"还剩几场"记在 Power 上，战斗一结束就没了。
/// 所以照 <see cref="Depth.DepthState"/>（「积累」的跨战斗层数）那套，
/// 用 RitsuLib 的 per-player 跑团存档容器存。</para>
///
/// <para><b>复制品的身份问题（本卡最麻烦的地方）</b>：复制品和原件<b>是同一种遗物、同一个 Id</b>，
/// 靠 Id 分不开。所以额外记 <see cref="CopyFloor"/>（<c>RelicCmd.Obtain</c> 会给新遗物写
/// <c>FloorAddedToDeck = 当前楼层</c>），"Id 相同 + 楼层相同"的一对里<b>取列表最后一个</b>
/// 就是复制品（<c>AddRelicInternal</c> 是 <c>_relics.Add</c>，复制品必然排在原件之后）。
/// 唯一的歧义场景是"原件也在同一层拿到"（例如那场战斗的奖励里就有它），
/// 这时"最后一个"依然是复制品，所以口径成立。</para>
/// </summary>
public sealed class HoloState
{
    /// <summary>本场战斗打出了「全息建模」，还欠玩家一次"选遗物"（在战斗奖励时机补上）。</summary>
    public bool PendingChoice { get; set; }

    /// <summary>这次待选的复制品能活几场战斗（1 = 未升级，2 = 升级后）。</summary>
    public int PendingCombats { get; set; } = 1;

    /// <summary>已经复制出来的遗物 Id（<c>ModelId.Entry</c>）。空串 = 当前没有活着的复制品。</summary>
    public string CopyRelicEntry { get; set; } = "";

    /// <summary>复制品的 <c>FloorAddedToDeck</c>（和 <see cref="CopyRelicEntry"/> 一起定位它）。</summary>
    public int CopyFloor { get; set; } = NoFloor;

    /// <summary>复制品还能活几场战斗。</summary>
    public int CombatsLeft { get; set; }

    /// <summary>
    /// 上一次"已经在哪个楼层倒过数"（<see cref="NoFloor"/> = 还没倒过）。
    ///
    /// <para><b>为什么要这个字段</b>：复制品本身若也是「下矿 / 深矿」（最诱人的复制目标），
    /// 它同样带着 <c>BeforeCombatRewardOffered</c> 钩子 —— 同一次战斗奖励会被调两遍
    /// （<c>MineRelic.cs:157 → RunRewardTime → HoloCmd.OnRewardTime</c>）。
    /// 不按楼层去重的话，升级版"活 2 场"会被数成"活 1 场"。</para>
    /// </summary>
    public int LastCountdownFloor { get; set; } = NoFloor;

    /// <summary>没有楼层时的哨兵值（0 层是合法楼层，所以用 -1）。</summary>
    public const int NoFloor = -1;

    /// <summary>
    /// 「记录里」是否还有复制品（<see cref="CopyRelicEntry"/> 非空）。
    ///
    /// <para>⚠️ 这只是<b>记录</b>，不是实物判据：实物是否还挂在玩家遗物栏里要另查
    /// （<c>HoloCmd.ResolveCopy</c>）。<b>所以倒数绝不能拿它当门槛</b> ——
    /// 用户 2026-10-11 报的"全息影像不会删除遗物"就是"记录丢了、遗物还在"，
    /// 而旧的 <c>OnRewardTime</c> 用 <c>if (HasLiveCopy)</c> 把整块倒数+删除一起跳过了。</para>
    /// </summary>
    public bool HasLiveCopy => !string.IsNullOrEmpty(CopyRelicEntry);

    /// <summary>记下"刚复制出来的那一份"。</summary>
    public void SetCopy(string relicEntry, int floor, int combatsLeft)
    {
        CopyRelicEntry = relicEntry;
        CopyFloor = floor;
        CombatsLeft = Math.Max(0, combatsLeft);
        PendingChoice = false;
    }

    /// <summary>忘掉复制品（摘掉之后调用）。</summary>
    public void ClearCopy()
    {
        CopyRelicEntry = "";
        CopyFloor = NoFloor;
        CombatsLeft = 0;
    }
}

/// <summary>「全息建模」状态的存储入口（照 <c>DepthRunData</c> 的写法）。</summary>
public static class HoloRunData
{
    private static PlayerRunSavedData<HoloState>? _store;

    /// <summary>
    /// ★ 2026-10-11：<b>显式声明写盘策略</b>，否则这份状态永远不会进存档。
    ///
    /// <para><b>为什么必须写这一句</b>：<c>RunSavedDataWritePolicy</c> 的默认值是
    /// <c>WhenSet</c>(=0)，而 <c>RunSavedDataSlot.ShouldWrite</c> 对 <c>WhenSet</c> 走的是
    /// 「槽位被 API 标脏过吗」（<c>bag.IsDirty(SlotKey)</c>）这一支 —— 只有调
    /// <c>PlayerRunSavedData.Modify/Set</c> 才会标脏。而 <see cref="HoloCmd"/> 和
    /// <c>DepthCmd</c> 全是<b>直接改字段</b>，全仓没有任何一处 <c>Modify/Set</c> 调用，
    /// 于是槽位永不落盘：存档里没有 <c>holo_state</c>，而<b>复制品遗物本身照存不误</b>
    /// （<c>SerializableRelic.FloorAddedToDeck</c> 有序列化）。
    /// 结果就是"退出再继续"之后：遗物还在身上，<see cref="HoloState"/> 却回到默认值
    /// （<c>CopyRelicEntry = ""</c>）→ <see cref="HoloState.HasLiveCopy"/> 变假 →
    /// 倒数与删除<b>整块被跳过</b> → 复制品永久残留。</para>
    ///
    /// <para>实证（2026-10-11）：<c>profile2\saves</c> 下 129 个 <c>.save/.run</c> 文件里
    /// <b>0 个</b>含 RitsuLib 的 <c>_ritsulib</c> 段 —— RayMod 的跑团存档从来没写进去过。
    /// 对照：RitsuLib 自己的 <c>ModCardPilePersistence</c> 就显式设了
    /// <c>WritePolicy = WhenNonDefault</c> 并每次 <c>SavedData.Set(...)</c>。</para>
    ///
    /// <para><c>AlwaysWhenRegistered</c> = "只要槽位注册了且能解析出这一局就写"，
    /// 与"直接改字段"的写法天然兼容（不依赖脏标记），语义也最直白。</para>
    /// </summary>
    public static PlayerRunSavedData<HoloState> Store =>
        _store ??= RitsuLibFramework.GetRunSavedDataStore(Entry.ModId)
            .RegisterPerPlayer<HoloState>(
                "holo_state",
                null,
                new RunSavedDataOptions { WritePolicy = RunSavedDataWritePolicy.AlwaysWhenRegistered });
}
