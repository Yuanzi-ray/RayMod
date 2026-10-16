using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「探井人」的载体：<b>每回合第一张"有层数限制的牌"无视层数条件</b>。
///
/// <para>本 Power 自己<b>不算</b>效果，它只是那个"豁免"的持有者；
/// 判定逻辑在 <see cref="RayMod.Depth.DepthGate"/>，
/// 各张门槛牌通过 <see cref="RayMod.Depth.DepthGatedCard"/> 基类自动接入。</para>
///
/// <para><c>UsedThisTurn</c> 每回合在 <c>AfterPlayerTurnStart</c> 重置 ——
/// 所以打出这张牌的当回合就能用掉第一次豁免（卡本身有虚无，本来也是当回合的事）。</para>
/// </summary>
[RegisterPower]
public sealed class ExploratoryWellDrillerPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(ExploratoryWellDrillerPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示还有几次豁免（0/1）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    private bool _usedThisTurn;

    /// <summary>本回合的豁免还在不在（<c>DepthGate</c> 读它）。</summary>
    public bool UsedThisTurn => _usedThisTurn;

    /// <summary>本回合的豁免被用掉了（<c>DepthGate</c> 在真正打出时调它）。</summary>
    public void MarkUsed() => _usedThisTurn = true;

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // 本 Power 永远挂在玩家自己的生物上，直接比对这个 player 就够（照抄 ForwardPower）。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) == player)
        {
            _usedThisTurn = false;
        }

        await Task.CompletedTask;
    }
}
