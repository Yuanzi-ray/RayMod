using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「积累」的载体：<b>把本场战斗结束时的层数带进下一场战斗</b>。
///
/// <para><b>用户 2026-10-04 拍板</b>：「积累」是<b>有意的跨战斗层数保留</b>，
/// 取<b>回合结束时的值</b>。基础规则"层数每场战斗归零"里唯一的自愿例外。</para>
///
/// <para><b>为什么必须写在跑团级存档里、不能只挂在这个 Power 上</b>：
/// 原版 <c>Player.AfterCombatEnd()</c> 会清空玩家所有战斗内 Power，
/// 所以"下场战斗开始时"这个时机这张牌<b>已经不在场上了</b> —— 值必须提前落到
/// <see cref="DepthState.CarriedDepth"/>（跑团级），由
/// <c>DepthCmd.EnsurePower</c> 在下场战斗建立容器时读出来使用（并立刻消费掉，只带一场）。
/// 这和"战斗结束发奖励必须挂在遗物上"是同一类时序问题（交接文档坑 37）。</para>
///
/// <para><b>两个记录点</b>：</para>
/// <list type="number">
/// <item><c>AfterSideTurnEnd</c>（自己这一侧）—— 规则②说的"回合结束时的值"，
/// 每个回合都覆盖一次，最后一个回合结束时的值自然成为带走的值。</item>
/// <item><c>AfterCombatEnd</c> —— 兜底。玩家在自己的回合把最后一只怪打死时，
/// <b>回合结束流程根本不会跑</b>（战斗直接结束），不补这一笔的话
/// "我最后那一波层数"就白打了。</item>
/// </list>
///
/// <para>⚠️ 记录动作整体包 <c>try/catch</c>：回合结束钩子在
/// <c>CombatManager</c> 的流程中间被 await，抛异常会把整局卡死（坑 42 的同类风险）。</para>
/// </summary>
[RegisterPower]
public sealed class AccumulatePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(AccumulatePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标只显示一个 1（没有"层数"语义）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>自己这一侧的回合结束时：把当前层数记成"下场要带的层数"。</summary>
    public override Task AfterSideTurnEnd(
        PlayerChoiceContext choiceContext, CombatSide side, IEnumerable<Creature> participants)
    {
        // 照抄原版 RagePower：靠 participants 判断"是不是我这一侧的回合结束"。
        if (participants.Contains(base.Owner))
        {
            Record();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 战斗结束兜底再记一次（原因见类注释：回合中途打完最后一只是收不到回合结束钩子的）。
    /// </summary>
    public override Task AfterCombatEnd(CombatRoom room)
    {
        Record();
        return Task.CompletedTask;
    }

    /// <summary>把当前层数写进跑团级存档的"待接收层数"。</summary>
    private void Record()
    {
        try
        {
            if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player player)
            {
                return;
            }

            int depth = DepthCmd.GetDepth(player);
            DepthRunData.Store.Get(player).SetCarry(depth);

            Log.Info($"[积累] 记下层数 {depth}（下场战斗开始时带过去）");
        }
        catch (Exception e)
        {
            // 记录失败最多是"下场少带一点层数"，绝不能把回合流程搞崩。
            Log.Warn($"[积累] 记录层数失败（本回合跳过）: {e}");
        }
    }
}
