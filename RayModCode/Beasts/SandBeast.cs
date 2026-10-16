using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MinionLib.Minion;
using STS2RitsuLib.Interop.AutoRegistration;

namespace RayMod.Beasts;

/// <summary>
/// 沙地兽：场上的一只召唤物。
///
/// <para><b>"没有血量、不会受伤"是怎么做到的</b>（用户 2026-10-03 确认要出现在场上）：</para>
/// <list type="bullet">
/// <item>血量用 <see cref="BeastConstants.PlaceholderHp"/> 兜底，且 <see cref="IsHealthBarVisible"/> 为 false
/// —— 原版 <c>MonsterModel</c> 不接受非正数血量，做法照抄原版宠物 Byrdpip。</item>
/// <item>真正的不受伤靠 <see cref="ShouldAllowHitting"/> 返回 false：
/// 原版 <c>Creature.IsHittable</c> 与 <c>Creature.CanReceivePowers</c> 都查它，
/// 于是既不吃伤害也不吃 buff（<c>Creature.cs:286,309</c>）。</item>
/// <item>"敌人不会选中它"是原版<b>白送</b>的：敌人 move 的目标池是
/// <c>combatState.PlayerCreatures</c>，而它是 <c>Creatures.Where(c =&gt; c.IsPlayer)</c>
/// —— 宠物不算玩家（<c>MonsterModel.cs:443</c>、<c>CombatState.cs:75</c>）。
/// ⚠️ 例外：走 <c>GetOpponentsOf</c> 的群体效果会把宠物算进去，但上面的
/// <see cref="ShouldAllowHitting"/> 已经保证它不受伤。</item>
/// </list>
///
/// <para><b>每只兽自己的状态</b>（"我标记了谁"、活了几回合）存在实例字段里 ——
/// 每次 <c>AddMinion</c> 都会克隆出一个新的 mutable 实例，所以字段天然是"每只一份"。
/// <b>注意 <c>MonsterModel</c> 没有 PowerModel 那套 <c>GetInternalData</c>/<c>InitInternalData</c></b>，
/// 直接用私有字段即可。</para>
/// </summary>
[RegisterMonster]
public sealed class SandBeast : MinionModel
{
    /// <summary>
    /// 这只兽<b>持有</b>的印记（每个元素 = 它在某个敌人身上的一层）。
    ///
    /// <para>通常只有 1 个元素（"沙地兽被召唤后立即随机标记一个敌人"）。
    /// 但"满上限替换"时，新兽会<b>接管被替换者持有的全部印记</b>（层数原地不动，只是换了持有者），
    /// 然后自己再按基础规则随机挂一层 —— 所以替换后的新兽可能持有 2 个甚至更多。
    /// 用户 2026-10-03 明确：<b>召唤沙地兽就会附着层数，替换时印记总数必然 +1，不是零</b>。</para>
    ///
    /// <para>"直到沙地兽退场"就是靠这个列表实现的：兽退场时把它持有的每一层都摘掉。</para>
    ///
    /// <para><b>★★ 2026-10-07：这个字段<b>不能</b>用声明时初始化 + readonly —— 那会让全队共用同一个 List。</b>
    /// 游戏造活体走的是 <c>ModelDb.Monster&lt;T&gt;().ToMutable()</c>
    /// → <c>AbstractModel.MutableClone()</c> = <c>MemberwiseClone()</c>（<b>浅拷贝</b>）+ <c>DeepCloneFields()</c>
    /// （<c>AbstractModel.cs:173/175</c>），而基类的 <c>DeepCloneFields</c> 是<b>空实现</b>，
    /// <c>MonsterModel</c> 与 MinionLib 的 <c>MinionModel</c> 都没重写 ——
    /// 于是 <c>ModelDb</c> 里的本体和场上每一只沙地兽<b>共享同一个 List 实例</b>。
    /// 后果：任意一只退场时 <c>TakeMarkedEnemies()</c> 一次把<b>全队</b>的条目都取走并 <c>Clear()</c>，
    /// 逐条 −1 → 用户报的"<i>一只沙地兽退场，所有层数都跟着没</i>"。
    /// 修法见下面的 <see cref="AfterCloned"/>。</para>
    /// </summary>
    private List<Creature> _markedEnemies = new();

    /// <summary>被召唤后经历过的"自己的回合开始"次数（10 层起的持续回合数减益要用）。</summary>
    private int _turnsLived;

    public override int MinInitialHp => BeastConstants.Hp;

    public override int MaxInitialHp => BeastConstants.Hp;

    /// <summary>没有血量，所以不画血条。</summary>
    public override bool IsHealthBarVisible => false;

    /// <summary>
    /// 占位视觉：原版自带的兜底 creature 场景（一定存在），等美术时换掉。
    /// </summary>
    protected override string VisualsPath =>
        SceneHelper.GetScenePath(BeastConstants.PlaceholderVisualsPath);

    /// <summary>这只兽持有的印记（元素可能已死，配合 <see cref="HasLiveMark"/> 用）。</summary>
    public IReadOnlyList<Creature> MarkedEnemies => _markedEnemies;

    /// <summary>
    /// <b>★ 每只沙地兽必须拿到自己的印记列表</b>（2026-10-07 修的"一只退场、全场印记全没"）。
    ///
    /// <para>克隆靠 <c>MemberwiseClone()</c> 浅拷贝，所有引用字段（含 <c>List</c>）都是<b>共用同一实例</b>的。
    /// 所以这里必须重建一遍，否则 <c>ModelDb</c> 本体与全场所有沙地兽会互相串味。</para>
    ///
    /// <para>⚠️ 同类的引用字段以后再加，也要在这里一并重建（<c>_turnsLived</c> 是值类型，浅拷贝天然安全）。</para>
    /// </summary>
    protected override void AfterCloned()
    {
        base.AfterCloned();
        _markedEnemies = new List<Creature>();
    }

    /// <summary>是否还有落在活敌人身上的印记。</summary>
    public bool HasLiveMark
    {
        get
        {
            foreach (var enemy in _markedEnemies)
            {
                if (enemy.IsAlive)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>给这只兽挂上"它在某个敌人身上持有一层印记"（<b>不</b>去重，见下方说明）。</summary>
    internal void AddMarkedEnemy(Creature enemy)
    {
        // ⚠️ 2026-10-03 修正（做「沙地兽之心」时发现的老 bug）：这里**不能去重**。
        //    每个调用点与"实际挂上的层数"是严格 1:1 的（继承 N 层就调 N 次，新挂 1 层就调 1 次）。
        //    原先的 Contains 去重会把"同一只兽在同一敌人身上持有 2 层"记成 1 层，
        //    于是它退场时 RemoveMarkOf 只摘 1 层，剩下那层变成**永久孤儿印记**贴在敌人身上。
        //    触发条件：满上限替换时，新兽继承来的印记恰好又随机命中同一个敌人。
        _markedEnemies.Add(enemy);
        RefreshBadge();
    }

    /// <summary>
    /// ★ 2026-10-09：把"这只兽现在持有几层"刷到**脚下的徽章**上（用户要求"在沙地兽底下显示它提供多少层增伤"）。
    ///
    /// <para>数值就是 <see cref="_markedEnemies"/> 的条目数：每层让那个敌人受到的攻击伤害 +10%
    /// （「沙地兽与她」时 +12%），所以这个数字 = <b>它一共贡献了几层增伤</b>。</para>
    ///
    /// <para>节点不在（还没挂形象 / 已经退场）时静默跳过 —— 徽章纯粹是显示件，绝不能影响结算。</para>
    /// </summary>
    private void RefreshBadge()
    {
        try
        {
            BeastArt.RefreshMarkBadge(base.Creature, _markedEnemies.Count);
        }
        catch
        {
            // BeastArt 内部已经吞异常；这里再兜一层，保证任何情况下都不会打断效果结算。
        }
    }

    /// <summary>
    /// 把持有的印记<b>整体交出去</b>并清空自己（<b>不动层数</b>）。
    /// 只有"满上限替换"用：旧的层原地留下、改由新兽持有。
    /// 交出之后旧兽再退场时 <see cref="BeastCmd.RemoveMarkOf"/> 就什么都不会摘。
    /// </summary>
    internal List<Creature> TakeMarkedEnemies()
    {
        var taken = new List<Creature>(_markedEnemies);
        _markedEnemies.Clear();
        RefreshBadge();
        return taken;
    }

    /// <summary>
    /// 把持有的层数<b>整体改挂到</b> <paramref name="target"/> 名下（<b>条目数一个不变</b>）。
    /// 「沙地兽之心」把所有印记集中到一个敌人时用 —— 层数搬了家，
    /// 「由谁负责将来摘掉」这件事也必须跟着搬，否则兽退场时会去摘已经清空的旧敌人。
    /// </summary>
    /// <param name="target">新的记账对象。</param>
    internal void RetargetMarks(Creature target)
    {
        for (int i = 0; i < _markedEnemies.Count; i++)
        {
            _markedEnemies[i] = target;
        }
    }

    /// <summary>
    /// 丢掉"记在已死敌人身上"的条目，返回丢掉几个。
    /// 那些条目对应的印记早随敌人一起消失了，留着只会在退场时做无用的摘除。
    /// </summary>
    internal int DropDeadMarks()
    {
        int before = _markedEnemies.Count;
        _markedEnemies.RemoveAll(enemy => !enemy.IsAlive);

        if (_markedEnemies.Count != before)
        {
            RefreshBadge();
        }

        return before - _markedEnemies.Count;
    }

    /// <summary>
    /// 不受伤、也不吃 buff。参数是"正在被检查的那个生物"，
    /// 所以只挡自己，不挡别的生物（这个钩子会被派发给每一个战斗内模型）。
    /// </summary>
    public override bool ShouldAllowHitting(Creature creature) => creature != base.Creature;

    /// <summary>
    /// 10 层起的层数减益：召唤出来的沙地兽<b>只能持续 3 个回合</b>
    /// （<c>DepthConstants.MinionLifespanMinDepth</c> / <c>MinionLifespanAtDepth10</c>）。
    ///
    /// <para>数法：被召唤的那回合不算，之后每经历一次"自己的回合开始"记 1 回合，
    /// 记满 3 次就退场 —— 也就是它实际存在于召唤回合 + 之后 2 个回合。</para>
    ///
    /// <para>层数是<b>每次回合开始现查的</b>：层数掉回 10 以下就不再见效，
    /// 和"20 层起 -6HP 持续生效"的口径一致（阈值型，不是档位专属）。</para>
    /// </summary>
    public override async Task AfterPlayerTurnStart(PlayerChoiceContext choiceContext, Player player)
    {
        if (base.Creature is not { } self || self.PetOwner != player)
        {
            return;
        }

        // 「探照灯」：本回合豁免层数负面效果 —— 这一回合不计入寿命。
        // 放在最前面（连"层数够不够触发这条减益"都不判），因为对这只兽来说
        // "不受影响"就等于"这个回合没有过去"。
        if (Depth.DepthPenalty.IsSuppressed(player))
        {
            return;
        }

        if (Depth.DepthCmd.GetDepth(player) < Depth.DepthConstants.MinionLifespanMinDepth)
        {
            return;
        }

        _turnsLived++;

        if (_turnsLived < Depth.DepthConstants.MinionLifespanAtDepth10)
        {
            return;
        }

        BeastRunData.Log.Info(
            $"[沙地兽] 层数 {Depth.DepthCmd.GetDepth(player)} >= {Depth.DepthConstants.MinionLifespanMinDepth}，" +
            $"这只已经活了 {_turnsLived} 回合，退场");

        await BeastCmd.Retire(choiceContext, self);
    }

    /// <summary>
    /// 自己退场时把印记一并摘掉。
    ///
    /// <para><see cref="BeastCmd.Retire"/> 里已经先摘过一次了，这里是第二道保险
    /// （两条路径都走同一个幂等方法，重复调用无副作用）：怕的是将来有人
    /// 用别的方式杀掉沙地兽（比如某个效果直接 <c>Kill</c>），印记会变成孤儿。</para>
    /// </summary>
    public override async Task AfterDeath(
        PlayerChoiceContext choiceContext, Creature creature, bool wasRemovalPrevented, float deathAnimLength)
    {
        if (creature != base.Creature)
        {
            return;
        }

        await BeastCmd.RemoveMarkOf(choiceContext, this);

        // ★ 2026-10-09：**节点也一并收掉**。
        //   `Retire` 那条路已经收过一次（幂等，重复调用无副作用）；
        //   这里是第二道保险 —— 万一将来有人用别的方式杀掉沙地兽（不经 Retire），
        //   节点就会像这次一样永远留在场上（宠物这条路上游戏和 MinionLib 都不收节点）。
        BeastArt.RemoveArt(base.Creature);
    }
}
