using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Powers;

/// <summary>
/// 稀有 能力牌「沙地兽巢穴」的载体：<b>每回合开始把 Amount 张「沙地兽」加入手牌</b>
/// （打出时 2；升级版不再加数量，而是给那张<b>卡牌</b>加「固有」）。
///
/// <para>钩子用 <c>BeforeHandDraw</c> 而不是 <c>AfterPlayerTurnStart</c> —— 照抄原版
/// <c>SentryModePower</c>（<c>src\Core\Models\Powers\SentryModePower.cs:22</c>）：
/// 它在抽牌<b>之前</b>把牌塞进手里，所以玩家第一眼看到手牌时那张牌就在了。</para>
///
/// <para>⚠️ 造的是<b>卡牌</b> <c>RayMod.Cards.SandBeast</c>，不是召唤物 ——
/// 和「饲养」「背扇」同一套（那边是"加进手牌/抽牌堆"，这里挂在 Power 上每回合自动来一张）。</para>
/// </summary>
[RegisterPower]
public sealed class SandBeastLairPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(SandBeastLairPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示每回合给几张。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    public override async Task BeforeHandDraw(Player player, PlayerChoiceContext choiceContext, ICombatState combatState)
    {
        if (player != (base.Owner?.Player ?? base.Owner?.PetOwner))
        {
            return;
        }

        int count = (int)base.Amount;

        for (int i = 0; i < count; i++)
        {
            var beastCard = combatState.CreateCard<RayMod.Cards.SandBeast>(player);

            await CardPileCmd.AddGeneratedCardToCombat(beastCard, PileType.Hand, player);
        }
    }
}
