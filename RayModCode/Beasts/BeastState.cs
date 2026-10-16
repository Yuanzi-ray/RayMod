using MegaCrit.Sts2.Core.Logging;
using STS2RitsuLib;
using STS2RitsuLib.RunData;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（和 AmmoState 一样）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Beasts;

/// <summary>
/// 沙地兽的战斗内状态。
///
/// <para><b>这里不存的东西（别加进来）</b>：</para>
/// <list type="bullet">
/// <item>每只沙地兽"标记了谁" —— 存在 <see cref="SandBeast"/> 实例自己的字段里。
/// 每只兽都是 <c>AddMinion</c> 时新建的独立 mutable 克隆，所以实例字段天然是"每只一份"。</item>
/// <item>场上数量 —— 由 <c>PlayerCombatState.Pets</c> 里筛 <see cref="SandBeast"/> 实时得到，
/// 不另存一份计数（存了就会有"两份真相打架"的风险）。</item>
/// </list>
///
/// <para>本类只管<b>全局计数</b>与<b>上限</b>。语义与层数/弹药一致：<b>每场战斗重置</b>
/// （设计稿"战斗结束时重置"；用户 2026-10-03 确认）。</para>
/// </summary>
public sealed class BeastState
{
    /// <summary>本场战斗的沙地兽上限（初始 2、硬顶 6，可被卡牌增减）。</summary>
    public int Cap { get; set; } = Depth.DepthConstants.MinionCapInitial;

    /// <summary>
    /// 本场战斗累计召唤过多少只（<b>含</b>被替换/退场的）。
    /// 沙地兽狂潮 / 之盾 / 背包 / 祝福 / 跳舞的月光 / 哺育 用。
    /// </summary>
    public int SummonedThisCombat { get; set; }

    /// <summary>
    /// 本回合召唤过多少只（<b>成功入场</b>的只数，含满上限替换）。
    /// <b>沙地兽的拯救</b> 的"额外打出次数"就取它；角标 / 发光 / 卡面 <c>{ExtraPlays}</c> 同源。
    ///
    /// <para><b>★ 2026-10-11 口径修正：本字段只对 <see cref="CountedTurn"/> 那个回合有效。</b>
    /// 读它之前必须先过 <see cref="RollTurn"/>（<c>BeastCmd.TryGetState</c> 已经统一代劳），
    /// 否则可能读到上一回合的残值。别再写"靠回合开始钩子清零"的代码 —— 理由见
    /// <see cref="CountedTurn"/>。</para>
    /// </summary>
    public int SummonedThisTurn { get; set; }

    /// <summary>
    /// ★ 2026-10-11 新增：<see cref="SummonedThisTurn"/> / <see cref="BlockedSummonsThisTurn"/> /
    /// <see cref="ReplacedThisTurn"/> / <see cref="QuotaUsedThisTurn"/> 这几个"本回合"字段
    /// <b>记的是哪个回合号</b>（<c>PlayerCombatState.TurnNumber</c>）。
    ///
    /// <para><b>为什么要它（真 bug 的根因）</b>：旧写法是"回合开始的钩子里清零"
    /// （<c>SandBeastQuotaPower.AfterPlayerTurnStart</c>），而那个钩子跑在<b>抽牌之后</b> ——
    /// 反编译实证：<c>CombatManager.SetupPlayerTurn</c> 是
    /// <c>:898 AfterEnergyReset</c> → <c>:900 BeforeHandDraw</c> → <c>:921 抽牌</c> →
    /// <c>:923 Hook.AfterPlayerTurnStart</c>。于是"回合开始流程进行中"发生的召唤
    /// （钩子里的效果、被自动打出的牌 —— 同一类问题本工程已经在
    /// <c>Powers\ProveWhatPower.cs</c> 的注释里踩过一次，那边的解法正是"记回合号"）
    /// 会先被计数、随后被清零吞掉；反过来，在流程中间<b>读</b>到的又是上一回合的旧值。
    /// 改成"记账 + 回合号"之后，读写都只认"回合号是否一致"，与钩子时机彻底解耦。</para>
    /// </summary>
    public int CountedTurn { get; set; } = TurnUnknown;

    /// <summary>回合号未知（战斗外 / 刚 <see cref="ResetForCombat"/>）。</summary>
    internal const int TurnUnknown = int.MinValue;

    /// <summary>
    /// ★ 2026-10-11 新增（<b>纯诊断</b>，不参与任何判定）：本回合有多少只"召唤请求"
    /// 被层数减益（30 层起少 1 只 / 51 层起不能召唤）或上限为 0 拦掉。
    ///
    /// <para>用途：实机日志里一眼区分"这回合没打召唤牌"和"打了但被层数拦掉了" ——
    /// 这两种情况在「沙地兽的拯救」看来都是"额外打出 0 次"，光看旧日志分不出来。</para>
    /// </summary>
    public int BlockedSummonsThisTurn { get; set; }

    /// <summary>本回合是否发生过"满上限随机替换"。饲料 用。</summary>
    public bool ReplacedThisTurn { get; set; }

    /// <summary>
    /// 本回合已经用掉多少点"增伤配额"。
    ///
    /// <para>用户 2026-10-03 拍板：每回合前【上限】张打中有印记敌人的攻击牌吃加成，
    /// 一张牌只扣 1 点，没吃到就不扣。</para>
    /// </summary>
    public int QuotaUsedThisTurn { get; set; }

    /// <summary>「沙地兽与她」是否生效（本场有效，印记每层 10% → 12%）。</summary>
    public bool MarkBonusAbility { get; set; }

    /// <summary>本回合还能吃到印记增伤的牌数（角标显示的就是它）。</summary>
    public int QuotaRemaining => Math.Max(0, Cap - QuotaUsedThisTurn);

    /// <summary>每场战斗开始归零（由 <see cref="BeastCmd.EnsurePower"/> 调）。</summary>
    public void ResetForCombat()
    {
        Cap = Depth.DepthConstants.MinionCapInitial;
        SummonedThisCombat = 0;
        MarkBonusAbility = false;
        ResetForTurn();
    }

    /// <summary>
    /// ★ 2026-10-11：把"本回合"的几个字段滚到 <paramref name="turnNumber"/> 这个回合号。
    ///
    /// <para><b>返回 true = 真的换回合了</b>（该清零的清零了）；返回 false = 本来就记在这个
    /// 回合号上，什么都不做。幂等，可以在任何时机随便调。</para>
    ///
    /// <para>调用点只有一处：<c>BeastCmd.TryGetState</c>（唯一读取入口）。
    /// 于是"写入（<c>BeastCmd.Summon</c>）"与"读取（拯救 / 卡面 / 发光 / 配额）"
    /// 都在同一个回合号上记账 —— 不再依赖 <c>AfterPlayerTurnStart</c> 的时机。</para>
    /// </summary>
    public bool RollTurn(int turnNumber)
    {
        if (CountedTurn == turnNumber)
        {
            return false;
        }

        CountedTurn = turnNumber;
        SummonedThisTurn = 0;
        BlockedSummonsThisTurn = 0;
        ReplacedThisTurn = false;
        QuotaUsedThisTurn = 0;

        return true;
    }

    /// <summary>
    /// 清零并把记账回合号置回"未知"，下一次读写会自动滚到当前回合号。
    ///
    /// <para>现在只有 <see cref="ResetForCombat"/> 用它（每场战斗第一次），
    /// 以及 <see cref="SandBeastQuotaPower"/> 的回合开始钩子（那里现在只是"顺手滚一下 + 刷新角标"，
    /// 正确性已不依赖它 —— 见 <see cref="CountedTurn"/>）。</para>
    /// </summary>
    public void ResetForTurn()
    {
        CountedTurn = TurnUnknown;
        SummonedThisTurn = 0;
        BlockedSummonsThisTurn = 0;
        ReplacedThisTurn = false;
        QuotaUsedThisTurn = 0;
    }
}

/// <summary>
/// 沙地兽状态的存储入口（与 <c>DepthRunData</c> / <c>AmmoRunData</c> 同构）。
/// </summary>
public static class BeastRunData
{
    private static PlayerRunSavedData<BeastState>? _store;

    internal static readonly Logger Log = new("RayMod.Beasts", LogType.Generic);

    public static PlayerRunSavedData<BeastState> Store =>
        _store ??= RitsuLibFramework.GetRunSavedDataStore(Entry.ModId)
            .RegisterPerPlayer<BeastState>("beast_state");
}
