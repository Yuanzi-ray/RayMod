using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 能力牌「探井人」的载体：<b>每回合前 N 张"有层数限制的牌"无视层数条件</b>。
///
/// <para>本 Power 自己<b>不算</b>效果，它只是那个"豁免"的持有者；
/// 判定逻辑在 <see cref="RayMod.Depth.DepthGate"/>，
/// 各张门槛牌通过 <see cref="RayMod.Depth.DepthGatedCard"/> 基类自动接入。</para>
///
/// <para><b>剩余次数 = <c>Amount</c></b>（卡把 <c>Uses</c> 当层数施加），每回合在
/// <c>AfterPlayerTurnStart</c> 复位。所以打出这张牌的当回合就能用掉第一次豁免
/// （卡本身有虚无，本来也是当回合的事）。</para>
///
/// <para>★★ <b>2026-10-16 修的真 bug（玩家实机反馈）</b>：原实现用一个
/// <c>bool _usedThisTurn</c>，而卡把 <c>Uses = 2</c> 当<b>层数</b>施加
/// ⇒ <b>无论层数多少都只豁免 1 张</b>（卡面写"前 2 张"，实际只能无视 1 张）。
/// 现在改成"用一次扣一次配额"。<b>判据：豁免次数必须来自 <c>Amount</c>，不能是 bool。</b></para>
/// </summary>
[RegisterPower]
public sealed class ExploratoryWellDrillerPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(ExploratoryWellDrillerPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示<b>还剩几次豁免</b>。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>本回合已经用掉几次（<c>AfterPlayerTurnStart</c> 复位）。</summary>
    private int _usedThisTurn;

    /// <summary>本回合已用掉的次数。</summary>
    public int UsedThisTurn => _usedThisTurn;

    /// <summary>本回合<b>还有没有</b>豁免可用（= 本回合已用 &lt; 层数）。<c>DepthGate</c> 读它。</summary>
    public bool HasWaiverLeft => _usedThisTurn < Amount;

    /// <summary>用掉一次豁免（<c>DepthGate</c> 在真正打出时调它）。</summary>
    public void MarkUsed() => _usedThisTurn++;

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        // 本 Power 永远挂在玩家自己的生物上，直接比对这个 player 就够（照抄 ForwardPower）。
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) == player)
        {
            _usedThisTurn = 0;
        }

        await Task.CompletedTask;
    }
}
