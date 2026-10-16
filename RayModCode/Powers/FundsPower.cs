using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 罕见能力牌「资金」的载体：<b>层数每累计变动 12，抽 1 张牌</b>。
///
/// <para><b>口径</b>（用户 2026-10-03 拍板 Q10）：从<b>打出这张牌那一刻</b>开始计数。
/// 计的是层数变动的<b>数量</b>（<c>|新 - 旧|</c>，<c>AddDepth</c> 一次算一次事件），
/// 不是"变动了多少次"—— 层数一次动 3~12 点很常见，按次数记会慢到没意义。
/// 正负都算（撤退、深入、塌方一样有效）。</para>
///
/// <para>余数保留：累计 7 + 再来 7 = 触发一次 + 剩 2。</para>
/// </summary>
[RegisterPower]
public sealed class FundsPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(FundsPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    /// <summary>
    /// 累计变动多少层抽一张。
    /// ★ 默认值只是兜底；实际值在能力被施加时从打出它的那张卡的
    /// <c>DynamicVars["DepthPerDraw"]</c> 读进来（卡面数字改了就真的生效）。
    /// </summary>
    private int _depthPerDraw = 12;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示"每累计多少层抽一张"。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    private int _progress;
    private bool _subscribed;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        // 从打出它的卡上取真实值（卡面数字改了就真的生效）；取不到就沿用默认值。
        if (cardSource is { } src && src.DynamicVars.ContainsKey("DepthPerDraw"))
        {
            _depthPerDraw = src.DynamicVars["DepthPerDraw"].IntValue;
        }

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

        int delta = Math.Abs(newDepth - oldDepth);

        if (delta <= 0)
        {
            return;
        }

        _progress += delta;

        int draws = _progress / _depthPerDraw;

        if (draws <= 0)
        {
            return;
        }

        _progress -= draws * _depthPerDraw;

        _ = TaskHelper.RunSafely(Draw(draws * (int)base.Amount));
    }

    private async Task Draw(int count)
    {
        var owner = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (owner == null || count <= 0)
        {
            return;
        }

        await CardPileCmd.Draw(new ThrowingPlayerChoiceContext(), count, owner);
    }
}
