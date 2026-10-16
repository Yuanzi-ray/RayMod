using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Hud;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（与 DepthPower 一致）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Depth;

/// <summary>
/// 层数的对外 API。所有卡牌都通过这里读写层数。
///
/// <para><b>数据流：</b>真实数据在 <see cref="DepthState"/>（跑团级存档），
/// 战斗中会把当前值同步到 <see cref="DepthPower"/> 用于显示与效果结算。</para>
///
/// <para>所以"战斗外加层数"（事件、遗物）和"战斗中加层数"（卡牌）走的都是同一套入口，
/// 区别只是战斗中会额外刷新 Power 的显示。</para>
/// </summary>
public static class DepthCmd
{
    private static readonly Logger Log = new("RayMod.Depth", LogType.Generic);

    // ===================== 读 =====================

    /// <summary>
    /// 取当前层数。战斗中与战斗外都可用（读的是跑团级存档，不是战斗内的 Power）。
    /// 卡牌的"层数大于 N 才能打出"判断应该用这个。
    /// </summary>
    public static int GetDepth(Player? player)
    {
        if (player == null)
        {
            return 0;
        }

        try
        {
            return DepthRunData.Store.Get(player).Depth;
        }
        catch (Exception e)
        {
            // 跑团还没开始（例如主菜单预览卡牌）时拿不到存档，返回 0 而不是抛异常。
            //
            // ★★ 2026-10-13 联机提醒 + 留痕：**返回 0 是有歧义的** ——
            //   "取不到存档"和"层数真的是 0"在调用方眼里一模一样，而层数 0 会让
            //   `DepthGate.CanPlay`（"层数小于 N 才能打出"那一类门槛）**判反**。
            //   联机下若**只有一端**取不到存档，两端能打出的牌就不同 ⇒ 行为分歧。
            //   所以这里至少**留一条日志**，让实机排查时能分辨"是取不到，还是真的是 0"。
            //   （故意仍然返回 0 而不是抛：主菜单/图鉴预览卡牌时会走这条路，抛会崩界面。）
            if (!_storeUnavailableLogged)
            {
                _storeUnavailableLogged = true;
                Log.Warn($"[DepthCmd] 取层数存档失败（当作 0 —— 注意这与'层数真的是 0'无法区分）: " +
                         $"{e.GetType().Name}: {e.Message}");
            }

            return 0;
        }
    }

    /// <summary>上面那条 Warn 只打一次（避免主菜单/图鉴里刷屏）。</summary>
    private static bool _storeUnavailableLogged;

    /// <summary>
    /// 从 Creature 取层数。
    /// <b>必须用 <c>Player ?? PetOwner</c></b>：玩家自己的 Creature 上
    /// <c>PetOwner</c> 为 null，<c>Player</c> 才是它；宠物则相反。
    /// </summary>
    public static int GetDepth(Creature? creature)
    {
        return (creature?.Player ?? creature?.PetOwner) is Player player ? GetDepth(player) : 0;
    }

    /// <summary>层数是否落在 [min, max] 内（含两端）。</summary>
    public static bool DepthInRange(Player? player, int min, int max)
    {
        int depth = GetDepth(player);
        return depth >= min && depth <= max;
    }

    public static bool IsDepthGreaterThan(Player? player, int threshold) => GetDepth(player) > threshold;

    public static bool IsDepthLessThan(Player? player, int threshold) => GetDepth(player) < threshold;

    /// <summary>
    /// 战斗中改层数（推荐）。
    /// 更新跑团级存档，并同步刷新战斗内 Power 的显示与层级效果。
    /// </summary>
    public static async Task AddDepth(
        PlayerChoiceContext choiceContext, Player player, int delta, CardModel? cardSource = null)
    {
        await AddDepthCore(choiceContext, player, delta);
    }

    /// <summary>直接设定层数（灵异录像带、积累 这类"设为"语义的卡用）。</summary>
    public static async Task SetDepth(
        PlayerChoiceContext choiceContext, Player player, int value, CardModel? cardSource = null)
    {
        var state = DepthRunData.Store.Get(player);
        int oldDepth = state.Depth;
        state.Depth = Math.Max(0, value);

        if (state.Depth == oldDepth)
        {
            return;
        }

        await SyncToPower(player, oldDepth, state.Depth);
    }

    /// <summary>
    /// 同步上下文里改层数（遗物、Power 内部回调）。用 <see cref="TaskHelper.RunSafely"/> 派发，
    /// 而不是丢弃 Task —— 原版文档明确要求这么做，否则异常不进日志，效果会静默失效。
    /// </summary>
    public static void AddDepthDeferred(Player player, int delta)
    {
        TaskHelper.RunSafely(AddDepthCore(null, player, delta));
    }

    /// <summary>清空层数。</summary>
    public static async Task ResetDepth(PlayerChoiceContext choiceContext, Player player)
    {
        await SetDepth(choiceContext, player, 0);
    }

    // ===================== 战斗内同步 =====================

    /// <summary>
    /// 确保玩家身上挂着 <see cref="DepthPower"/>，并把层数同步进去。
    ///
    /// <para><b>同时负责"层数每场战斗归零"</b>（见 <see cref="DepthState.ResetForCombat"/>）：
    /// 这个方法由初始遗物「下矿」在每个回合开始时调用。战斗第一回合时
    /// <see cref="DepthPower"/> 还不存在（原版战斗结束会清空所有 Power），
    /// 所以"Power 不存在"正好等价于"这是本场战斗的第一次"，
    /// 在此处重置层数既准确又不需要额外的战斗开始钩子。</para>
    ///
    /// <para>为什么不挂在遗物的 <c>BeforeCombatStart</c> 上：
    /// <c>RunState.IterateHookListeners(combatState)</c> 在
    /// <c>childCombatState != null</c> 时<b>不会把遗物放进监听列表</b>
    /// （只放卡牌和战斗内模型），所以遗物收不到 <c>BeforeCombatStart</c>。
    /// 注意 <c>DepthPower</c> 自己收到的 <c>BeforeCombatStart</c> 只能处理第 2 场及以后的战斗
    /// （第 1 场时它还没被创建）。</para>
    /// </summary>
    public static async Task EnsurePower(
        PlayerChoiceContext choiceContext, Player player, CardModel? cardSource = null,
        bool announceHud = true, bool clearBus = true)
    {
        var power = player.Creature.GetPower<DepthPower>();

        if (power == null)
        {
            // Power 不在位 = 本场战斗还没开始过 = 层数该归零了。
            //
            // ⚠️ 同时清空事件总线（2026-10-03 批 2B 补）：DepthEvents 的订阅者是**静态**列表，
            //    而「资金」「源矿」「沙地兽的祝福」「沙地兽之怒」这类牌会在打出时注册、
            //    在 Power 被移除时（AfterRemoved）注销。万一某个 Power 没走到注销那一步
            //    （战斗被强制结束、异常等），订阅就会跨战斗残留 —— 上一个存档的 Power
            //    会继续响应新战斗的层数变化/召唤，且**完全不报错**。
            //    这里是"本场战斗第一次"的唯一判定点，也正是唯一安全的清理时机：
            //    此刻还没有任何卡牌注册过（Power 都是玩家打出能力牌之后才出现的）。
            //
            // ★ 2026-10-13：加 `clearBus` 开关。**给"非莱伊玩家"挂系统时必须传 false** ——
            //   那条路会在**每一个玩家**的回合开始被走到，而 `ClearAll()` 是**全局**的：
            //   队友的回合开始会把**别人**刚注册的订阅一起清掉（「宛如沙石」「入神」
            //   「子弹模式弹幕」从此静默失效到战斗结束）。那一刀只该由"自己那次"触发。
            if (clearBus)
            {
                DepthEvents.ClearAll();
            }

            // 「积累」的跨战斗层数：**必须在 ResetForCombat 之前读**（那一步会消费掉它）。
            var state = DepthRunData.Store.Get(player);
            int carry = state.TakeCarry();

            state.ResetForCombat();

            if (carry != DepthState.NoCarry)
            {
                // 有保留值就直接拿它当本场起始层数 —— 这是"层数每场归零"的
                // 唯一自愿例外（用户 2026-10-04 拍板：「积累」是有意的跨战斗层数保留）。
                state.Depth = carry;

                Log.Info($"[DepthCmd] 「积累」带回层数：本场起始层数 = {carry}");
            }

            power = await PowerCmd.Apply<DepthPower>(
                choiceContext, player.Creature, 1m, applier: null, cardSource: cardSource);
        }

        power?.SyncFromRunState(GetDepth(player));

        // 战斗开始时也推一次 HUD 计数器（否则要等第一次层数变动才出现）。
        // ★ 2026-10-13：加 `announceHud` 开关 —— 给"非莱伊玩家"挂系统时传 false：
        //   他没有数据源就不该在战斗一开始冒出一块"层数 0"的牌子；等他真的涨了层数，
        //   正常的 Push 路径会自动把计数器建出来（`ResourceHudCounter.Push` 会自建）。
        if (announceHud && LocalContext.IsMe(player))
        {
            ResourceHud.Depth.Push(GetDepth(player).ToString());

            // ★ 层数 >60 的"四角泛红"（Art/DepthRedVignette）：这里与 HUD 是同一个汇聚点。
            Art.DepthRedVignette.Update(GetDepth(player));
        }
    }

    /// <summary>
    /// 层数变更的核心实现。choiceContext 允许为空 —— 战斗外的派发路径拿不到它，
    /// 而这里的同步流程本来也不需要（改的是跑团级存档 + 刷新已存在的 Power 显示）。
    /// </summary>
    private static async Task AddDepthCore(PlayerChoiceContext? choiceContext, Player player, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        var state = DepthRunData.Store.Get(player);
        int oldDepth = state.Depth;
        state.Depth = Math.Max(0, state.Depth + delta);

        if (state.Depth == oldDepth)
        {
            return;
        }

        await SyncToPower(player, oldDepth, state.Depth);
    }

    private static async Task SyncToPower(Player player, int oldDepth, int newDepth)
    {
        // ★ 能量球旁边的层数计数器（2026-10-07）。这里是层数变化的汇聚点，推一次就够。
        //   多人对局里只推自己的那份。层数**没有上限**，所以只显示数字。
        if (LocalContext.IsMe(player))
        {
            ResourceHud.Depth.Push(newDepth.ToString());

            // ★ 层数 >60 的"四角泛红"（Art/DepthRedVignette）—— 与 HUD 同一个汇聚点，
            //   阈值判断只在那个类里做一处（铁律 50：判据只有一处）。
            Art.DepthRedVignette.Update(newDepth);
        }

        var power = player.Creature.GetPower<DepthPower>();

        if (power == null)
        {
            // 战斗外改层数（例如事件里）：没有 Power 也无所谓，跑团级存档已经更新了。
            // 下次进战斗时 EnsurePower 会把新值带进去。
            await Task.CompletedTask;
            return;
        }

        power.SyncFromRunState(newDepth);
        DepthEvents.RaiseDepthChanged(player, oldDepth, newDepth);
    }
}
