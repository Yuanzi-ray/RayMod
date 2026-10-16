using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Beasts;

/// <summary>
/// 沙地兽印记：挂在<b>敌人</b>身上的减益。角标数字 = 印记层数。
///
/// <para><b>规则（用户 2026-10-03 拍板）：</b></para>
/// <list type="bullet">
/// <item>每只沙地兽被召唤时随机标记一个敌人 = 给这个敌人 +1 层印记。</item>
/// <item>每层使该敌人受到的<b>攻击牌伤害</b>提高 10%（加法：2 层 = ×1.2，3 层 = ×1.3）。</item>
/// <item>只对攻击牌生效 —— 照抄原版 <c>VulnerablePower</c> 的口径
/// （<c>props.IsPoweredAttack()</c>），否则层数自伤之类的非攻击伤害也会被加成。</item>
/// <item>沙地兽退场时移除自己那一层（<see cref="BeastCmd.RemoveMarkOf"/>）。</item>
/// <item>目标敌人死亡 → 印记随之消失，且沙地兽<b>不会</b>重新标记（用户明确要求）。</item>
/// </list>
///
/// <para><b>每回合配额</b>：上限决定每回合有几张牌能吃到这个增伤。配额记账不在本类，
/// 而在玩家侧的 <see cref="SandBeastQuotaPower"/>（本类只负责"问一句还能不能吃"）。</para>
///
/// <para><b>为什么用 VulnerablePower 那套而不是自己造</b>：原版受伤百分比就是走
/// <c>ModifyDamageMultiplicative</c>（<c>VulnerablePower.cs:28</c>、
/// <c>SlowPower.cs:43</c> 是 <c>1m + 0.1m * n</c> 的现成写法），
/// 引擎合成顺序是 加法 → 乘法 → 封顶（<c>Hook.cs:2523</c> 起）。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastMarkPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SandBeastMarkPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Debuff;

    /// <summary>Counter：角标显示层数。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override decimal ModifyDamageMultiplicative(
        Creature? target, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        // 只影响"身上带着这个印记的那个敌人"（同 VulnerablePower）。
        if (target != base.Owner)
        {
            return 1m;
        }

        // 只对攻击牌生效。
        if (!props.IsPoweredAttack())
        {
            return 1m;
        }

        // 必须是玩家这边打出来的（玩家本人或其宠物）。
        // ⚠️ 玩家自己的 Creature 上 PetOwner 为 null，那个字段是 Player —— 两个都要试。
        var player = dealer?.Player ?? dealer?.PetOwner;
        var state = BeastCmd.TryGetState(player);

        if (state == null)
        {
            return 1m;
        }

        // 配额用完了就不再吃加成（但也不扣）。
        var quota = player!.Creature.GetPower<SandBeastQuotaPower>();

        if (quota == null || !quota.CanSpendQuota(state))
        {
            return 1m;
        }

        // 记一笔"这张牌吃到了加成"，真正的扣减在 AfterCardPlayed 里做 ——
        // 那里才知道一张牌结束了没有（一张牌多段攻击只该扣 1 点）。
        //
        // ★★★ 2026-10-13 联机（**实机复现的真 desync，已修**）：
        //   **这里绝不能无条件写状态。** `ModifyDamageMultiplicative` 是**查询型**钩子，
        //   而它不只被"真实伤害结算"调用 —— **卡面/预览也算伤害、也走这个钩子**：
        //     · `CalculatedDamageVar.cs:64`（`cardPlay: null`）
        //     · `DamageVar.cs:43`（`cardPlay: null`）
        //   而 `Hook.ModifyDamageInternal` **完全不看 `previewMode`**（实测读过源码），
        //   于是预览会一路走到这里。**预览的调用次数是每个客户端自己的事**
        //   （本 mod 的卡面补丁 `Patches\BulletAmmoCostHintPatch`、游戏的悬停/描述渲染
        //    都会触发），⇒ 两端"这张牌到底吃没吃过印记加成"的看法会不同
        //   ⇒ 配额消耗数不同 ⇒ 镜像进 `SandBeastQuotaPower.Amount` ⇒ **联机校验和分歧**。
        //   实测现场（2026-10-13）：
        //     `State divergence detected! Checksum with ID 27 for client 2000 doesn't match host's!`
        //     双方状态 dump 的**唯一**差异就是
        //     `RAY_MOD_POWER_SAND_BEAST_QUOTA_POWER`：主机 Amount 0 / 客机 1
        //     （主机日志那句 `配额已用 2/2` 就是它），客机随后被踢回主菜单。
        //
        //   分界线就是 `cardPlay`：**真实结算一定带 `CardPlay`，预览一律传 `null`**。
        //   ⚠️ 注意**仍然要返回下面的倍率** —— 否则卡面就不再显示印记加成了（那是显示回归）。
        //      被这道门挡掉的**只是"记账"这一步**，不是加成本身。
        //   ⚠️ 多段攻击会对同一张牌调这里多次，但 `NoteBonusConsumed()` 只是置一个 bool
        //      ⇒ 幂等，仍然"一张牌只扣 1 点"。
        if (cardPlay != null)
        {
            quota.NoteBonusConsumed();
        }

        decimal perStack = BeastConstants.MarkBonusPerStack;

        if (state.MarkBonusAbility)
        {
            perStack *= BeastConstants.MarkBonusAbilityMultiplier;
        }

        return 1m + perStack * Amount;
    }
}
