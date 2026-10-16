using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Holo;

/// <summary>
/// 「全息建模」的全部逻辑（唯一入口）。
///
/// <para><b>生命周期</b>（设计稿："战斗结束时选择一个已有的遗物复制，
/// 该遗物在下一场战斗结束时消失；升级后改为下两场"）：</para>
/// <list type="number">
/// <item><b>打出那张牌</b> → <c>HolographicModelingPower</c> 挂上，Amount = 复制品能活几场（1 / 2）。</item>
/// <item><b>这场战斗结束</b> → <c>HolographicModelingPower.AfterCombatEnd</c> 调
/// <see cref="OnCombatEnded"/> 记下"欠一次选择"（⚠️ <b>这时只记账、不弹界面</b>）。</item>
/// <item><b>战斗奖励时机</b> → <c>MineRelic.BeforeCombatRewardOffered</c>（原版遗物
/// <c>LastingCandy</c> 用的就是这个钩子，且它在奖励面板弹出<b>之前</b>被 await）
/// 调 <see cref="OnRewardTime"/>：先给旧复制品倒计时（到 0 就摘掉），
/// 再补上这次欠的"选遗物 → 复制"。</item>
/// </list>
///
/// <para><b>为什么"弹选择界面"不放战斗结束那一刻</b>：那是 <c>CombatManager</c> 战斗收尾流程的中间
/// （<c>Hook.AfterCombatEnd</c> 被 await 在 <c>SaveRun</c> 之前），在那儿压一个阻塞式覆盖界面
/// 属于没人验证过的时机；放到奖励时机则<b>前面</b>有原版先例（<c>LastingCandy</c>），
/// 而且仍然满足"战斗结束时选择"（玩家是在这一场战斗的结算里做的选择）。
/// 代价：不给奖励的战斗（<c>Encounter.ShouldGiveRewards == false</c>）不会触发这个钩子，
/// 于是那次选择会被推迟到下一场给奖励的战斗 —— 日志里看得出来。</para>
///
/// <para><b>用户 2026-10-04 拍板</b>：复制品与原件<b>完全叠加生效</b>，
/// 而且<b>不禁任何遗物</b>（含初始遗物「下矿」—— 复制它等于每回合层数 +6 / 弹药上限 +6）。
/// 所以候选列表就是玩家的全部遗物，不做筛选。
/// 另：同一时间<b>只维持一份复制品</b>（再打一次会先把旧的那份摘掉）。</para>
///
/// <para><b>2026-10-11 修「全息影像不会删除遗物」</b>（三条一起改，缺一条都还会复发）：</para>
/// <list type="number">
/// <item><b>倒数与"记忆"解耦</b>：<c>OnRewardTime</c> 原来写的是 <c>if (state.HasLiveCopy) { 倒数… }</c>，
/// 而 <c>HasLiveCopy</c> 只是<b>记录</b>（<c>CopyRelicEntry</c> 非空）。记录一丢，整块倒数+删除被跳过 →
/// 遗物永不删除。现在改成"只要 <c>CombatsLeft &gt; 0</c> 就倒数"。</item>
/// <item><b>那份记录以前根本存不下来</b>：<c>HoloRunData</c> 用了 RitsuLib 的默认写盘策略
/// <c>WhenSet</c>（要求显式 <c>Modify/Set</c> 标脏），而这里全是直接改字段 → 存档里从来没有
/// <c>holo_state</c>，可<b>复制品遗物本身却存进了存档</b> → "退出再继续"之后遗物还在、记录没了。
/// 修法与证据见 <see cref="HoloRunData.Store"/> 的注释。</item>
/// <item><b>失败不再弃疗</b>：<c>RemoveCopy</c> 原来<b>先</b> <c>ClearCopy()</c> 再找实物，
/// 找不到/摘除抛异常时记录已经清掉 → 回合开始的兜底 <c>SweepExpired</c> 再也救不回来。
/// 现在只有"确实摘掉了/身上确实没有这份复制品了"才清记录。</item>
/// </list>
/// <para>另：<c>LastCountdownFloor</c> 去重，修掉"复制品本身也是「下矿/深矿」时同一次奖励倒两次数"
/// （那会让升级版"活 2 场"变成"活 1 场"）。</para>
/// </summary>
public static class HoloCmd
{
    private static readonly Logger Log = new("RayMod.Holo", LogType.Generic);

    /// <summary>取状态（拿不到就算空操作，绝不抛出去）。</summary>
    private static HoloState? TryGetState(Player player)
    {
        try
        {
            return HoloRunData.Store.Get(player);
        }
        catch (Exception e)
        {
            Log.Warn($"[全息建模] 取跑团状态失败：{e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    // ===================== ① 战斗结束（Power 调用） =====================

    /// <summary>
    /// 战斗结束时记账：欠玩家一次"选遗物"。
    /// <b>不弹界面</b>（见类注释"为什么"）。
    /// </summary>
    public static void OnCombatEnded(Player player, int combats)
    {
        var state = TryGetState(player);

        if (state == null)
        {
            return;
        }

        state.PendingChoice = true;
        state.PendingCombats = Math.Max(1, combats);

        Log.Info($"[全息建模] 本场战斗结束 → 记下待选（复制品将活 {state.PendingCombats} 场战斗）");
    }

    // ===================== ② 奖励时机（遗物钩子调用） =====================

    /// <summary>
    /// 战斗奖励时机：先倒计时旧的复制品（到 0 就摘掉），再把欠的那次选择补上。
    ///
    /// <para>★ 2026-10-11：<b>倒数只看 <c>CombatsLeft</c>，不再看 <c>HasLiveCopy</c></b>。
    /// 原来整块是 <c>if (state.HasLiveCopy) { 倒数; 删除; }</c> —— 那是把"倒数"绑在
    /// 一份<b>记录</b>上，记录一丢（旧的写盘策略导致跨存档必丢）就连倒数都不做，
    /// 遗物于是永远留着；而删除又必须在 <c>CombatsLeft</c> 归零时才发生，两个前提互相锁死。</para>
    /// </summary>
    public static async Task OnRewardTime(Player player)
    {
        var state = TryGetState(player);

        if (state == null)
        {
            return;
        }

        try
        {
            int floor = CurrentFloor(player);

            if (state.LastCountdownFloor == floor)
            {
                // 同一次奖励时机只倒一次：复制品本身若也是「下矿 / 深矿」，它同样挂着
                // BeforeCombatRewardOffered（MineRelic.cs:157 → RunRewardTime → 这里），
                // 于是同一次战斗奖励会被调用两遍。倒两遍会让"活 2 场"变成"活 1 场"。
                Log.Info($"[全息建模] 奖励时机重复进入（楼层 {floor}）：本次不重复倒数，还剩 {state.CombatsLeft} 场");
            }
            else
            {
                state.LastCountdownFloor = floor;

                // ★ 倒数与"记忆"解耦：只要还记着"还剩几场"就倒数。
                if (state.CombatsLeft > 0)
                {
                    state.CombatsLeft--;

                    Log.Info($"[全息建模] 倒计时（楼层 {floor}）：复制品 {state.CopyRelicEntry} " +
                             $"还剩 {state.CombatsLeft} 场战斗");
                }

                if (state.CombatsLeft <= 0)
                {
                    await RemoveCopy(player, state, "奖励时机到期");
                }
            }

            // 2) 这次欠的选择。
            if (state.PendingChoice)
            {
                await ChooseAndCopy(player, state, Math.Max(1, state.PendingCombats));
            }
        }
        catch (Exception e)
        {
            // 结算流程里的异常不能继续往外扔（坑 42 的同类风险）。
            Log.Error($"[全息建模] 奖励时机处理失败：{e}");
        }
    }

    // ===================== ③ 兜底（回合开始调用） =====================

    /// <summary>
    /// 兜底：寿命已尽但还挂在身上的复制品（正常路径摘不掉时的保险），补摘一次。
    /// <b>只摘不倒数</b>，所以不会和 <see cref="OnRewardTime"/> 重复计数。
    ///
    /// <para>★ 2026-10-11：判据不再依赖"记录一定对" —— 只要 <c>CombatsLeft</c> 到 0、
    /// 而且能找到实物，就摘；摘除失败会保留记录，下一回合开始还能再试
    /// （旧版在 <see cref="RemoveCopy"/> 里先清记录，所以兜底从来救不回来）。</para>
    /// </summary>
    public static async Task SweepExpired(Player player)
    {
        var state = TryGetState(player);

        if (state == null || state.CombatsLeft > 0 || !state.HasLiveCopy)
        {
            return;
        }

        Log.Warn($"[全息建模] 兜底：复制品 {state.CopyRelicEntry} 寿命已尽（剩 {state.CombatsLeft} 场）" +
                 $"却仍挂在身上，这里补摘");

        try
        {
            await RemoveCopy(player, state, "回合开始兜底");
        }
        catch (Exception e)
        {
            Log.Error($"[全息建模] 兜底摘除失败：{e}");
        }
    }

    // ===================== 内部 =====================

    /// <summary>弹选择界面 → 复制 → 记账。</summary>
    private static async Task ChooseAndCopy(Player player, HoloState state, int combats)
    {
        state.PendingChoice = false;

        // 用户拍板"干脆不禁"：候选 = 玩家的全部遗物。
        var candidates = player.Relics.ToList();

        if (candidates.Count == 0)
        {
            Log.Warn("[全息建模] 玩家没有任何遗物，跳过（不给复制品）");
            return;
        }

        var chosen = await RelicSelectCmd.FromChooseARelicScreen(player, candidates);

        if (chosen == null)
        {
            // 关掉界面 = 放弃这次（不复制、也不欠着）。
            Log.Info("[全息建模] 玩家没有选择遗物，本次不复制");
            return;
        }

        // 同一时间只维持一份复制品：旧的先摘掉。
        if (state.HasLiveCopy)
        {
            await RemoveCopy(player, state, "换成新的复制品");
        }

        // ⚠️ 复制一份**可变**克隆：原件是已拥有（mutable）实例，
        //    RelicModel.ToMutable() 里有 AssertCanonical()，对已拥有的实例会直接抛异常；
        //    MutableClone() 才是"不管原来是什么状态都给一份新的可变克隆"。
        if (chosen.MutableClone() is not RelicModel copy)
        {
            Log.Error($"[全息建模] 克隆 {chosen.Id.Entry} 失败，跳过");
            return;
        }

        await RelicCmd.Obtain(copy, player);

        state.SetCopy(chosen.Id.Entry, copy.FloorAddedToDeck, combats);

        Log.Info($"[全息建模] 复制 {chosen.Id.Entry}（楼层 {copy.FloorAddedToDeck}）：" +
                 $"活 {combats} 场战斗；玩家现在有 {player.Relics.Count} 个遗物");
    }

    /// <summary>当前楼层。复制品的"获得楼层"就是拿这个值写的（<c>RelicCmd.Obtain</c>）。</summary>
    private static int CurrentFloor(Player player) => player.RunState.TotalFloor;

    /// <summary>
    /// 在玩家的遗物栏里找出<b>实物</b>的那份复制品。
    ///
    /// <para>① 首选判据：<c>Id 相同 + 获得楼层相同</c>（复制品是 <c>RelicCmd.Obtain</c> 加进去的，
    /// 那一刻的楼层被 <see cref="HoloState.CopyFloor"/> 记了下来；复制品在列表最后，
    /// 原件就算同层也是它前面那份，所以取 <c>LastOrDefault</c>）。</para>
    ///
    /// <para>② 兜底判据（<b>仅当</b>同 Id 的遗物有 <b>≥2 份</b>、且这种遗物<b>不可能合法重复</b>时才用）：
    /// 取同 Id 的最后一份。为什么敢这么退化：原版 <c>RelicModel.IsStackable</c> 默认 <c>false</c>，
    /// 而 <c>RelicCmd.Obtain</c> 对不可叠加遗物会把它从 grab bag 里移除 —— 所以同 Id 有两份时，
    /// 后加进去的那份只能是复制品。可叠加遗物（原版只有 Circlet / DeprecatedRelic）本来就允许
    /// 合法重复，那种情况<b>宁可不摘也不猜</b>（猜错就是删掉玩家的真遗物）。</para>
    ///
    /// <para>⚠️ 找不到就返回 null（<b>绝不</b>在只有一份同 Id 遗物时"猜"它是复制品 —— 那会删掉真原件）。</para>
    /// </summary>
    private static RelicModel? ResolveCopy(Player player, HoloState state, out string how)
    {
        string entry = state.CopyRelicEntry;
        int floor = state.CopyFloor;

        how = $"Id+楼层({floor})";

        var copy = player.Relics.LastOrDefault(r => r.Id.Entry == entry && r.FloorAddedToDeck == floor);

        if (copy != null)
        {
            return copy;
        }

        var sameId = player.Relics.Where(r => r.Id.Entry == entry).ToList();

        if (sameId.Count >= 2 && !sameId[^1].IsStackable)
        {
            how = $"同 Id 最后一份（共 {sameId.Count} 份，记录楼层 {floor} 对不上）";
            return sameId[^1];
        }

        return null;
    }

    /// <summary>
    /// 摘掉记录里的那份复制品（到期、以及"要换成新的复制品"时都用它）。
    ///
    /// <para><b>判据日志三态</b>（用户 2026-10-11 要求：这类 bug 最容易"没报错但没生效"）：</para>
    /// <list type="bullet">
    /// <item><b>摘成功</b> → Info「复制品 X 已消失（原因；定位=…）」；</item>
    /// <item><b>找不到实物</b> → Warn「该摘了但遗物栏里找不到 X（附楼层/同 Id 份数/遗物总数）：只清记录」；</item>
    /// <item><b>找到但摘除抛异常</b> → Error「摘除失败，记录保留待重试」。</item>
    /// </list>
    ///
    /// <para>★ 关键：<c>ClearCopy()</c> <b>只在处理完之后</b>才调 —— 旧版是先清记录再找实物，
    /// 于是"找不到 / 抛异常"都会把记录一起丢掉，连 <see cref="SweepExpired"/> 都再没机会补摘。</para>
    /// </summary>
    /// <returns>true = 这一次确实把复制品摘掉了。</returns>
    private static async Task<bool> RemoveCopy(Player player, HoloState state, string reason)
    {
        if (!state.HasLiveCopy)
        {
            // 记录本来就空 = 当前没有复制品，正常情况，安静退出。
            return false;
        }

        string entry = state.CopyRelicEntry;
        int floor = state.CopyFloor;

        var copy = ResolveCopy(player, state, out string how);

        if (copy == null)
        {
            // 例如玩家在那之前把那个遗物弄没了（原版有极少见的 Replace/Melt 路径）。
            Log.Warn($"[全息建模] 该摘复制品了（{reason}），但在遗物栏里找不到 {entry}（记录楼层 {floor}，" +
                     $"同 Id 共 {player.Relics.Count(r => r.Id.Entry == entry)} 份，玩家共 {player.Relics.Count} 个遗物）：" +
                     $"只清记录，不做别的");
            state.ClearCopy();
            return false;
        }

        try
        {
            await RelicCmd.Remove(copy);
        }
        catch (Exception e)
        {
            // 异常不能往外扔（同 <see cref="OnRewardTime"/> 的口径），但也绝不静默：
            // 记录**保留**，回合开始的兜底还会再试一次。
            Log.Error($"[全息建模] 摘除复制品 {entry}（记录楼层 {floor}，定位={how}）失败，" +
                      $"记录保留待重试：{e}");
            return false;
        }

        state.ClearCopy();

        Log.Info($"[全息建模] 复制品 {entry} 已消失（{reason}；定位={how}）；" +
                 $"玩家现在有 {player.Relics.Count} 个遗物");

        return true;
    }
}
