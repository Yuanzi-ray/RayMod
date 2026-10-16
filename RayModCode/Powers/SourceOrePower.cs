using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「源矿」的载体：<b>每次层数变化，对随机敌人造成 6 点伤害</b>。
///
/// <para><b>口径</b>（用户 2026-10-03 拍板 Q11）：一次 <c>AddDepth</c> 调用 = 一次事件。
/// 注意「塌方」是"先算档数、再一次性 <c>AddDepth</c>"，所以它只触发一次（这正是不滚雪球的设计）。</para>
///
/// <para>用 <c>ValueProp.Unpowered</c>：这是<b>能力牌给的伤害</b>，不是攻击牌 ——
/// 所以不吃力量/易伤，也不吃沙地兽印记（印记明确只对攻击牌生效）。
/// 照抄原版 <c>CacophonyPower</c>（Power 打随机敌人伤害的现成写法）。仍然可以被格挡。</para>
/// </summary>
[RegisterPower]
public sealed class SourceOrePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SourceOrePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每次打多少。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    private bool _subscribed;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        if (!_subscribed)
        {
            DepthEvents.Register(OnDepthChanged);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    public override Task AfterRemoved(Creature oldOwner)
    {
        DepthEvents.Unregister(OnDepthChanged);
        _subscribed = false;

        return Task.CompletedTask;
    }

    private void OnDepthChanged(Player player, int oldDepth, int newDepth)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        _ = TaskHelper.RunSafely(Strike());
    }

    private async Task Strike()
    {
        var owner = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (owner == null)
        {
            return;
        }

        var enemies = owner.Creature.CombatState?.HittableEnemies;

        if (enemies == null || enemies.Count == 0)
        {
            return;
        }

        // 坑：NextItem 在空集合时返回 null，必须判空（原版反编译里那个非空赋值是抑制符的产物）。
        var target = owner.RunState.Rng.CombatTargets.NextItem(enemies);

        if (target == null)
        {
            return;
        }

        await CreatureCmd.Damage(
            new ThrowingPlayerChoiceContext(), target, base.Amount, ValueProp.Unpowered, base.Owner);
    }
}
