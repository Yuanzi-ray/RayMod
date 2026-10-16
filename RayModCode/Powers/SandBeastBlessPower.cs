using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「沙地兽的祝福」的载体：<b>每召唤 4 只沙地兽，获得 1 点能量</b>。
///
/// <para>计数<b>从打出这张牌那一刻</b>开始（用户 2026-10-03 拍板 Q10）——
/// 否则一打出就会按"本场已经召唤过多少只"倒推，白送好几瓶能量。
/// 进度存在 <c>_progress</c> 里，<c>Amount</c> 表示"每满一次给多少能量"（角标显示它）。</para>
///
/// <para>余数保留（召唤 6 只 = 触发 1 次 + 剩 2 只计入下一轮），这样"3 只 + 3 只"不会白白浪费。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastBlessPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SandBeastBlessPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    /// <summary>
    /// 每召唤多少只触发一次。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["SummonsPerTrigger"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _summonsPerTrigger = 4;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示"每满一次给多少能量"。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    private int _progress;
    private bool _subscribed;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        // 从打出它的卡上取真实值（卡面数字改了就真的生效）；取不到就沿用默认值。
        if (cardSource is { } src && src.DynamicVars.ContainsKey("SummonsPerTrigger"))
        {
            _summonsPerTrigger = src.DynamicVars["SummonsPerTrigger"].IntValue;
        }

        if (!_subscribed)
        {
            DepthEvents.RegisterSummoned(OnSummoned);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    public override Task AfterRemoved(Creature oldOwner)
    {
        DepthEvents.UnregisterSummoned(OnSummoned);
        _subscribed = false;

        return Task.CompletedTask;
    }

    private void OnSummoned(Player player, int count, CardModel? cardSource)
    {
        if (count <= 0 || (base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        _progress += count;

        int triggers = _progress / _summonsPerTrigger;

        if (triggers <= 0)
        {
            return;
        }

        // 余数留着，别清空。
        _progress -= triggers * _summonsPerTrigger;

        _ = TaskHelper.RunSafely(Grant(triggers * (int)base.Amount));
    }

    private async Task Grant(int energy)
    {
        var owner = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (owner == null || energy <= 0)
        {
            return;
        }

        await PlayerCmd.GainEnergy(energy, owner);
    }
}
