using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Ammo;
using RayMod.Characters;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名（和 MineRelic 一致）。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Relics;

/// <summary>
/// 遗物「弩」（2026-10-12 用户指定）：<b>弹药永久上限 +3</b>（7 → 10）。
///
/// <para><b>"永久"是怎么实现的</b>：弹药上限的真值在 <see cref="AmmoState.Cap"/>，
/// 而它每场战斗都会被 <c>AmmoCmd.EnsurePower</c> → <c>ResetForCombat(基础上限)</c> 重算一次。
/// 所以只要把"基础上限"改成"<see cref="AmmoState.InitialCap"/> + 遗物加成"，
/// 加成就会<b>每场自动重新生效</b>（= 永久），而战斗内被卡牌改过的上限
/// （「扩张」/「无限」/「重大升级」的 +N）仍然只活一场 —— 两种语义天然分开，不用额外记账。</para>
///
/// <para><b>加成怎么被认出来</b>：实现 <see cref="IAmmoCapModifier"/> 即可，
/// <c>AmmoState.BaseCap(player)</c> 会把玩家身上所有实现者的 <c>AmmoCapBonus</c> 加起来
/// （弹药层不认识"弩"这个具体遗物）。</para>
///
/// <para><b>三个同步点</b>（缺一个就会出现"上限没变/变了又变回去"）：
/// <list type="number">
/// <item>战斗开始 —— <c>ResetForCombat(BaseCap)</c>（已有，自动带上加成）；</item>
/// <item>战斗中途拿到它 —— <see cref="AfterObtained"/> 调 <c>AmmoCmd.RecomputeCap</c>；</item>
/// <item>失去它（熔毁 / 被移除）—— <see cref="AfterRemoved"/> 同样重算，本场上限立刻退回 7。</item>
/// </list></para>
///
/// <para>⚠️ <b>开局装填量不变</b>：<c>InitialAmmo = 4</c> 是"装几颗"不是"上限"，
/// 所以拿到「弩」是 4/10 而不是 7/10 —— 多出来的是<b>可以攒的空间</b>（靠装填类牌填）。
/// 这是刻意的口径（见 <c>AmmoState.InitialAmmo</c> 的注释）。</para>
/// </summary>
[RegisterRelic(typeof(RayModRelicPool))]
public sealed class Crossbow : ModRelicTemplate, IAmmoCapModifier
{
    private static readonly Logger Log = new("RayMod.Relics", LogType.Generic);

    /// <summary>基础上限加成（用户 2026-10-12 指定：+3）。</summary>
    public const int CapBonus = 3;

    /// <inheritdoc />
    public int AmmoCapBonus => CapBonus;

    /// <summary>
    /// 描述里的数字走占位符（铁律 28）。遗物的描述显示路径是
    /// <c>RelicModel.DynamicDescription</c>（<c>RelicModel.cs:91-99</c> 会
    /// <c>DynamicVars.AddTo(description)</c>），所以 <c>{CapBonus}</c> 能正常渲染。
    /// ⚠️ 名字刻意<b>不叫 <c>Cap</c></b>：卡牌那边有一个同名的「本卡加多少上限」变量，别混。
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
        new DynamicVar[] { new DynamicVar("CapBonus", CapBonus) };

    /// <summary>
    /// 稀有度：「稀有」。
    /// <para>弹药是本角色的核心资源（上限决定"能攒多少燃料"、且多张攻击牌的伤害按弹药结算），
    /// +3 上限是长期收益，不是一次性爆发 ⇒ 定 Rare。
    /// ⚠️ 这是**我拍的**，用户没指定，要改就改这一行（Common/Uncommon/Rare 才会进奖励掷骰）。</para>
    /// </summary>
    public override RelicRarity Rarity => RelicRarity.Rare;

    /// <summary>图标：与「下矿」同一套 AssetProfile 写法（三处都指向同一张 256×256）。</summary>
    public override RelicAssetProfile AssetProfile => new(
        IconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        IconOutlinePath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png",
        BigIconPath: $"{Entry.ResPath}/images/relics/{GetType().Name}.png");

    /// <summary>战斗中拿到时立刻把本场上限抬上去（战斗外拿到不用管：下场 ResetForCombat 会算进去）。</summary>
    public override Task AfterObtained()
    {
        if (Owner is { } owner)
        {
            AmmoCmd.RecomputeCap(owner);
        }

        return Task.CompletedTask;
    }

    /// <summary>失去它（熔毁 / 被移除）时本场上限立刻退回。</summary>
    public override Task AfterRemoved()
    {
        if (Owner is { } owner)
        {
            Log.Info($"[弩] 已失去：弹药基础上限退回 {AmmoState.InitialCap}");
            AmmoCmd.RecomputeCap(owner);
        }

        return Task.CompletedTask;
    }
}
