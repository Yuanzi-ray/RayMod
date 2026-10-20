using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;

// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>「上膛」的螺旋进场</b>（2026-10-19，用户选了方案 b：真实螺旋弧旋进手牌位）。
///
/// <para><b>为什么不能直接改 <c>holder.Position</c></b>：手牌位移由
/// <c>NHandCardHolder.AnimPosition()</c> 驱动 —— 一个<b>指数 lerp 的 async 循环</b>
/// （<c>Position = Position.Lerp(_targetPosition, dt * 7)</c>），它每帧都在写 <c>Position</c>。
/// 我们若也每帧写 <c>Position</c>，就是两个写者抢同一个属性 ⇒ 抖动或瞬移，
/// 而且会绕开它内部那套 <c>Hitbox.SetEnabled</c> 与"到点吸附"逻辑。</para>
///
/// <para><b>本类的做法：不抢位置，只抢「目标」。</b>每帧把 holder 的私有字段
/// <c>_targetPosition</c> 改写成<b>螺旋曲线上的下一个点</b>，让游戏自己的
/// <c>AnimPosition</c> 去追。于是：</para>
/// <list type="bullet">
///   <item>缓动、收敛、Hitbox 全部由游戏负责，<b>行为不变</b>；</item>
///   <item>我们只是把一个"直线目标"替换成一条"螺旋目标轨迹"；</item>
///   <item><c>RefreshLayout</c> 随时可能重写目标（抽多张时每张都会触发）——
///         所以每帧都要<b>重读真实目标</b>，不能只在开始时抓一次。</item>
/// </list>
///
/// <para>⚠️ 结束时必须把目标<b>写回真实位置</b>，否则手牌会停在螺旋末端。</para>
/// </summary>
internal sealed partial class CardChamberSpiral : Node
{
    private static readonly Logger Log = new("RayMod.ChamberSpiral", LogType.Generic);

    // ───────────────────────── 调参 ─────────────────────────

    /// <summary>整段螺旋时长（秒）。</summary>
    private const float Duration = 0.54f;

    /// <summary>起始螺旋半径（像素）。半径为 0 时就是直线。</summary>
    private const float Radius = 110f;

    /// <summary>转几圈。<b>1.15 是刻意选的</b> —— 再多会晕，再少读不出"旋"。</summary>
    private const float Turns = 1.0f;

    /// <summary>起始相位（弧度）。留一点偏置，避免所有牌从同一个角度转进来。</summary>
    private const float PhaseJitter = 0.9f;

    // ───────────────────────── 状态 ─────────────────────────

    private Control _holder = null!;
    private Vector2 _start;
    private float _t;
    private float _phase;
    private bool _done;

    /// <summary>
    /// <b>登记时抓下来的 holder InstanceId</b>（不是每次现算）。
    ///
    /// <para>⚠️⚠️ 2026-10-20 修的<b>严重 bug</b>：以前 <see cref="Release"/> 的第一行是
    /// <code>if (_holder == null || !IsInstanceValid(_holder)) return;   // ← 在 Remove 之前！</code>
    /// holder 被销毁的那一刻走进来，<b>直接 return 了，`Driven` 里的 id 永远留在集合里</b>。
    /// 而 Godot 的 InstanceId <b>会被复用</b> —— 下一个拿到这个 id 的新 holder
    /// 一出生就被 <c>CardSpiralSuppressPatch</c> 判成"正在被螺旋驱动"，
    /// 它的 <c>AnimPosition</c> 从此一次都不跑 ⇒ <b>那张牌永远停在出生点</b>
    /// （抽牌时出生点在牌库里）—— 表现就是"有时候牌渲染不出来"。
    /// 所以：<b>id 必须在登记时就抓死，且 Remove 必须在任何 return 之前执行</b>。</para>
    /// </summary>
    private ulong _holderId;

    private static FieldInfo? _targetField;
    private static PropertyInfo? _targetProp;
    private static bool _resolved;

    private static int _spin;

    /// <summary>
    /// 正在被螺旋**直接驱动位置**的 holder（InstanceId → 驱动器）。
    /// 由 <c>Patches\CardReloadVfxPatch</c> 里的 <c>CardSpiralSuppressPatch</c> 查询：
    /// 在表里的 holder，它的 <c>AnimPosition</c> 会被跳过。
    ///
    /// <para>★ 值从"只存 id"改成"存驱动器本身"是为了<b>自愈</b>：前缀每次都会问一句
    /// <see cref="StillDriving"/>，发现驱动器已经死了就顺手把这条登记删掉，
    /// 这样即使哪天又出现"没人来删"的路径，也不会永久毒死一个复用的 id。</para>
    /// </summary>
    internal static readonly Dictionary<ulong, CardChamberSpiral> Driven = new();

    /// <summary>这张螺旋是否还在驱动它的 holder（给前缀做活性校验）。</summary>
    internal bool StillDriving()
    {
        try
        {
            return !_done
                   && _holder != null
                   && GodotObject.IsInstanceValid(_holder)
                   && _holder.IsInsideTree();
        }
        catch
        {
            return false;
        }
    }

    private static PropertyInfo? _hitboxProp;
    private static MethodInfo? _hitboxEnable;
    private static MethodInfo? _setTargetPos;

    /// <summary>挂一个螺旋驱动器到 <paramref name="holder"/> 上。</summary>
    /// <param name="startLocal">起点（holder 父节点的局部坐标 —— 和 <c>Position</c> 同一套坐标）。</param>
    /// <param name="targetLocal">这一张的落点（手牌位），游戏设好的那个值。</param>
    internal static void Begin(Control holder, Vector2 startLocal, Vector2 targetLocal)
    {
        try
        {
            if (holder == null || !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree())
            {
                return;
            }

            ResolveMembers(holder);

            if (_targetField == null)
            {
                return;      // 拿不到私有字段 ⇒ 什么也不做（卡牌按原版直线 lerp 进来）
            }

            var sp = new CardChamberSpiral
            {
                _holder = holder,
                _start = startLocal,
                _phase = (_spin++ % 2 == 0 ? 1f : -1f) * PhaseJitter,
                _real = targetLocal,
            };

            // 挂在手牌容器上（和 holder 同父），这样坐标与 holder.Position 一致
            Node? parent = holder.GetParent();

            if (parent == null || !GodotObject.IsInstanceValid(parent))
            {
                return;
            }

            parent.AddChild(sp);
            sp.SetProcess(true);

            // ★ 登记：从这一刻起，这张 holder 的 AnimPosition 会被 Prefix 跳过，
            //   位置完全由本类写（否则低通会把螺旋滤平，见 Patches 里那段注释）。
            //   id 当场抓死存进 _holderId —— Release / _ExitTree 都只认这个值，
            //   绝不再现算（现算的时机正是"holder 已经没了"的时候）。
            sp._holderId = holder.GetInstanceId();
            Driven[sp._holderId] = sp;
            Log.Info($"[上膛螺旋] 启动（起点 {startLocal} → 落点 {targetLocal}，半径 {Radius:F0}，{Turns:F2} 圈，{Duration:F2}s）");
        }
        catch (Exception e)
        {
            Log.Warn($"[上膛螺旋] 启动失败（这张牌按直线进场）：{e.GetType().Name}: {e.Message}");
        }
    }

    public override void _Process(double delta)
    {
        try
        {
            if (_holder == null || !GodotObject.IsInstanceValid(_holder) || !_holder.IsInsideTree())
            {
                Release();
                QueueFree();
                return;
            }

            float dt = Mathf.Min((float)delta, 0.05f);
            _t += dt;

            // ★ 每帧重读真实目标：RefreshLayout 在"抽多张"时会给每张牌重设目标，
            //   只在开头抓一次的话，后面几张的螺旋会朝一个已经作废的目标旋。
            Vector2 real = RealTarget();

            float k = Mathf.Clamp(_t / Duration, 0f, 1f);

            if (k >= 1f)
            {
                // 收尾：落到真实位置 + 补上 AnimPosition 本该做的 Hitbox 启用，
                //       然后把控制权交回游戏（移除登记 + SetTargetPosition）。
                _holder.Position = real;
                Release();
                QueueFree();
                return;
            }

            // ★★ 直接写 Position。**这里不能再走 `_targetPosition` 那条路** ——
            //    AnimPosition 是个 7/s 的一阶低通，会把螺旋的横向摆动整个滤平。
            _holder.Position = SpiralPoint(real, k);
        }
        catch
        {
            Release();
            QueueFree();
        }
    }

    /// <summary>
    /// 交还控制权：移出 <see cref="Driven"/>、把 Hitbox 打开、
    /// 再让游戏重新起一次 <c>AnimPosition</c>（否则这张牌从此没人管位置了）。
    /// </summary>
    private void Release()
    {
        // ⚠️⚠️ 解除登记必须**排在所有 return 之前** —— 见 _holderId 的注释。
        //   以前这里先判 holder 有效、无效就 return，结果 id 永久留在 Driven 里，
        //   被复用的那一刻毒死下一个 holder（牌永远停在出生点）。
        _done = true;

        ulong id = _holderId;
        _holderId = 0UL;

        if (id != 0UL)
        {
            Driven.Remove(id);
        }

        try
        {
            if (_holder == null || !GodotObject.IsInstanceValid(_holder))
            {
                return;
            }

            // Hitbox：AnimPosition 正常跑的时候会把它打开，我们跳过它就得自己补
            try
            {
                if (_hitboxProp == null)
                {
                    _hitboxProp = _holder.GetType().GetProperty("Hitbox");
                }

                object? hitbox = _hitboxProp?.GetValue(_holder);

                if (hitbox != null)
                {
                    _hitboxEnable ??= hitbox.GetType().GetMethod("SetEnabled", new[] { typeof(bool) });
                    _hitboxEnable?.Invoke(hitbox, new object[] { true });
                }
            }
            catch
            {
                // 打不开就算了，最坏是这张牌暂时点不到
            }

            // 把位置补到真实落点，并让游戏重新接管
            Vector2 real = RealTarget();
            _holder.Position = real;

            try
            {
                _setTargetPos ??= _holder.GetType().GetMethod("SetTargetPosition", new[] { typeof(Vector2) });
                _setTargetPos?.Invoke(_holder, new object[] { real });
            }
            catch
            {
                // 交还失败也只是这张牌位置不再更新
            }
        }
        catch
        {
            // 绝不外抛
        }
    }

    public override void _ExitTree()
    {
        // 兜底：无论怎么退出，都必须解除登记（否则这张牌的位置永远没人写）
        _done = true;

        if (_holderId != 0UL)
        {
            Driven.Remove(_holderId);
            _holderId = 0UL;
        }
    }

    /// <summary>
    /// 螺旋上的点。
    ///
    /// <para><b>★ 2026-10-19 第三轮重写</b>（用户反馈「螺旋进入手牌也怪怪的，我们可能需要更好的曲线」）。
    /// 上一版是：
    /// <code>
    /// straight = Lerp(start, real, OutCubic(k));        // 直线用 OutCubic
    /// radius   = Radius * (1f - k);                     // 半径线性衰减
    /// angle    = phase + Turns * Tau * k;               // 匀速转
    /// </code>
    /// 三个问题，每个都会让人看出「怪」：
    /// <list type="number">
    ///   <item><b>起手瞬移</b>：k=0 时 straight 还在起点，但 radius 已经是<b>满值 165px</b>，
    ///     于是第一帧的位置 = 起点 + 165px 的偏移 —— 卡牌凭空跳到旁边，
    ///     再开始走。这是"怪"最主要的一笔。</item>
    ///   <item><b>末段原地抖</b>：OutCubic 在 k=0.5 时已经走完 87.5%，
    ///     可半径还有 82px、还在以匀速转圈 ⇒ 卡已经到位了却绕着槽位画圈，读成"手抖"。</item>
    ///   <item><b>匀速转</b>：真实世界里没有机械匀速，眼睛立刻觉得假。</item>
    /// </list></para>
    ///
    /// <para>现在：直线走 <see cref="FieldFxEase.InOutSine"/>，半径包络用 <c>sin²(πk)</c>，
    /// 转角保持线性 —— 半径包络在 k=0 和 k=1 处<b>值和导数都是 0</b>，
    /// 所以起手和落位都没有横向速度突变，卡是"从静止里被甩出去、又轻轻坐进槽里"。</para>
    /// </summary>
    private Vector2 SpiralPoint(Vector2 real, float k)
    {
        // 直线部分：两端速度都为 0（InOutSine），起手不生硬、落位不砸。
        Vector2 straight = _start.Lerp(real, FieldFxEase.InOutSine(k));

        // 半径包络 sin²(πk)：0 → 满值 → 0，且两端一阶导为 0。
        float bell = Mathf.Sin(Mathf.Pi * k);
        float radius = Radius * bell * bell;

        // 转角线性（匀速），半径那边负责"细—粗—细"，合起来是一个收得住口的旋。
        float angle = _phase + Turns * Mathf.Tau * k;

        return straight + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    // ───────────────────────── 反射 ─────────────────────────

    private static void ResolveMembers(Control holder)
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;

        try
        {
            Type t = holder.GetType();

            // 私有字段 _targetPosition（AnimPosition 追的就是它）
            for (Type? cur = t; cur != null; cur = cur.BaseType)
            {
                FieldInfo? f = cur.GetField(
                    "_targetPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (f != null)
                {
                    _targetField = f;
                    break;
                }
            }

            _targetProp = t.GetProperty("TargetPosition");   // 公开 getter，用来读"真实目标"
        }
        catch (Exception e)
        {
            Log.Warn($"[上膛螺旋] 反射失败：{e.GetType().Name}: {e.Message}");
        }
    }

    private Vector2 _real;          // 游戏设的真实目标（RefreshLayout 会改它）
    private Vector2 _lastWritten;   // 我上一次写进 _targetPosition 的值
    private bool _hasWritten;

    /// <summary>
    /// 读出**游戏设的真实目标**。
    ///
    /// <para>⚠️ 不能直接读 <c>_targetPosition</c> —— 那个字段我自己每帧都在写，
    /// 直接读会读回"我上一帧写的螺旋点"，于是螺旋会自己追自己、越转越小或干脆不动。
    /// 判据：如果当前值与"我刚写的值"不同，说明<b>是游戏改的</b>（<c>RefreshLayout</c>），
    /// 那就更新真实目标；否则保持原样。</para>
    /// </summary>
    private Vector2 RealTarget()
    {
        Vector2 cur = _targetProp?.GetValue(_holder) is Vector2 v ? v : _real;

        if (!_hasWritten || cur.DistanceTo(_lastWritten) > 0.5f)
        {
            _real = cur;
        }

        return _real;
    }

    private void WriteTarget(Vector2 value)
    {
        _targetField?.SetValue(_holder, value);
        _lastWritten = value;
        _hasWritten = true;
    }
}
