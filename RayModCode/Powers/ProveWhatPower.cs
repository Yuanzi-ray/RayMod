using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有攻击「证明什么？」的载体：<b>下回合开始时，你死亡</b>（用户拍板④："下回合死亡"= 下回合开始时死）。
///
/// <para><b>为什么需要一个 Power</b>：它要活过"这个回合结束"，而
/// <c>AfterCombatEnd</c> 之外的跨回合状态只有 Power 能扛（战斗内的 Power 在战斗结束才被清）。
/// 这张牌在回合 N 打出时施加，Power 的 <c>AfterPlayerTurnStart</c> 最早在<b>回合 N+1</b> 才触发。</para>
///
/// <para><b>⚠️ 记回合号是为了"只扣一次扳机"</b>：极端情况下（例如「证明什么？」被另一张牌
/// 在<b>回合开始流程中间</b>自动打出），这个 Power 可能在<b>当回合</b>就收到
/// <c>AfterPlayerTurnStart</c>，那就会当场暴毙 —— 与"下回合"不符。
/// 所以施加时记下当时的回合号，只有"回合号真的变了"才执行死亡。</para>
///
/// <para><b>⚠️ 整个效果包 <c>try/catch</c></b>：回合开始类钩子抛异常会让
/// <c>CombatManager.StartTurn</c> 永远完不成 → <b>整个游戏卡死</b>（坑 42）。
/// 这里失败最多是"没死成"，绝不能把游戏挂掉。</para>
/// </summary>
[RegisterPower]
public sealed class ProveWhatPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(ProveWhatPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    /// <summary>施加时的回合号（用来判定"是不是已经到下一个回合了"）。</summary>
    private int _appliedTurn = -1;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标只显示一个 1。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        _appliedTurn = (base.Owner?.Player ?? base.Owner?.PetOwner)?.PlayerCombatState?.TurnNumber ?? -1;

        return Task.CompletedTask;
    }

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        try
        {
            int turn = player.PlayerCombatState?.TurnNumber ?? -1;

            if (turn <= _appliedTurn)
            {
                // 还是打出它的那个回合（理论上到不了这里）—— 不扣扳机。
                return;
            }

            Log.Info($"[证明什么？] 下回合已到（第 {turn} 回合）→ 玩家死亡");

            // force: true —— 这是"死亡"不是"伤害"，无视防死/护盾一类。
            await CreatureCmd.Kill(player.Creature, force: true);
        }
        catch (Exception e)
        {
            // 回合开始钩子抛异常 = 游戏卡死（坑 42），所以这里必须吞掉。
            Log.Warn($"[证明什么？] 死亡结算失败（本回合跳过）: {e}");
        }
    }
}
