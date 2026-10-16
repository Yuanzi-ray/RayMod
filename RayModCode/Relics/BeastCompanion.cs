using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Beasts;
using RayMod.Characters;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（和 MineRelic 一致）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Relics;

/// <summary>
/// 遗物「伙伴」（2026-10-12 用户指定）：<b>战斗开始时，召唤两只沙地兽</b>。
///
/// <para><b>为什么是 <c>BeforeCombatStart</c> 而不是回合开始钩子</b>：
/// 用户的字面要求就是"战斗开始时"。反编译核实这条路对遗物是通的 ——
/// <c>CombatManager.StartCombatInternal</c>（<c>:586-594</c>）先把所有 creature 加进战斗，
/// 再 <c>await Hook.BeforeCombatStart(runState, combatState)</c>，而
/// <c>RunState.IterateHookListeners(childCombatState)</c> 在战斗内会转投
/// <c>CombatState.IterateHookListeners()</c>（<c>CombatState.cs:429-436</c>）——
/// <b>那里会把玩家的遗物（未熔毁）加进监听列表</b>。
/// 原版同类遗物也是这么写的（<c>Models.Relics\Byrdpip.cs:64</c> 在 <c>BeforeCombatStart</c> 里放宠物）。</para>
///
/// <para>⚠️ <b>与 <c>MineRelic</c> 注释里那句"遗物收不到 BeforeCombatStart"的关系</b>：
/// 那句说的是 <c>RunState.IterateHookListeners</c> <b>自己那份列表</b>在战斗内不含遗物 ——
/// 但它在方法末尾还会 <c>yield</c> 战斗状态自己的监听列表（含遗物），
/// 所以**结论是"收得到"**（原版 <c>Anchor</c>/<c>BeltBuckle</c>/<c>Byrdpip</c> 都靠这条路生效）。
/// 那句旧注释的结论已按本条订正。</para>
///
/// <para><b>★ 顺序上有两个必须先做的事（否则会静默少召唤）</b>：
/// <list type="number">
/// <item><c>DepthCmd.EnsurePower</c> —— <b>层数的每场归零发生在【回合开始】，不在战斗开始</b>
/// （<c>DepthCmd.EnsurePower</c> 是唯一复位点）。所以在 <c>BeforeCombatStart</c> 直接召唤时，
/// <c>DepthCmd.GetDepth</c> 读到的是<b>上一场战斗结束时</b>的层数（很可能 &gt;50）⇒
/// <c>BeastCmd.Summon</c> 会按"超过 50 层不能召唤"把两只<b>全部拦掉</b>，而且不报错。
/// 先 Ensure 一次就把它归零，同时把「积累」的跨战斗层数照常吃进来。</item>
/// <item><c>BeastCmd.EnsurePower</c> —— 沙地兽自己的每场复位（<c>Cap = 2</c>、各项计数归零）也挂在这条路上。</item>
/// </list>
/// 两条都是<b>幂等</b>的（内部判据是"Power 不在位"），所以和初始遗物「下矿」在回合开始的那次调用
/// 不会互相干扰：本遗物先做，那边到回合开始时发现 Power 已存在就什么都不做，
/// 之后的 <c>AddDepth(+4)</c> 照常生效。</para>
///
/// <para><b>与「沙地兽」的关系（设计后果，先说清）</b>：沙地兽的每场上限是
/// <c>DepthConstants.MinionCapInitial = 2</c>，所以开场两只 = <b>正好满员</b>；
/// 之后打出「沙地兽」这类召唤牌会走"<b>满上限随机替换</b>"（把一只换掉、印记原样移交），
/// 而不是"再加一只"。这是上限 2 的必然结果，不是 bug。</para>
///
/// <para>⚠️ <b>层数 ≥30 的减益对它无效</b>（战斗开始时层数是 0）；层数 ≥30 的<b>后果</b>是
/// 之后自己再打召唤牌时少一只 —— 与本遗物无关。</para>
/// </summary>
[RegisterRelic(typeof(RayModRelicPool))]
public sealed class BeastCompanion : ModRelicTemplate
{
    private static readonly Logger Log = new("RayMod.Relics", LogType.Generic);

    /// <summary>战斗开始时召唤的只数（用户 2026-10-12 指定：两只）。</summary>
    private const int SummonCount = 2;

    /// <summary>
    /// 描述里的数字走占位符（铁律 28：写死在文案里的数字是骗人源头）。
    /// 遗物的描述显示路径是 <c>RelicModel.DynamicDescription</c> —— 它会
    /// <c>DynamicVars.AddTo(description)</c>（<c>RelicModel.cs:91-99</c>），
    /// 所以遗物和卡牌一样能用 <c>{变量名}</c>。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new DynamicVar[] { new DynamicVar("Summon", SummonCount) };

    /// <summary>
    /// 稀有度：「罕见」。
    /// <para>每场战斗白拿两只沙地兽（= 两层印记 + 每回合的印记增伤配额），是**构筑级**的强度，
    /// 但不像初始遗物那样定义整个角色。⚠️ 这是**我拍的**，用户没指定，要改就改这一行
    /// （<c>RelicRarity</c>：Common/Uncommon/Rare 三种才会进战斗奖励/商店掷骰）。</para>
    /// </summary>
    public override RelicRarity Rarity => RelicRarity.Uncommon;

    /// <summary>图标：与「下矿」同一套 AssetProfile 写法（三处都指向同一张 256×256）。</summary>
    public override RelicAssetProfile AssetProfile => new(
        IconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        IconOutlinePath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        BigIconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png");

    /// <summary>战斗开始：召唤两只沙地兽（见类注释里的顺序要求）。</summary>
    public override async Task BeforeCombatStart()
    {
        await SummonBeasts("战斗开始");
    }

    /// <summary>
    /// 若在<b>战斗中途</b>拿到这块遗物（事件 / 商店），也立刻补一次 —— 否则本场就白拿了。
    /// <para>判据用 <c>CombatManager.Instance.IsInProgress</c>（原版 <c>Byrdpip</c> 同款）。</para>
    /// </summary>
    public override async Task AfterObtained()
    {
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress)
        {
            return;
        }

        await SummonBeasts("战斗中拿到遗物");
    }

    /// <summary>
    /// 真正干活的入口。全部包 try/catch：<b>战斗开始钩子里抛异常会把整场战斗卡住</b>
    /// （铁律 9 的同款思路），宁可这次不召唤也不能炸。
    /// </summary>
    private async Task SummonBeasts(string trigger)
    {
        var owner = Owner;

        if (owner == null)
        {
            Log.Warn($"[伙伴] {trigger}：Owner 为空，跳过召唤");
            return;
        }

        if (owner.Creature.CombatState == null)
        {
            Log.Info($"[伙伴] {trigger}：不在战斗中，跳过召唤");
            return;
        }

        try
        {
            // ★★ 2026-10-13 把 `Throwing` 换成 `BlockingPlayerChoiceContext`。
            //    `Throwing` 的 `SignalPlayerChoiceBegun` 是**直接抛 NotImplementedException**，
            //    它是"我保证这一路绝不触发玩家选择"的**断言**，不是"没 context 时的兜底"。
            //    而这条链上有真实分支（`BeastCmd.Summon` →「萌宠博主」`OnBeastSummoned` /
            //    `PowerCmd.Apply<SandBeastMarkPower>`）：一旦某条链路要选牌就会抛，
            //    而下面的 catch **只记日志、不回滚** ⇒ 留下"召唤了 N 只、印记只挂了一半"。
            //    `Blocking` 是空实现、只同步阻塞当前执行流 ⇒ 需要选择时会**真的走完**。
            //    ⚠️ 两个调用点（`BeforeCombatStart` / `AfterObtained`）都拿不到
            //    `PlayerChoiceContext` 形参，所以只能在"会抛的"和"不抛的"之间选 —— 选不抛的。
            var context = new BlockingPlayerChoiceContext();

            // ★ 顺序要紧：先把层数归零（否则会按"上一场结束时的层数"判成不能召唤），
            //   再让沙地兽状态每场复位，最后才召唤。
            await DepthCmd.EnsurePower(context, owner, cardSource: null);
            await BeastCmd.EnsurePower(context, owner, cardSource: null);

            int summoned = await BeastCmd.Summon(context, owner, SummonCount);

            Log.Info($"[伙伴] {trigger}：召唤 {summoned}/{SummonCount} 只沙地兽" +
                     $"（depth={DepthCmd.GetDepth(owner)}，场上 {BeastCmd.GetCount(owner)}/{BeastCmd.GetCap(owner)}）");
        }
        catch (Exception e)
        {
            Log.Error($"[伙伴] {trigger}：召唤失败（本场不给，不影响战斗）: {e}");
        }
    }
}
