using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Ammo;

/// <summary>
/// 「本回合与弹药有关的攻击牌造成了多少伤害」的账本（「你只能走到这了」用）。
///
/// <para><b>为什么需要一个常驻 Power</b>：账本必须在<b>打出那张牌之前</b>就开始记账
/// （它算的是"本回合到目前为止"的总和），所以不能像别的牌那样"打出的瞬间才创建"。
/// 这里挂在 <see cref="AmmoCmd.EnsurePower"/> 上和 <see cref="AmmoPower"/> 一起创建
/// —— 那个分支的判据（"Power 不在位"）本来就是"本场战斗第一次"，同一个地方建、
/// 同一处生命周期，最不容易漏。</para>
///
/// <para><b>为什么不塞进 <see cref="AmmoPower"/></b>：那个类写明了"职责边界 = 只负责显示"
/// （它的 <c>SyncFromData</c> 有一整套"别解析 Player"的教训）。账本是逻辑不是显示，
/// 混进去会把那条边界搞脏。</para>
///
/// <para><b>不显示角标</b>：<see cref="IsVisibleInternal"/> 返回 false —— 它是个纯记账容器，
/// 玩家不需要看到（它照样会收到战斗钩子，隐藏只影响 UI）。</para>
///
/// <para><b>口径</b>（用户 2026-10-03 拍板）：
/// <list type="bullet">
/// <item>算<b>实际造成的伤害</b>（<c>DamageResult.UnblockedDamage</c>，被打掉的血）——
/// 被格挡的部分不算。</item>
/// <item>只算"我方打出去的、攻击牌、且实现了 <see cref="IAmmoCard"/>"的伤害。</item>
/// <item>「你只能走到这了」<b>自己不算</b>（否则它一边领账本一边记账，自我循环）。</item>
/// <item>每回合开始清零。</item>
/// </list></para>
/// </summary>
[RegisterPower]
public sealed class AmmoLedgerPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(AmmoLedgerPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>不显示（纯记账容器）。</summary>
    protected override bool IsVisibleInternal => false;

    private int _damageThisTurn;

    /// <summary>本回合累计的伤害（只读）。</summary>
    public int DamageThisTurn => _damageThisTurn;

    /// <summary>取走本回合的总和并清零（一张牌只该领一次）。</summary>
    public int TakeDamageThisTurn()
    {
        int total = _damageThisTurn;
        _damageThisTurn = 0;
        return total;
    }

    public override async Task AfterDamageGiven(
        PlayerChoiceContext choiceContext,
        Creature? dealer,
        DamageResult result,
        ValueProp props,
        Creature target,
        CardModel? cardSource)
    {
        var owner = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (owner == null)
        {
            return;
        }

        // 只记"我方造成的"。
        if ((dealer?.Player ?? dealer?.PetOwner) != owner)
        {
            return;
        }

        // 只记"与弹药有关的攻击牌"。
        if (cardSource is not IAmmoCard)
        {
            return;
        }

        // 「你只能走到这了」自己不算（用户 2026-10-03 拍板，否则它一打出就把自己的伤害记进去）。
        if (cardSource is RayMod.Cards.OnlyGoThisFar)
        {
            return;
        }

        // 只有攻击牌伤害算（照抄 SandBeastMarkPower 的口径，排除层数自伤那种非攻击伤害）。
        if (!props.IsPoweredAttack())
        {
            return;
        }

        _damageThisTurn += result.UnblockedDamage;

        await Task.CompletedTask;
    }

    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) == player)
        {
            _damageThisTurn = 0;
        }

        await Task.CompletedTask;
    }
}
