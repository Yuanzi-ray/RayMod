using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Interop.AutoRegistration;

using STS2RitsuLib.Scaffolding.Content;
using STS2RitsuLib.Scaffolding.Content.Patches;
namespace RayMod.Ammo;

/// <summary>
/// 「精制子弹」（初始卡）与「重构子弹」（先古卡）<b>共用</b>的载体：
/// 你的下 <c>Amount</c> 次攻击额外造成<b>相当于当前弹药数 × 系数</b>的伤害，每次结算后弹药 -1。
///
/// <para><b>两张牌的差别只有系数</b>：精制子弹 ×1，重构子弹 ×2（升级后）。
/// 所以系数做成了"每次用量各带一个"的队列，而<b>判据本体只有这一份</b>
/// —— 坑 31 / 坑 52 的同源原则：别为了换个系数复制一遍判断逻辑。</para>
///
/// <para><b>本类是照抄原版 <c>VigorPower</c> 写的</b>（<c>src\Core\Models\Powers\VigorPower.cs</c>），
/// 那正是"下一次攻击 +N 伤害然后消耗掉"这条机制的原版实现。照抄的地方：</para>
/// <list type="bullet">
/// <item><c>BeforeAttack</c> 里把"本次攻击"快照下来（<c>Data.CommandToModify</c>）——
/// 这样多段攻击的每一段都能吃到加成，而<b>别的一次攻击</b>吃不到。</item>
/// <item>三重守卫：攻击者必须是自己、必须是受加成的攻击、来源必须是卡牌。</item>
/// <item><c>AfterAttack</c> 里按快照判断"就是这次"，然后消耗。</item>
/// </list>
///
/// <para><b>与 Vigor 的差别</b>：Vigor 一次性把 Amount 全扣掉，本 Power 每次只扣 1
/// （升级后能用两次）。加成值也不是固定数，而是按<b>"这张牌开始结算前的弹药数"</b>算
/// （见 <see cref="Data.AmmoAtCardPlayStart"/>），
/// 所以"先打攻击牌、再打精制子弹"的次序会直接影响收益大小。</para>
///
/// <para><b>⚠️ 2026-10-04 两处口径修正（修"重构子弹对狙击不生效"）：</b></para>
/// <list type="number">
/// <item><b>加成基数</b>不再在伤害结算时现读弹药，改用<b>出牌前的快照</b>
/// —— 否则"先扣弹药再开火"的牌（狙击 / 霰弹枪 / 爆破）会读到 0 或被削过的值。</item>
/// <item><b>记账单位由 AttackCommand 改为"整张牌"</b> —— 否则一次狙击会连烧 7 次用量。</item>
/// </list>
/// <para>两处的详细理由分别写在 <see cref="Data.AmmoAtCardPlayStart"/> 与
/// <see cref="Data.BonusConsumedThisCard"/> 上。</para>
/// </summary>
[RegisterPower]
public sealed class RefinedBulletPower : PowerModel, IModPowerAssetOverrides
{

    public PowerAssetProfile AssetProfile => RayMod.Art.PowerArt.Profile(nameof(RefinedBulletPower));

    public string? CustomIconPath => AssetProfile.IconPath;

    public string? CustomBigIconPath => AssetProfile.BigIconPath;

    /// <summary>本次攻击的上下文（照抄 VigorPower 的 Data）。</summary>
    private sealed class Data
    {
        public AttackCommand? CommandToModify;

        /// <summary>
        /// 还没用掉的每一次攻击<b>各自的系数</b>（队首 = 下一次攻击用的）。
        ///
        /// <para>为什么是队列而不是一个数字：精制子弹（×1）与重构子弹（×2）是<b>两张不同的牌</b>，
        /// 手牌里可能同时各有一张。一个数字会变成"后打的那张决定全部剩余次数"，
        /// 队列才能做到"每次用量按打出它的那张牌算"。</para>
        ///
        /// <para>⚠️ 放在 <c>InitInternalData</c> 的数据里而不是普通字段：<c>PowerModel.DeepCloneFields</c>
        /// 会 <c>_internalData = InitInternalData()</c>，普通字段走 <c>MemberwiseClone</c> 是<b>浅拷贝</b>
        /// —— 队列会和规范实例（ModelDb 里的那个）共享同一个 List，跨战斗串味。
        /// 槽位为空时按 ×1 兜底（控制台改过 Amount 之类的外部改动只会退化成旧行为，不会出错）。</para>
        /// </summary>
        public readonly List<int> Multipliers = [];

        /// <summary>
        /// <b>本张牌开始结算前</b>（<c>BeforeCardPlayed</c>，早于 <c>OnPlay</c>）的弹药数快照。
        ///
        /// <para>⚠️ <b>2026-10-04 新增，修的是"重构子弹对狙击不生效"</b>：
        /// 加成原本是<b>在伤害结算那一刻现读</b>弹药，而这假设了"牌不会先花掉弹药"。
        /// 但「狙击」「霰弹枪」是在 <c>OnPlay</c> 里<b>先把弹药扣光再开火</b>
        /// （<c>Snipe.cs:56 ConsumeAllAmmo</c> → 之后才 <c>DamageCmd.Attack</c>），
        /// 「爆破」也是先付 5 点弹药再打 —— 于是加成读到的是 0（或被削过的值）。
        /// 有了这张快照，基数就与"牌什么时候扣弹药"解耦。</para>
        ///
        /// <para>取数规则见 <see cref="BonusBase"/>（快照与当前值取<b>较大者</b>）。</para>
        ///
        /// <para>⚠️ <b>已知残留边界（未修，2026-10-04）</b>：若玩家同时挂着「子弹模式」
        /// （<c>BulletModePower</c>，它<b>也在</b> <c>BeforeCardPlayed</c> 里扣弹药），
        /// 两个 Power 的钩子<b>顺序不确定</b>（<c>EngrossedPower</c> 的类注释同样警告过这一点）——
        /// 万一子弹模式先跑，本快照拍到的就是"已经被它扣过"的值，基数偏低。
        ///
        /// <para><b>但这不比改动前更糟</b>：旧实现是在伤害结算时现读弹药，那时子弹模式早就扣完了，
        /// 一样偏低。本快照修掉的是<b>"出牌过程中</b>自己扣弹药"这一类（狙击/霰弹枪/爆破）。
        /// 要彻底覆盖跨 Power 的顺序问题，得把"本张牌花掉的弹药"加成基数
        /// （例如改用 <c>AmmoEvents</c> 记录本张牌的扣减额），属于另一个改动。</para>
        /// </summary>
        public int AmmoAtCardPlayStart;

        /// <summary>
        /// 本张牌是否<b>真的吃到过加成</b>（由 <c>AfterAttack</c> 在确认真有加成后置上）。
        /// <c>AfterCardPlayed</c> 据此决定要不要扣掉一次用量。
        ///
        /// <para>⚠️ <b>为什么用"整张牌"而不是"每个 AttackCommand"记账</b>（2026-10-04 改）：
        /// 「狙击」一次打出会发 <b>7 个独立</b> <c>DamageCmd.Attack</c>
        /// （每段随机目标、逐段算伤害，见 <c>Snipe</c> 的注释）。
        /// 按命令记账时它会<b>连烧 7 次用量</b>，而「重构子弹」只有 2 次 ——
        /// 前 2 段就烧空，后 5 段一点加成都没有。
        /// 改成按牌记账后，整张狙击<b>只算 1 次用量</b>、7 段全部吃加成
        /// （用户 2026-10-04 拍板），这也和 <c>SandBeastQuotaPower</c> 的既有口径一致
        /// （"一张牌只扣 1 点，多段/群体都只算一张牌"）。</para>
        ///
        /// <para>⚠️ 置位动作放在 <c>AfterAttack</c> 而<b>不是</b> <c>ModifyDamageAdditive</c>：
        /// 后者的取值路径可能被<b>卡面预览</b>调用，在那里置位会把预览也算成一次真实消耗。</para>
        /// </summary>
        public bool BonusConsumedThisCard;
    }

    public override PowerType Type => PowerType.Buff;

    /// <summary>Counter：角标显示"还能生效几次"。</summary>
    public override PowerStackType StackType => PowerStackType.Counter;

    protected override object InitInternalData() => new Data();

    /// <summary>
    /// 给玩家挂 <paramref name="uses"/> 次"额外造成 弹药 × <paramref name="multiplier"/> 伤害"。
    /// <b>两张牌的唯一入口</b>（Amount 与系数队列在这里一起走，不能只动一边）。
    /// </summary>
    public static async Task AddUses(
        PlayerChoiceContext choiceContext, Creature owner, CardModel cardSource,
        int uses, int multiplier)
    {
        if (uses <= 0)
        {
            return;
        }

        // 已经挂着就累加（PowerCmd.Apply 内部走 ModifyAmount），没有就新建。
        var power = await PowerCmd.Apply<RefinedBulletPower>(
            choiceContext, owner, uses, applier: owner, cardSource: cardSource);

        if (power == null)
        {
            return;
        }

        var data = power.GetInternalData<Data>();

        for (int i = 0; i < uses; i++)
        {
            data.Multipliers.Add(Math.Max(1, multiplier));
        }
    }

    /// <summary>
    /// 本 Power 挂在玩家生物上。玩家 Creature 的 <c>PetOwner</c> 恒为 null，必须带上 <c>Player</c>
    /// （同 <c>AmmoPower.SyncFromData</c> 那条坑）。
    /// </summary>
    private Player? ResolveOwner() => base.Owner?.Player ?? base.Owner?.PetOwner;

    /// <summary>
    /// 出牌前（<b>早于 <c>OnPlay</c></b>）：拍下弹药快照、清掉上一次的记账标记。
    ///
    /// <para>⚠️ <b>快照必须在这里拍，不能挪到 <c>BeforeAttack</c></b>：
    /// 狙击是在 <c>OnPlay</c> 里就把弹药扣光的，等攻击开始时再拍已经是 0 了 ——
    /// 那正是"重构子弹对狙击不生效"的根因。</para>
    ///
    /// <para>⚠️ <c>BeforeCardPlayed</c> 是<b>全局广播</b>（宠物/敌人打牌也会派发，同
    /// <c>LoadingPosturePower</c> 的注释），所以要先认出"这是不是我自己打的牌"。</para>
    /// </summary>
    public override Task BeforeCardPlayed(CardPlay cardPlay)
    {
        if (ResolveOwner() is not { } owner || cardPlay.Card?.Owner != owner)
        {
            return Task.CompletedTask;
        }

        var data = GetInternalData<Data>();

        data.AmmoAtCardPlayStart = AmmoCmd.GetAmmo(owner);
        data.BonusConsumedThisCard = false;
        data.CommandToModify = null;

        return Task.CompletedTask;
    }

    public override Task BeforeAttack(AttackCommand command)
    {
        if (command.Attacker != base.Owner)
        {
            return Task.CompletedTask;
        }

        if (!command.DamageProps.IsPoweredAttack())
        {
            return Task.CompletedTask;
        }

        var data = GetInternalData<Data>();

        // 已经认领了这次攻击（多段攻击会多次进 BeforeAttack），不重复认领。
        if (data.CommandToModify != null)
        {
            return Task.CompletedTask;
        }

        // 只对"从卡牌打出来的"攻击生效（同 VigorPower）。
        if (command.ModelSource != null && command.ModelSource is not CardModel)
        {
            return Task.CompletedTask;
        }

        data.CommandToModify = command;
        return Task.CompletedTask;
    }

    public override decimal ModifyDamageAdditive(
        Creature? target, decimal amount, ValueProp props, Creature? dealer,
        CardModel? cardSource, CardPlay? cardPlay)
    {
        if (base.Owner != dealer)
        {
            return 0m;
        }

        if (!props.IsPoweredAttack())
        {
            return 0m;
        }

        var data = GetInternalData<Data>();

        if (data.CommandToModify != null && cardSource != null && cardSource != data.CommandToModify.ModelSource)
        {
            return 0m;
        }

        if (data.CommandToModify != null && data.CommandToModify.Attacker != dealer)
        {
            return 0m;
        }

        // 加成 = 弹药基数 × 本次用量的系数（精制子弹 1、重构子弹 2）。
        //
        // ⚠️ 2026-10-04：基数不再是"此刻的弹药"（见 BonusBase 与 Data.AmmoAtCardPlayStart）。
        // ⚠️ 这里**刻意不置** BonusConsumedThisCard：本方法的取值路径可能被**卡面预览**调用
        //    （Hook.ModifyDamage 带 CardPreviewMode），在这里记账会把预览算成一次真实消耗。
        //    记账放在 AfterAttack（只有真实攻击才会走到）。
        return ResolveOwner() is { } owner
            ? BonusBase(data, owner) * CurrentMultiplier(data)
            : 0m;
    }

    /// <summary>本次用量的系数（队列空 = 兜底 ×1）。</summary>
    private static int CurrentMultiplier(Data data) =>
        data.Multipliers.Count > 0 ? data.Multipliers[0] : 1;

    /// <summary>
    /// 加成的弹药基数 = <b>出牌前快照</b>与<b>当前值</b>取<b>较大者</b>。
    ///
    /// <para>用 <c>Max</c> 而不是直接用快照，是为了两头都不吃亏：</para>
    /// <list type="bullet">
    /// <item>牌<b>自己先花了弹药</b>再打（狙击 / 霰弹枪 / 爆破）→ 当前值被削，取<b>快照</b>。</item>
    /// <item>牌<b>自己先加了弹药</b>再打（先装弹后射击那类）→ 当前值更大，取<b>当前值</b>。</item>
    /// </list>
    ///
    /// <para>唯一入口 —— <see cref="ModifyDamageAdditive"/> 与 <c>AfterAttack</c> 都调它，
    /// 避免"算加成"和"判断有没有加成"两处口径走偏。</para>
    /// </summary>
    private static int BonusBase(Data data, Player player) =>
        Math.Max(data.AmmoAtCardPlayStart, AmmoCmd.GetAmmo(player));

    /// <summary>
    /// 攻击结算完：只做"记账"，<b>不</b>扣用量。
    ///
    /// <para>⚠️ 2026-10-04 起扣用量挪到了 <see cref="AfterCardPlayed"/>：一次狙击会发 7 个
    /// AttackCommand，在这里扣就会连烧 7 次。本方法只负责"这次攻击真的吃到了加成吗"。</para>
    /// </summary>
    public override Task AfterAttack(PlayerChoiceContext choiceContext, AttackCommand command)
    {
        var data = GetInternalData<Data>();

        if (command != data.CommandToModify)
        {
            return Task.CompletedTask;
        }

        data.CommandToModify = null;

        // 只有**真有加成**（基数 > 0）才记账 ——
        // 弹药为 0 时打出的攻击牌不该白吃一次用量。
        if (ResolveOwner() is { } owner && BonusBase(data, owner) > 0)
        {
            data.BonusConsumedThisCard = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 出牌结束：本牌真的吃到过加成的话，扣掉<b>一次</b>用量
    /// （系数出队 + 弹药 -1 + Amount -1，三件事必须一起做）。
    ///
    /// <para><b>"一张牌 = 一次用量"的口径由这个钩子统一收口</b>（2026-10-04 改）。
    /// 拆回 <c>AfterAttack</c> 里做就会退化成"按 AttackCommand 记账"，
    /// 一次狙击直接烧空用量 —— 那正是要修的 bug。</para>
    ///
    /// <para>⚠️ 时机不能提前到 <c>BeforeCardPlayed</c>（同 <c>SandBeastQuotaPower</c> 的警告）：
    /// 一张牌的伤害是在它的 <c>OnPlay</c> 里算的，放前面会让当前这张牌自己吃不到加成。</para>
    ///
    /// <para>⚠️ <c>AfterCardPlayed</c> 同样是全局广播，要认出自己的牌。</para>
    /// </summary>
    public override async Task AfterCardPlayed(PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        var data = GetInternalData<Data>();

        if (ResolveOwner() is not { } owner || cardPlay.Card?.Owner != owner)
        {
            return;
        }

        data.CommandToModify = null;

        // 没吃到加成就不扣 —— 弹药为 0 时打出的攻击牌不该被白吃一次。
        if (!data.BonusConsumedThisCard)
        {
            return;
        }

        data.BonusConsumedThisCard = false;

        // 用掉的那一次把系数也吃掉（与下面的 Amount -1 一一对应）。
        if (data.Multipliers.Count > 0)
        {
            data.Multipliers.RemoveAt(0);
        }

        // 弹药 -1。弹药不足时 ConsumeAmmo 不会扣成负数（返回实际消耗量）。
        AmmoCmd.ConsumeAmmo(owner, 1);

        // 用掉一次（归零时 PowerCmd.ModifyAmount 会自动移除这个 Power）。
        await PowerCmd.ModifyAmount(choiceContext, this, -1m, applier: null, cardSource: null);
    }
}
