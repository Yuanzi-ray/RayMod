using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using RayMod.Cards;
using STS2RitsuLib.Interop.AutoRegistration;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有能力牌「奇异宝藏」的载体：<b>每打出一张 0 费牌 → 抽牌堆随机一张"打到之前 0 费"
/// + 手牌随机一张"打到之前费用翻倍"</b>。
///
/// <para><b>"费用为 0"的判定用 <c>cardPlay.Card.EnergyCost.GetResolved() == 0</c></b>
/// （含全部修正的当前费用）—— 所以被「Winged Step」「追光者之瞳」抹成 0 费的牌、
/// 以及本来 0 费的牌都会触发；而 X 费牌取的是本次实际花的能量
/// （<c>GetResolved()</c> 对 X 费返回 <c>CapturedXValue</c>，X=0 时算 0 费）。</para>
///
/// <para><b>钩子时机</b>：<c>AfterCardPlayed</c> 在"移动到结果牌堆"<b>之前</b>触发
/// （<c>CardModel.cs</c>：<c>Hook.AfterCardPlayed</c> 在前、结果牌堆移动在后），
/// 而费用修正的清理 <c>EnergyCost.AfterCardPlayedCleanup()</c> 更晚 ——
/// 所以此刻读到的还是"打出时那个费用"。被这张牌自己生成的 0 费牌不会递归
/// （它改的是别的牌，不是自己）。</para>
///
/// <para>两个目标都<b>静默跳过空牌堆</b>：抽牌堆空了就不改，手牌空了就不翻倍 ——
/// 不是错误，只是这次没有可改的牌。</para>
///
/// <para>随机流用 <c>RunState.Rng.CombatCardSelection</c>（"随机选牌"语义流）。</para>
/// </summary>
[RegisterPower]
public sealed class StrangeTreasurePower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(StrangeTreasurePower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    private static readonly Logger Log = new("RayMod.Powers", LogType.Generic);

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标只显示一个 1（没有"层数"语义）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var owner = base.Owner?.Player ?? base.Owner?.PetOwner;

        if (owner == null || cardPlay.Card?.Owner != owner)
        {
            return;
        }

        // 只认"费用为 0"的牌。
        if (cardPlay.Card.EnergyCost.GetResolved() != 0)
        {
            return;
        }

        try
        {
            var rng = owner.RunState.Rng.CombatCardSelection;

            // ① 抽牌堆随机一张 → 打到之前 0 费。
            var drawPile = PileType.Draw.GetPile(owner).Cards;

            if (drawPile.Count > 0)
            {
                var freebie = rng.NextItem(drawPile);

                if (freebie != null)
                {
                    freebie.EnergyCost.SetUntilPlayed(0);
                }
            }

            // ② 手牌随机一张 → 打到之前费用翻倍（复用「发家致富」的同一实现，口径不会跑偏）。
            var hand = PileType.Hand.GetPile(owner).Cards;

            if (hand.Count > 0)
            {
                var victim = rng.NextItem(hand);

                if (victim != null)
                {
                    GetRich.DoubleCost(victim);
                }
            }
        }
        catch (Exception e)
        {
            // 效果失败最多是"这次没改牌"，不能让出牌流程崩掉。
            Log.Warn($"[奇异宝藏] 改费用失败（本次跳过）: {e}");
        }

        await Task.CompletedTask;
    }
}
