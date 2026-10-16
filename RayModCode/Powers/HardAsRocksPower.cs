using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Ammo;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「宛如沙石」的载体：<b>buff 一有变动就获得 Amount 点格挡</b>。
///
/// <para><b>口径（用户 2026-10-04 拍板）</b>：这里的"buff"按<b>玩家看到的东西</b>算 ——
/// 本角色的 <c>层数</c> / <c>弹药</c> / <c>沙地兽</c> 在界面上本来就是 Buff 图标，
/// 所以它们的变动也必须触发。四条来源：</para>
/// <list type="number">
/// <item><b>层数变动</b> —— 挂 <see cref="DepthEvents.Register"/>（已有的层数总线）</item>
/// <item><b>召唤沙地兽</b> —— 挂 <see cref="DepthEvents.RegisterSummoned"/>（已有的召唤总线）</item>
/// <item><b>弹药变动</b> —— 挂 <see cref="AmmoEvents.Register"/>（2026-10-04 为此新建的总线）</item>
/// <item><b>真正的 Power 数值变动</b> —— 原版 <c>AfterPowerAmountChanged</c> 钩子</item>
/// </list>
///
/// <para><b>为什么前三条必须走自己的总线</b>：层数/弹药/沙地兽的"真实值"都在跑团级存档里，
/// 战斗内的那个 Power 只是显示镜像，而且<b>刻意走 <c>SetAmount</c></b>
/// （弹药为 0 时必须留在场上，而 <c>PowerCmd.ModifyAmount</c> 会"数值归零就移除 Power"）。
/// 代价就是原版钩子收不到它们 —— 所以这些 Power 一个都不改，改用事件总线旁路。</para>
///
/// <para><b>计数口径</b>：<b>一次变化事件 = 1 点格挡</b>，不是"每变动 1 点 = 1 点格挡"。
/// 所以一次 +5 层、一次 +3 弹药、一次召唤 3 只，各只给 1 点。要改成按数量给就把
/// <c>Grant()</c> 里的 <c>Amount</c> 乘上变化量（弹药用 <c>|new - old|</c>、召唤用 <c>count</c>）。</para>
///
/// <para><b>口径</b>：只算<b>己方</b>生物身上的、类型为 <see cref="PowerType.Buff"/> 的 Power ——
/// 敌人身上的、以及所有 Debuff 一律不计（敌人的「沙地兽印记」是 Debuff，不会白送格挡）。</para>
///
/// <para>⚠️ <b>自己不算</b>（<c>power == this</c> 直接返回）：否则这张牌被施加时
/// 就会因为"自己的数值变了"白给一次格挡。</para>
/// </summary>
[RegisterPower]
public sealed class HardAsRocksPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(HardAsRocksPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每次给多少格挡。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    private bool _subscribed;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        if (!_subscribed)
        {
            DepthEvents.Register(OnDepthChanged);
            DepthEvents.RegisterSummoned(OnSummoned);
            AmmoEvents.Register(OnAmmoChanged);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 一定要注销：三条总线都是<b>静态</b>列表，不注销的话这张牌退场后效果还在，
    /// 而且不报错。
    /// </summary>
    public override Task AfterRemoved(Creature oldOwner)
    {
        DepthEvents.Unregister(OnDepthChanged);
        DepthEvents.UnregisterSummoned(OnSummoned);
        AmmoEvents.Unregister(OnAmmoChanged);
        _subscribed = false;

        return Task.CompletedTask;
    }

    // ===================== 三套资源的总线 =====================

    private void OnDepthChanged(Player player, int oldDepth, int newDepth)
    {
        if (IsMine(player))
        {
            Grant();
        }
    }

    private void OnSummoned(Player player, int count, CardModel? cardSource)
    {
        if (count > 0 && IsMine(player))
        {
            Grant();
        }
    }

    private void OnAmmoChanged(Player player, int oldValue, int newValue)
    {
        if (IsMine(player))
        {
            Grant();
        }
    }

    // ===================== 原版钩子：真正的 buff 数值变动 =====================

    public override async Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext,
        PowerModel power,
        decimal amount,
        Creature? applier,
        CardModel? cardSource)
    {
        if (power == this)
        {
            return;
        }

        if (base.Owner is not { } self)
        {
            return;
        }

        var owner = self.Player ?? self.PetOwner;

        if (owner == null)
        {
            return;
        }

        // 只算己方生物身上的 buff。
        bool mine = power.Owner == self || power.Owner?.PetOwner == owner;

        if (!mine || power.Type != PowerType.Buff)
        {
            return;
        }

        await GainBlock();
    }

    // ===================== 内部 =====================

    /// <summary>总线处理器是同步的，给格挡是异步的 —— 按总线约定自己派发。</summary>
    private void Grant() => _ = TaskHelper.RunSafely(GainBlock());

    private async Task GainBlock()
    {
        if (base.Owner is not { } self)
        {
            return;
        }

        await CreatureCmd.GainBlock(self, base.Amount, ValueProp.Move, null);
    }

    /// <summary>事件是不是我这个主人的。</summary>
    private bool IsMine(Player player) => (base.Owner?.Player ?? base.Owner?.PetOwner) == player;
}
