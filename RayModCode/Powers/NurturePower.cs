using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Depth;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 先古能力牌「哺育」的载体：<b>每当你召唤沙地兽时，把等量的「沙地兽」卡加入手牌</b>。
///
/// <para><b>用户 2026-10-04 拍板的两条口径（设计稿那句话有歧义）</b>：</para>
/// <list type="number">
/// <item><b>"（不包括沙地兽这张牌）"= 打出「沙地兽」<u>这张卡</u>触发的召唤不计数。</b>
/// 这正是掐断"给牌 → 打牌 → 又召唤 → 又给牌"自我循环的那一刀：
/// <c>BeastCmd.Summon</c> 会把引起召唤的卡通过 <see cref="DepthEvents.RaiseMinionSummoned"/>
/// 一起广播出来（<c>cardSource</c>），这里判 <see cref="Cards.SandBeast"/> 即可 ——
/// 不去另写一套判据（坑 31/52 的同源原则）。</item>
/// <item><b>数量 = 本次召唤的只数</b>（一次召唤 3 只 = 给 3 张），
/// <b>不是</b>"本场累计召唤过多少只"（那会滚雪球）。</item>
/// </list>
///
/// <para>⚠️ 订阅挂在<b>静态</b>总线上，所以 <see cref="AfterRemoved"/> 必须注销（坑 32）。</para>
/// <para>⚠️ 处理器是同步的，造卡是异步的 —— 按总线约定自己 <c>TaskHelper.RunSafely</c> 派发，
/// 并且整个异步体包 try/catch（坑 42：回合开始钩子里抛异常会让整个游戏卡死，
/// 而"召唤"完全可能发生在回合开始的钩子里）。</para>
/// </summary>
[RegisterPower]
public sealed class NurturePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(NurturePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    private bool _subscribed;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标只显示一个 1（这张牌没有"层数"语义）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override Task AfterApplied(Creature? applier, CardModel? cardSource)
    {
        if (!_subscribed)
        {
            DepthEvents.RegisterSummoned(OnSummoned);
            _subscribed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 一定要在这里注销：订阅是挂在<b>静态</b>总线上的，
    /// 不注销的话这张牌的效果会在 Power 退场后继续生效（而且不报错，坑 32）。
    /// </summary>
    public override Task AfterRemoved(Creature oldOwner)
    {
        DepthEvents.UnregisterSummoned(OnSummoned);
        _subscribed = false;

        return Task.CompletedTask;
    }

    private void OnSummoned(Player player, int count, CardModel? cardSource)
    {
        if (count <= 0)
        {
            return;
        }

        if ((base.Owner?.Player ?? base.Owner?.PetOwner) != player)
        {
            return;
        }

        // 拍板①：打出「沙地兽」这张卡引起的召唤不计数（否则那张卡会自己喂自己）。
        if (cardSource is Cards.SandBeast)
        {
            Log.Info("[哺育] 本次召唤来自打出「沙地兽」卡，按拍板不计数");
            return;
        }

        // 事件总线是同步的，造卡是异步的 —— 按 DepthEvents 的约定自己派发。
        _ = TaskHelper.RunSafely(GiveCards(player, count));
    }

    private async Task GiveCards(Player player, int count)
    {
        try
        {
            var combatState = player.Creature.CombatState;

            if (combatState == null)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                // ⚠️ 造"要进战斗手牌"的卡必须用 CombatState.CreateCard（坑 41）。
                var beastCard = combatState.CreateCard<Cards.SandBeast>(player);

                await CardPileCmd.AddGeneratedCardToCombat(beastCard, PileType.Hand, player);
            }

            Log.Info($"[哺育] 本次召唤 {count} 只 → 加入手牌 {count} 张「沙地兽」");
        }
        catch (Exception e)
        {
            // 效果失败最多是"这回合少给几张牌"，绝不能把游戏挂掉（坑 42）。
            Log.Error($"[哺育] 给牌失败（{count} 张）：{e}");
        }
    }
}
