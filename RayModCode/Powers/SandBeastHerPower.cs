using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using RayMod.Beasts;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「沙地兽与她」的载体：<b>沙地兽印记效果 +20%</b>（每层 10% → 12%）。
///
/// <para>效果本身不是这个 Power 算的 —— 印记增伤在 <c>SandBeastMarkPower.ModifyDamageMultiplicative</c> 里，
/// 它读的是 <c>BeastState.MarkBonusAbility</c> 这个开关（<b>该字段当初就是为这张牌预留的</b>，
/// 见 <c>BeastState.cs:49</c> 的注释）。所以这里只负责在"被施加时"把开关打开，
/// 让这套效果有角标、悬停说明，并且在存档/战斗重置时和开关同生命周期。</para>
///
/// <para>上限 +1 不在这里做 —— 那是卡牌 <c>OnPlay</c> 里的一次性动作
/// （<c>BeastCmd.ModifyCap</c>，会被夹在 [0, MinionCapMax=6]）。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastHerPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SandBeastHerPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示打了几张。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>打出的瞬间把"印记 +20%"开关打开（开关是每场战斗重置的，与本 Power 同生命周期）。</summary>
    public override Task BeforeApplied(Creature target, decimal amount, Creature? applier, CardModel? cardSource)
    {
        // 坑 1：玩家自己的 Creature 上 PetOwner 是 null，那个字段叫 Player。
        if (BeastCmd.TryGetState(target.Player ?? target.PetOwner) is { } state)
        {
            state.MarkBonusAbility = true;
        }

        return Task.CompletedTask;
    }
}
