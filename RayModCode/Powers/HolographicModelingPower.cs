using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using RayMod.Holo;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 先古能力牌「全息建模」的载体：只负责<b>"这一场战斗结束时该给玩家一次选遗物的机会"</b>。
///
/// <para><b>Amount = 复制品能活几场战斗</b>（1 = 未升级，2 = 升级后）。</para>
///
/// <para>真正的逻辑全在 <see cref="HoloCmd"/> 里 —— 这里只把 <c>AfterCombatEnd</c> 接上
/// <see cref="HoloCmd.OnCombatEnded"/>。<b>选择界面刻意不在这里弹</b>：
/// 这个钩子被 await 在 <c>CombatManager</c> 战斗收尾流程的中间（<c>SaveRun</c> 之前），
/// 在那里压一个阻塞式覆盖界面属于没人验证过的时机；改到战斗奖励时机由遗物钩子来弹
/// （见 <see cref="HoloCmd"/> 的类注释）。</para>
///
/// <para>⚠️ 本 Power 只活到这场战斗结束（原版在 <c>Player.AfterCombatEnd()</c> 里清空战斗内 Power），
/// 所以"还剩几场"不存在这里，而是记在跑团存档 <see cref="HoloState"/> 里。</para>
/// </summary>
[RegisterPower]
public sealed class HolographicModelingPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(HolographicModelingPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示"复制品能活几场"。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override Task AfterCombatEnd(CombatRoom room)
    {
        var player = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (player == null)
        {
            return Task.CompletedTask;
        }

        try
        {
            HoloCmd.OnCombatEnded(player, (int)base.Amount);
        }
        catch (Exception e)
        {
            // 记账失败最多是"这次不复制"，不能把战斗收尾流程搞崩。
            Log.Error($"[全息建模] AfterCombatEnd 记账失败：{e}");
        }

        return Task.CompletedTask;
    }
}
