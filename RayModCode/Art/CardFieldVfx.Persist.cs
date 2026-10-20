using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using RayMod.Hud;
using RayMod.Powers;

namespace RayMod.Art;

/// <summary>
/// ★★ 2026-10-20 <b>「SL 一次之后特效全消失」的修法</b> —— 用户原话：
/// <i>"还有那个问题：sl一次后特效消失"</i>。
///
/// <para><b>根因（读代码得到，不是猜的）</b>：本项目所有<b>常驻</b>表现件都是
/// 「能力被施加」那一刻挂上去的 ——
/// <see cref="AttachAmmoBelt"/> / <see cref="AttachReloadAura"/> 挂在
/// <c>LoadingPosturePower.AfterApplied</c>，<see cref="AttachGiantEye"/> 挂在
/// <c>FeranmutsPower.AfterApplied</c>，<see cref="AttachBulletSwarm"/> 挂在
/// <c>BulletModePower.AfterApplied</c>。
/// 而 <c>AfterApplied</c> 在整个游戏里<b>只有一个调用点</b>：
/// <c>MegaCrit.Sts2.Core.Commands\PowerCmd.cs:159</c>（施加能力的那条命令）。
/// 存档是<b>直接反序列化出能力列表</b>的，<b>不走 PowerCmd</b> ⇒
/// SL 之后 <c>AfterApplied</c> 一次都不会被调 ⇒ 弹链 / 绕体光环 / 巨眼 / 弹幕
/// 全都没有了（玩家只能再打一次对应卡才会回来）。</para>
///
/// <para><b>更隐蔽的第二层</b>：<c>_reloadOwner</c> / <c>_persistBound</c> 这些静态字段
/// 存的是<b>旧的 Player 实例</b>。SL 之后游戏给了新的 Player 对象，
/// 而 <see cref="ReloadLive"/> 判的是"这个 owner 身上有没有能力" ⇒
/// <b>旧 owner 上永远没有</b> ⇒ 填装形态的卡牌动效（退壳 / 上膛 / 壳层）也一起静默失效。
/// 光把节点补挂回来不够，<b>必须重新认人</b>。</para>
///
/// <para><b>修法</b>：一个挂在 <c>SceneTree.Root</c> 上的心跳节点（换房间 / SL 都回收不到它），
/// 每 0.5 秒看一眼<b>当前</b>本地玩家身上有哪些能力，缺什么补什么、多了什么收什么。
/// 各 <c>Attach*</c> 函数本来就写了 <c>Reconfigure(owner)</c> 复用分支
/// （<c>BulletMode</c> 的注释甚至明说"SL / 重开后重新认人"）—— 它们缺的只是<b>被再调一次</b>。</para>
///
/// <para>⚠️ 心跳**只在"节点没了 / 换了人"时才调 Attach**，不是无脑每帧调 ——
/// <c>Reconfigure</c> 本身很轻（改字段），但没必要每秒改两遍。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>心跳间隔（秒）。0.5s 够快，肉眼不会觉得"迟了一块"。</summary>
    private const ulong PersistIntervalMs = 500;

    private static Node? _persistBeat;

    /// <summary>上一次把常驻件认给哪个 Player —— SL 之后它一定是旧对象。</summary>
    private static Player? _persistBound;

    private static ulong _persistNextMs;
    private static bool _persistRebindLogged;

    // ══════════════════════════════════════════════════════════════════════
    //  心跳节点的安装
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把心跳挂到 <c>SceneTree.Root</c>（根窗口）。幂等，重复调用安全。
    /// <para>挂根窗口而不是战斗容器：<c>CombatVfxContainer</c> 会随房间销毁，
    /// 而 SL 恰恰要销毁它 —— 挂在里面的心跳会在最需要它的那一刻一起消失。</para>
    /// </summary>
    internal static void EnsurePersistBeat()
    {
        try
        {
            if (IsAlive(_persistBeat))
            {
                return;
            }

            if (Engine.GetMainLoop() is not SceneTree tree)
            {
                return;
            }

            var beat = new PersistBeat { Name = "RayModPersistBeat" };
            tree.Root.AddChild(beat);
            beat.SetProcess(true);   // ★ 显式开启（巨眼那轮踩过"重写了 _Process 却不开"的坑）
            _persistBeat = beat;
        }
        catch (Exception e)
        {
            Log.Info($"[常驻心跳] 安装失败（不影响已挂上的特效）：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>心跳节点：每 <see cref="PersistIntervalMs"/> 毫秒跑一次对账。</summary>
    private sealed partial class PersistBeat : Node
    {
        public override void _Process(double delta)
        {
            try
            {
                ulong now = Time.GetTicksMsec();

                if (now < _persistNextMs)
                {
                    return;
                }

                _persistNextMs = now + PersistIntervalMs;
                CardFieldVfx.PersistTick();
            }
            catch
            {
                // 表现层绝不外抛（工程铁律）。
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  对账本体
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 看一眼当前本地玩家，把四个常驻件对齐到"它身上现在真有的能力"。
    /// <para>门控顺序按便宜到贵：<c>Enabled</c>（纯字段）→ 取本地玩家 → 三个能力查询。</para>
    /// </summary>
    internal static void PersistTick()
    {
        try
        {
            if (!Enabled)
            {
                // 设置里把总开关关了：连已经挂上的也收掉（和其它效果的语义一致）。
                RetireAllPersistent();
                return;
            }

            if (ResourceHud.LocalPlayerOrNull() is not Player me)
            {
                // 不在 run 里 / 主菜单：什么都不做，别把上一局的遗物收掉又挂回来。
                return;
            }

            bool ownerChanged = !ReferenceEquals(_persistBound, me);

            bool loading = HasPower<LoadingPosturePower>(me);
            bool feranmuts = HasPower<FeranmutsPower>(me);
            bool bullet = HasPower<BulletModePower>(me);

            // ── 填装形态：弹链 + 绕体光环 ──
            if (loading)
            {
                if (ownerChanged || !IsAlive(_ammoBelt))
                {
                    AttachAmmoBelt(me);
                }

                if (ownerChanged || !IsAlive(_reloadAura))
                {
                    AttachReloadAura(me);
                }
            }
            else
            {
                if (IsAlive(_ammoBelt))
                {
                    RetireAmmoBelt();
                }

                if (IsAlive(_reloadAura))
                {
                    RetireReloadAura();
                }
            }

            // ── 巨兽：巨眼 ──
            if (feranmuts)
            {
                if (ownerChanged || !IsAlive(_giantEye))
                {
                    AttachGiantEye(me);
                }
            }
            else if (IsAlive(_giantEye))
            {
                RetireGiantEye();
            }

            // ── 子弹模式：弹幕 ──
            if (bullet)
            {
                if (ownerChanged || !IsAlive(_bulletSwarm))
                {
                    AttachBulletSwarm(me);
                }
            }
            else if (IsAlive(_bulletSwarm))
            {
                _bulletSwarm!.Retire();
            }

            // ── HUD 两块牌子：SL / 换房间后如果没了就补挂 ──
            try
            {
                if (!IsAlive(ResourceHud.Ammo.Root))
                {
                    ResourceHud.EnsureAttached();
                }
            }
            catch
            {
                // HUD 补挂失败与战斗表现无关，静默。
            }

            if (ownerChanged)
            {
                _persistBound = me;

                if (!_persistRebindLogged)
                {
                    _persistRebindLogged = true;
                    Log.Info($"[常驻心跳] 已对齐到当前玩家（填装={loading} 巨兽={feranmuts} 子弹模式={bullet}）");
                }
            }
        }
        catch
        {
            // 静默
        }
    }

    private static void RetireAllPersistent()
    {
        try
        {
            if (IsAlive(_ammoBelt))
            {
                RetireAmmoBelt();
            }

            if (IsAlive(_reloadAura))
            {
                RetireReloadAura();
            }

            if (IsAlive(_giantEye))
            {
                RetireGiantEye();
            }

            if (IsAlive(_bulletSwarm))
            {
                _bulletSwarm!.Retire();
            }
        }
        catch
        {
            // 静默
        }
    }

    /// <summary>
    /// 本地玩家身上有没有这个能力。
    /// <para>⚠️ 用 <c>try/catch</c> 包住而不是判空就完事：SL 之后旧 <c>Creature</c>
    /// 可能是已释放对象，访问它会抛 <c>ObjectDisposedException</c> /
    /// <c>InvalidOperationException</c> —— 那种情况等价于"没有"。</para>
    /// </summary>
    private static bool HasPower<TPower>(Player player)
        where TPower : MegaCrit.Sts2.Core.Models.PowerModel
    {
        try
        {
            return player.Creature != null
                   && player.Creature.GetPower<TPower>() != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>GodotObject.IsInstanceValid</c> 的防抛版本（已释放对象上它自己也可能炸）。</summary>
    private static bool IsAlive(GodotObject? node)
    {
        try
        {
            return node != null && GodotObject.IsInstanceValid(node);
        }
        catch
        {
            return false;
        }
    }
}
