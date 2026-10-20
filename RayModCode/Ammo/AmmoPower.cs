using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Interop.AutoRegistration;
using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;

namespace RayMod.Ammo;

/// <summary>
/// 弹药（子弹）在战斗内的显示载体。
///
/// <para><b>本类是照着 <see cref="Depth.DepthPower"/> 一比一写的</b>，
/// 因为层数那套已经被实测证明"角标 + 悬停都能正确显示"。
/// 不要在这里引入 DepthPower 没有的东西（尤其是解析 Player 的守卫）——
/// 见 <see cref="SyncFromData"/> 的说明。</para>
///
/// <para><b>⚠️ 与 DepthPower 唯一的、故意的差异</b>：层数那边 <c>SetAmount</c> 用的是
/// <c>Math.Max(depth, 1)</c>（层数 0 会显示成 1），本类<b>不给这个兜底</b> ——
/// 因为弹药的开局值就是 0，兜底会让角标和悬停都从 1 起跳（用户实测报过）。
/// 别为了"和层数保持一致"把这行改回去。
/// （层数那边 2026-10-03 用户明确选择<b>保持现状</b>，也不要顺手去改它。）</para>
///
/// <para><b>职责边界</b>：真实弹药存在 <see cref="AmmoState"/>（跑团级存档容器）；
/// 本类只负责显示。改弹药一律走 <see cref="AmmoCmd"/>。</para>
///
/// <para><b>显示</b>：<c>Amount</c> 直接等于<b>真实弹药数</b>（<b>可以是 0</b>）。
/// 角标（<see cref="DisplayAmount"/>，由 <c>NPower.RefreshAmount</c> 读取）与悬停描述里的
/// <c>{Amount}</c>（由 <c>PowerModel.HoverTips</c> 绑到 <c>Amount</c>）读的都是它，天然一致。</para>
///
/// <para><b>为什么敢让 <c>Amount</c> 落到 0</b>（2026-10-03 实测 + 反编译核对）：
/// 原版"Amount 归零就移除 Power"的判断 <c>ShouldRemoveDueToAmount()</c>
/// 在 0.110.1 的源码里<b>只有一个调用点</b> —— <c>PowerCmd.ModifyAmount</c>
/// （<c>Core/Commands/PowerCmd.cs:251</c>，方法体见 <c>PowerModel.cs:478</c>）。
/// 本 Power 的数值全程走 <c>SetAmount</c>（见 <see cref="SyncFromData"/>），
/// <b>从不经过 <c>PowerCmd.ModifyAmount</c></b>（全工程 grep 确认），
/// 所以 0 不会把 Power 摘掉，也就不会误触发「Power 不在位 == 本场第一次」的归零逻辑。</para>
///
/// <para><b>教训</b>：早先版本在这里用 <c>max(弹药, 1)</c> 兜底"保命"，
/// 代价就是弹药为 0 时角标和悬停都显示 1（用户 2026-10-03 实测发现）。
/// 兜底前先确认那个风险到底存不存在 —— 这次它不存在。</para>
/// </summary>
[RegisterPower]
public sealed class AmmoPower : PowerModel, IModPowerAssetOverrides
{
    /// <summary>
    /// 图标（2026-10-04 用户提供：桌面上的「职业分支图标_猎手.png」）。
    ///
    /// <para>挂法说明：我们的 Power 是裸 <c>PowerModel</c>（不是 <c>ModPowerTemplate</c>），
    /// 所以走 RitsuLib 的 <see cref="IModPowerAssetOverrides"/> 接口 —— 它的
    /// <c>PowerIconPathPatch</c> 补的就是 <c>PowerModel.IconPath</c> / <c>PackedIconPath</c> 两个取值器。
    /// 三个成员都要给（<c>AssetProfile</c> + <c>CustomIconPath</c> + <c>CustomBigIconPath</c>），
    /// 否则补丁拿不到路径、日志里就是原来那句 <c>Missing sprite 'ray_mod_power_ammo_power'</c>。</para>
    /// </summary>
    private static readonly PowerAssetProfile IconProfile = new(
        IconPath: $"{Entry.ResPath}/images/powers/AmmoPower.png",
        BigIconPath: $"{Entry.ResPath}/images/powers/AmmoPower.png");

    public PowerAssetProfile AssetProfile => IconProfile;

    public string? CustomIconPath => IconProfile.IconPath;

    public string? CustomBigIconPath => IconProfile.BigIconPath;

    internal sealed class Data
    {
        /// <summary>当前弹药快照（真实值在 AmmoState）。</summary>
        public int Current;

        /// <summary>本场战斗的弹药上限快照。</summary>
        public int Cap = AmmoState.InitialCap;
    }

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：数字可见（弹药数）。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    public override bool AllowNegative => false;

    /// <summary>
    /// <b>不在状态栏显示这个 Power</b>（2026-10-07）：弹药已经改成显示在**能量球旁边**的计数器上
    /// （见 <see cref="AmmoHud"/>），状态栏再挂一个图标就是同一份信息说两遍。
    ///
    /// <para>⚠️ <b>只是不"显示"，Power 本身照旧存在</b> —— 卡的结算、弹药账本（<c>AmmoLedgerPower</c>）、
    /// 回合逻辑全都依赖它的 Amount。判据是 <c>PowerModel.IsVisibleInternal</c>（<c>PowerModel.cs:167</c>，
    /// 消费点在 <c>NPowerContainer.cs:106</c>），只影响图标画不画。</para>
    /// </summary>
    protected override bool IsVisibleInternal => false;

    protected override object InitInternalData() => new Data();

    /// <summary>
    /// 弹药<b>上限</b>的显示变量（描述里的 <c>{Cap}</c>）。
    ///
    /// <para><b>为什么必须是 DynamicVar，而不是在 <see cref="Description"/> 覆写里
    /// <c>loc.Add("Cap", …)</c></b>：<c>PowerModel.HoverTips</c> 有两条渲染路径 ——
    /// 智能描述那条只补 <c>Amount</c>/<c>singleStarIcon</c>/<c>energyPrefix</c> 这些内置变量，
    /// <b>不会</b>调用覆写里的自定义绑定；它补的是 <c>DynamicVars.AddTo(locString)</c>
    /// （<c>PowerModel.cs:390</c>）。所以只有定义在 <see cref="CanonicalVars"/> 里的变量
    /// 两条路都能解析。这正是类注释里那条血的教训的通用解法。</para>
    ///
    /// <para>上限是<b>会变</b>的（重大升级 +10 / 无限子弹 每回合 +2 / 扩容弹匣 +3 /
    /// 填装形态 每张牌 +1），所以 <see cref="SyncFromData"/> 每次都会把它同步过来。
    /// 早先文案把上限<b>写死</b>成"7"，上限涨了悬停也不变（用户 2026-10-04 报）。</para>
    /// </summary>
    protected override IEnumerable<DynamicVar> CanonicalVars =>
    [
        new DynamicVar("Cap", AmmoState.InitialCap)
    ];

    /// <summary>当前弹药（快照）。</summary>
    public int Ammo => GetInternalData<Data>().Current;

    /// <summary>本场战斗的弹药上限（快照）。</summary>
    public int AmmoCap => GetInternalData<Data>().Cap;

    /// <summary>
    /// 角标显示的数字（<c>NPower.RefreshAmount</c> 读的就是它）
    /// = <c>Amount</c> = <b>真实弹药数</b>（0 就是 0）。
    ///
    /// <para>原版默认实现也是返回 <c>Amount</c>，这里显式覆写只是为了标明
    /// "这个数字就是弹药数"，<b>别再往里加 <c>max(..., 1)</c> 之类的兜底</b> ——
    /// 那会让 0 弹药显示成 1。</para>
    /// </summary>
    public override int DisplayAmount => Amount;

    /// <summary>
    /// 描述：把 <c>{Amount}</c> 绑成弹药数（上限 7 写在文本里）。
    ///
    /// <para><b>⚠️ 不要用自定义变量名（如 <c>{AmmoDisplay}</c>）。</b>
    /// 实测踩过：<c>PowerModel.HoverTips</c> 走 SmartDescription 分支时，
    /// 只补 <c>Amount</c> / <c>OnPlayer</c> / <c>PlayerCount</c> / <c>OwnerName</c>
    /// 这些内置变量，<b>不会</b>调用本覆写里的自定义绑定，于是 SmartFormat 报
    /// <c>No source extension could handle the selector named "AmmoDisplay"</c>，
    /// 整段描述渲染失败（和之前 <c>{Depth}</c> 那次是同一类坑）。</para>
    /// </summary>
    public override LocString Description
    {
        get
        {
            var loc = new LocString("powers", base.Id.Entry + ".description");
            if (loc.Exists())
            {
                DynamicVars.AddTo(loc);
                loc.Add("Amount", Amount);
            }

            return loc;
        }
    }

    /// <summary>让"智能描述"复用 description（理由同 DepthPower）。</summary>
    protected override string SmartDescriptionLocKey => base.Id.Entry + ".description";

    /// <summary>
    /// 本 Power 是否<b>已经从数据源同步过至少一次</b>（见
    /// <see cref="AfterPowerAmountChanged"/> 的说明 —— 它是"出生值不是外部改动"的判据）。
    /// </summary>
    private bool _syncedFromData;

    /// <summary>
    /// 把弹药与上限同步进显示。
    ///
    /// <para><b>⚠️ 这里刻意不解析 Player。</b>早先版本开头写了
    /// <c>if (base.Owner?.PetOwner is not Player) return;</c>，
    /// 结果<b>角标和悬停全是错的</b> —— <c>Creature.PetOwner</c> 只对<b>宠物</b>有值
    /// （由 <c>PlayerCombatState.AddPet</c> 设置），玩家自己的 Creature 上
    /// <c>PetOwner</c> 恒为 null（那个是 <c>Creature.Player</c>）。
    /// 所以守卫每次都成立、直接 return，<c>SetAmount</c> 从没执行过，
    /// 显示永远停在创建时的 1。</para>
    ///
    /// <para><b>教训</b>：显示同步只需要摆弄 Power 自己的字段，<b>不要</b>去解析 Player。
    /// 真正需要 Player 的地方（给金币、给药水）用
    /// <c>Owner.Player ?? Owner.PetOwner</c>，并且不要因为解析失败就跳过副作用之外的显示更新。</para>
    /// </summary>
    internal void SyncFromData(int current, int cap)
    {
        var data = GetInternalData<Data>();
        data.Current = current;
        data.Cap = cap;

        // 上限会变，描述的 {Cap} 必须跟着走（见 CanonicalVars 的注释）。
        DynamicVars["Cap"].BaseValue = cap;

        // 与 DepthPower.SyncFromRunState 完全同构：只摆弄自己的字段，不解析 Player。
        //
        // 直接给真实弹药数（0 也给 0）。安全性依据见类注释：
        // ShouldRemoveDueToAmount() 的唯一调用点是 PowerCmd.ModifyAmount，
        // 本 Power 的数值从不经过那条路径。
        SetAmount(current, silent: true);

        // 从这里开始，"Amount 被改" 才可能是外部改动（见 AfterPowerAmountChanged）。
        _syncedFromData = true;

        InvokeDisplayAmountChanged();
    }

    /// <summary>
    /// 回合开始收尾：关掉「无限」开的"弹药可以超过上限"开关
    /// （用户口径："本回合内"= 开关只活一个回合），并把超出的弹药夹回上限。
    ///
    /// <para>放在这里而不是回合结束钩子上：本 Power 是弹药系统的常驻容器
    /// （<see cref="AmmoCmd.EnsurePower"/> 建的），生命周期与弹药完全同源，
    /// 少一个"谁负责收尾"的悬空约定。</para>
    ///
    /// <para>⚠️ 必须判 <c>Owner.Player ?? Owner.PetOwner</c>：这个钩子是全局广播，
    /// 场上每一只宠物/敌人也会派发（见 <c>AmmoPower.SyncFromData</c> 那条坑）。</para>
    /// </summary>
    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if ((base.Owner?.Player ?? base.Owner?.PetOwner) == player)
        {
            AmmoCmd.EndOverCapTurn(player);

            // 「灵异录像带」的"本回合手牌东倒西歪"只活一个回合 —— 新回合把所有牌的角度还回去。
            RayMod.Art.CardFieldVfx.ClearTapeHand();
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 把<b>外部</b>对 <c>Amount</c> 的改动<b>回写成真实弹药</b>（2026-10-04 补）。
    ///
    /// <para>与 <c>DepthPower.AfterPowerAmountChanged</c>（2026-10-04 00:06 加的那条）
    /// <b>完全同构</b> —— 层数那边修好了，弹药这边当时漏了。</para>
    ///
    /// <para><b>为什么需要它</b>：真实弹药在跑团级存档 <see cref="AmmoState"/> 里，
    /// <c>Amount</c> 只是<b>显示镜像</b>。原版开发者控制台的
    /// <c>power &lt;id&gt; &lt;amount&gt; &lt;target-index&gt;</c> 对<b>已存在</b>的 Power 走的是
    /// <c>PowerCmd.ModifyAmount</c>（<c>ApplyPowerConsoleCmd</c>，见坑 36）——
    /// 于是角标能显示 20，而所有真正读弹药的牌（「狙击」「霰弹枪」「以弹为盾」…）
    /// 读到的还是 3，看起来像"控制台没用"。</para>
    ///
    /// <para><b>用法</b>：<c>power RAY_MOD_POWER_AMMO_POWER 20 0</c>
    /// （值是"原值 + n"）。⚠️ 它<b>仍然会被上限夹住</b> ——
    /// 想测"超过上限"，先打出「无限」再输这条命令。</para>
    ///
    /// <para><b>⚠️⚠️ 必须先判 <see cref="_syncedFromData"/>（2026-10-04 用户实测踩到的坑）</b>：
    /// <c>PowerCmd.Apply</c> 施加**全新** Power 时，会在 <c>ApplyInternal</c>（<c>PowerCmd.cs:139</c>）
    /// 之后<b>立刻</b>派发本钩子（<c>:160</c>），此时 <c>Amount</c> 还是
    /// <b>出生值</b>（<c>AmmoCmd.EnsurePower</c> 传的 <c>1m</c>），而真实弹药刚被
    /// <c>ResetForCombat</c> 清成 <b>0</b> —— 于是这条回写会把真实弹药<b>改成 1</b>，
    /// 而且是在 <c>EnsurePower</c> 里那次 <c>SyncFromData(0, 7)</c> <b>之前</b>发生的，
    /// 结果：<b>每场战斗一开始弹药就是 1</b>（角标 1、悬停 1、日志
    /// <c>[AmmoPower] Amount 被外部改成 1（真实弹药 0）→ 回写成真实弹药</c>）。
    /// 层数那边同一个钩子踩掉了「积累」带回来的层数（见 <c>DepthPower</c> 的同款守卫）。
    /// 所以只有"已经从数据源同步过一次"之后，外部改动才真的是外部改动。</para>
    ///
    /// <para><b>不会自我递归</b>：本 Power 的数值全程走 <see cref="SyncFromData"/> 里的
    /// <c>SetAmount</c>，而那条路<b>不会</b>触发本钩子（钩子只在 <c>PowerCmd.ModifyAmount</c>
    /// 与 <c>Apply</c> 里派发）。再加一道"显示值 == 真实弹药"的确认，双保险。</para>
    /// </summary>
    public override async Task AfterPowerAmountChanged(
        PlayerChoiceContext choiceContext, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        if (power != this)
        {
            return;
        }

        if (!_syncedFromData)
        {
            // 刚 Apply 出来的出生值（1），不是外部改动 —— 回写会把真实弹药踩成 1。
            AmmoRunData.Log.Info(
                $"[AmmoPower] 出生值 {base.Amount}（还没同步过数据源）→ 跳过回写");

            return;
        }

        if ((base.Owner?.Player ?? base.Owner?.PetOwner) is not Player player)
        {
            return;
        }

        int shown = (int)base.Amount;
        int real = AmmoCmd.GetAmmo(player);

        // 显示值 == 真实弹药：这次改动就是我们自己的同步造成的，不用管。
        if (shown == real)
        {
            return;
        }

        AmmoRunData.Log.Info($"[AmmoPower] Amount 被外部改成 {shown}（真实弹药 {real}）→ 回写成真实弹药");

        // SetAmmo 内部会夹上限（「无限」生效时上界是 int.MaxValue），并同步回显示。
        AmmoCmd.SetAmmo(player, shown);

        await Task.CompletedTask;
    }
}
