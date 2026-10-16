using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using RayMod.Beasts;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「萌宠博主」的载体：<b>把"召唤沙地兽时随机标记一个敌人"换成"所有敌人各挂一层"，
/// 并对所有有印记的敌人各造成 Amount 点伤害</b>（升级加的"固有"不在这里）。
///
/// <para><b>用户 2026-10-04 拍板⑦</b>："<b>替换</b>原来的随机单标记（改为所有敌人各挂一层）"。
/// 所以它<b>不是</b>在随机标记之上再加一层 —— 挂印记那一步整个换掉了。
/// 实现点在 <c>BeastCmd.MarkOnSummon</c>（那里查这个 Power 存不存在）。</para>
///
/// <para><b>为什么挂印记不在这类里做</b>：挂印记要和"这只兽持有哪几层"严格 1:1 记账
/// （<c>SandBeast.AddMarkedEnemy</c>），而那个记账对象是召唤流程里的实例 ——
/// 所以逻辑留在 <c>BeastCmd</c>，这里只负责"入场之后的附带伤害"。</para>
///
/// <para><b>伤害口径</b>：用 <c>ValueProp.Unpowered</c> —— 这是<b>能力牌给的伤害</b>，
/// 不是攻击牌，所以不吃力量/易伤/沙地兽印记，也不进「你只能走到这了」的弹药账本。
/// 照抄本工程「源矿」与「沙地兽之怒」同源的写法（Power 打伤害的现成模板）。
/// 仍然可以被格挡。</para>
///
/// <para><b>触发时机</b>：调用方是 <c>BeastCmd.MarkOnSummon</c>，<b>每召唤一只</b>结算一次 ——
/// 所以一次召唤 3 只 = 3 波"全体各挨 4 点"，而不是只有一波。</para>
/// </summary>
[RegisterPower]
public sealed class PetBloggerPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(PetBloggerPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每次打多少伤害。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>
    /// 一只沙地兽入场后：对所有<b>有印记</b>的敌人各造成一次伤害。
    /// 由 <see cref="BeastCmd"/> 在挂完印记之后调用（那时"所有敌人"身上都刚挂了一层）。
    /// </summary>
    public async Task OnBeastSummoned(PlayerChoiceContext choiceContext)
    {
        if (base.Owner is not { } self)
        {
            return;
        }

        var enemies = self.CombatState?.HittableEnemies;

        if (enemies == null || enemies.Count == 0)
        {
            return;
        }

        // 快照一份：伤害会打死人，边遍历边变脸最容易漏打。
        var marked = enemies
            .Where(enemy => enemy.GetPower<SandBeastMarkPower>() != null)
            .ToList();

        if (marked.Count == 0)
        {
            return;
        }

        Flash();

        foreach (var enemy in marked)
        {
            if (!enemy.IsAlive)
            {
                continue;
            }

            await CreatureCmd.Damage(
                choiceContext, enemy, base.Amount, ValueProp.Unpowered, self);
        }
    }
}
