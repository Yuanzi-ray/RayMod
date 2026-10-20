using System;
using System.Collections.Generic;
using Godot;

namespace RayMod.Art;

/// <summary>
/// <b>「灵异录像带」的场内表现</b>（用户 2026-10-20：选了这张牌，并追加
/// "并且更改本回合手牌（东倒西歪，鼠标掠过会晃动）"）。
///
/// <para>两件事：
/// <list type="number">
/// <item><b>本回合手牌东倒西歪</b>：每张牌额外挂一个 ±12° 的固定倾角（按实例 id 取，稳定不抖）。
///   实现方式<b>不是</b>直接写 <c>RotationDegrees</c> —— 那会被 <c>NHandCardHolder.AnimAngle</c>
///   每帧拉回 <c>_targetAngle</c>。正确做法是给它的<b>目标角</b>加偏移：
///   <c>SetTargetAngle(游戏给的角度 + 倾角 + 晃动)</c>。</item>
/// <item><b>鼠标掠过会晃动</b>：命中 <c>DoCardHoverEffects(true)</c> 之后 1.2 秒内，
///   目标角叠一个<b>衰减正弦</b>（±10°，约 3Hz），读作"磁带卡了一下又回正"。</item>
/// </list></para>
///
/// <para><b>为什么用"改目标角"而不是"改实际角"</b>：<c>AnimAngle</c> 是每帧
/// <c>RotationDegrees = Lerp(RotationDegrees, _targetAngle, dt*10)</c> 的一阶低通，
/// 并且贴近目标（0.1° 内）就 <c>return</c> 停止追击。直接改实际角会被下一帧抹掉；
/// 改目标角则是"喂给它一个新的终点"，游戏自己的补间会平滑跟过去。</para>
///
/// <para><b>转角什么时候还原</b>：<see cref="ClearTapeHand"/>（下一回合玩家回合开始时调，
/// 挂在 <c>AmmoPower.AfterPlayerTurnStart</c> 里）。手牌在回合结束就进弃牌堆了，
/// 新抽的牌是新 holder 实例（新 id），所以清掉标志后不会再被加倾角。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>本回合手牌"东倒西歪"是否生效。</summary>
    internal static bool TapeHandActive { get; private set; }

    /// <summary>游戏原来要求的倾角（每张牌的基准，倾角都是相对它的偏移）。</summary>
    private static readonly Dictionary<ulong, float> TapeBaseAngle = new();

    private static Vector2 _lastTapeMouse;
    private static float _lastTapeMoved;
    private static int _tapePollTick;

    /// <summary>当前判定为"鼠标在里面"的牌。</summary>
    private static readonly HashSet<ulong> TapeHoverInside = new();

    /// <summary>鼠标进入的毫秒时刻（没记录 = 当前没悬停）。</summary>
    private static readonly Dictionary<ulong, ulong> TapeHoverStart = new();

    /// <summary>记录过倾角的 holder（ticker 每帧扫这个表）。</summary>
    private static readonly List<Control> TapeHolders = new();

    /// <summary>游戏原来要求的落位（局部坐标，基准值；位移抖动在它之上叠加）。</summary>
    private static readonly Dictionary<ulong, Vector2> TapeBasePos = new();

    private static TapeHandTicker? _tapeTicker;

    /// <summary>同一张牌两次喂角度之间的最小间隔（毫秒）—— 别把 SetTargetAngle 打成 60Hz。</summary>
    private const ulong TapeApplyIntervalMs = 40;

    private static readonly Dictionary<ulong, ulong> TapeAppliedAt = new();

    /// <summary>打出这张牌时是第几回合 / 哪个战斗房间实例（用来判"只维持这一回合"）。</summary>
    private static int _tapeTurn;
    private static ulong _tapeRoomId;

    /// <summary>
    /// 正在被拖着的那张牌（由 <c>CardDragGhostPatch</c> 每帧登记）。
    /// <para><b>为什么要排除它</b>：拖拽时位置/角度由游戏跟着鼠标写，我们每 40ms 再把
    /// "基准落位 + 倾斜"喂回去，两边互相拉扯 ⇒ 用户看到的"把卡拖起来就不停抽搐"。</para>
    /// </summary>
    private static ulong _dragHolderId;
    private static ulong _dragHolderAt;

    /// <summary>登记"这张牌正在被拖"（拖拽每帧一次，松手后 250ms 自动失效）。</summary>
    internal static void NoteDragging(Control? holder)
    {
        try
        {
            if (holder == null)
            {
                return;
            }

            _dragHolderId = holder.GetInstanceId();
            _dragHolderAt = Time.GetTicksMsec();
        }
        catch
        {
            // 忽略
        }
    }

    private static bool IsDragging(ulong id)
        => id != 0UL && id == _dragHolderId && Time.GetTicksMsec() - _dragHolderAt < 250UL;

    /// <summary>开启"本回合手牌东倒西歪"。</summary>
    internal static void ArmTapeHand()
    {
        try
        {
            if (!Enabled)
            {
                return;
            }

            TapeHandActive = true;
            _tapeTurn = CurrentTurnNumber();
            _tapeRoomId = CurrentRoomId();
            EnsureTapeTicker();
            LogOnce("[录像带·手牌] 本回合手牌开始东倒西歪");
        }
        catch (Exception e)
        {
            Diag("tapearm", $"[录像带·手牌] 开启失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 只维持"打出的那一个回合"：回合数变了、或者战斗房间换了实例（含读档重建）就还原。
    /// <para><b>为什么不能只靠 <c>AmmoPower.AfterPlayerTurnStart</c></b>：读档不走那条钩子
    /// （存档直接反序列化状态），所以读档后手牌会一直歪着 —— 用户实测报的就是这个。</para>
    /// </summary>
    private static void CheckTapeExpiry()
    {
        if (!TapeHandActive)
        {
            return;
        }

        int turn = CurrentTurnNumber();
        ulong room = CurrentRoomId();

        if ((_tapeTurn > 0 && turn > 0 && turn != _tapeTurn) || (_tapeRoomId != 0UL && room != _tapeRoomId))
        {
            Diag("tapeexpire", $"[录像带·手牌] 到期：回合 {_tapeTurn}→{turn} 房间 {_tapeRoomId}→{room}", 6);
            ClearTapeHand();
        }
    }

    private static int CurrentTurnNumber()
    {
        try
        {
            if (Hud.ResourceHud.LocalPlayerOrNull() is not object player)
            {
                return 0;
            }

            object? combatState = player.GetType()
                .GetProperty("PlayerCombatState", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(player);

            return combatState?.GetType()
                .GetProperty("TurnNumber", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(combatState) is int n ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static ulong CurrentRoomId()
    {
        try
        {
            object? room = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom")
                ?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null);

            return room is GodotObject node && GodotObject.IsInstanceValid(node) ? node.GetInstanceId() : 0UL;
        }
        catch
        {
            return 0UL;
        }
    }

    /// <summary>还原（下一回合开始 / 战斗结束时调用）。</summary>
    internal static void ClearTapeHand()
    {
        try
        {
            // 墓碑和手牌同一个收场点：不管手牌这一刻是不是"歪着"，
            // 回合结束都要把碑收掉（所以放在 TapeHandActive 早退之前）。
            ClearTapeGraves();

            if (!TapeHandActive)
            {
                return;
            }

            TapeHandActive = false;

            Diag("tapeclear", $"[录像带·手牌] 还原（剩余 holder {TapeHolders.Count} 张）", 20);

            // 把每张还在场的牌送回它原本的角度
            foreach (Control? holder in TapeHolders)
            {
                if (IsAlive(holder) && TapeBaseAngle.TryGetValue(holder.GetInstanceId(), out float baseAngle))
                {
                    FeedAngle(holder, baseAngle);
                }
            }

            // 回合结束 ⇒ 鬼影一起收场（和手牌摆正同一个收场点）
            RetireTapeGhosts();

            TapeBaseAngle.Clear();
            TapeHoverStart.Clear();
            TapeAppliedAt.Clear();
            TapeHolders.Clear();
            TapeHoverInside.Clear();
        }
        catch (Exception e)
        {
            Diag("tapeclear", $"[录像带·手牌] 还原失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// 正在"喂角度"的标记。
    /// <para>⚠️ <b>必须的防回环</b>：<see cref="FeedAngle"/> 调的就是被补丁挂着的
    /// <c>SetTargetAngle</c>，如果不拦，补丁会把"已经加过倾角的角度"当成新的基准角存下来，
    /// 下一帧再叠一次 ⇒ 每帧 +12°，几秒后手牌会转成风车。</para>
    /// </summary>
    internal static bool TapeFeeding { get; private set; }

    /// <summary>补丁钩子：游戏调 <c>SetTargetAngle</c> 时记下基准角，并立刻叠上我们的偏移。</summary>
    internal static void NoteTapeHolder(Control holder, float angle)
    {
        if (TapeFeeding)
        {
            return;
        }

        try
        {
            ulong id = holder.GetInstanceId();

            Diag("tapencall", $"[录像带·手牌] SetTargetAngle 被调到 angle={angle:F1} active={TapeHandActive}", 6);

            TapeBaseAngle[id] = angle;

            // 游戏重新排布了手牌（角度又喂了一次）⇒ 落位基准可能过期，丢掉重测。
            TapeBasePos.Remove(id);

            if (!TapeHolders.Contains(holder))
            {
                TapeHolders.Add(holder);
            }

            if (!TapeHandActive || !Enabled)
            {
                return;
            }

            FeedAngle(holder, angle + TiltOf(id) + WobbleOf(id));

            Diag("tapenote", $"[录像带·手牌] 喂角度 base={angle:F1} tilt={TiltOf(id):F1} → {angle + TiltOf(id):F1}", 6);
        }
        catch
        {
            // 表现层失败绝不外抛
        }
    }

    /// <summary>补丁钩子：鼠标进/出手牌。</summary>
    internal static void NoteTapeHover(Control holder, bool hovered)
    {
        try
        {
            ulong id = holder.GetInstanceId();

            if (hovered)
            {
                TapeHoverStart[id] = Time.GetTicksMsec();
                Diag("tapehover", $"[录像带·手牌] 悬停进入 id={id} active={TapeHandActive}", 10);
            }
            else
            {
                TapeHoverStart.Remove(id);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 「鼠标掠过手牌会晃」—— 自己判，不靠游戏的回调。
    /// <para>为什么不用 <c>DoCardHoverEffects</c> 补丁：本机实测鼠标移过去它一次都不调
    /// （真机大概率也是走别的路径），而视口鼠标位置每帧可读 ⇒ 只要鼠标在屏幕下半区
    /// 还在动，就让手牌晃一下，行为上等价于"掠过"。</para>
    /// </summary>
    private static void PollHover(Control holder, ulong id)
    {
        try
        {
            if (holder.GetViewport() is not Viewport vp)
            {
                return;
            }

            Vector2 mouse = vp.GetMousePosition();
            ulong now = Time.GetTicksMsec();
            float moved = _lastTapeMouse.DistanceTo(mouse);
            int ticks = _tapePollTick++;
            bool inHandBand = mouse.Y > vp.GetVisibleRect().Size.Y * 0.55f;

            if (ticks % 5 == 0)
            {
                _lastTapeMouse = mouse;
                _lastTapeMoved = moved;
            }

            if (!inHandBand || _lastTapeMoved < 6f)
            {
                return;
            }

            if (TapeHoverStart.TryGetValue(id, out ulong start) && now - start < 650UL)
            {
                return;
            }

            TapeHoverStart[id] = now;
            TapeHoverInside.Add(id);
            Diag("tapehover", $"[录像带·手牌] 掠过晃动 id={id}", 3);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>每张牌固定的"歪"多少度。
    /// <para>用实例 id 做哈希 ⇒ 同一张牌一直歪同一个方向（不稳定的话看起来像在抽搐）。</para>
    /// </summary>
    private static float TiltOf(ulong id)
    {
        uint h = (uint)(id * 2654435761UL);
        float t = (h % 1000u) / 1000f;      // 0..1
        return (t * 2f - 1f) * 20f;         // ±20°（±12° 时和原版扇形分不开）
    }

    /// <summary>
    /// 每张牌固定的"错位"多少像素（东倒西歪的"倒"要靠位置，只转角度读起来还是扇形）。
    /// <para>用另一路哈希 ⇒ 和倾角不相关，不会所有牌往同一个方向歪。</para>
    /// </summary>
    private static Vector2 PosJitterOf(ulong id)
    {
        uint h = (uint)(id * 40503UL + 1013UL);
        float tx = (h % 997u) / 996f - 0.5f;           // -0.5..0.5
        float ty = ((h / 997u) % 991u) / 990f;         // 0..1

        return new Vector2(tx * 36f, ty * 34f);
    }

    /// <summary>悬停后的衰减正弦晃动（度）。</summary>
    private static float WobbleOf(ulong id)
    {
        if (!TapeHoverStart.TryGetValue(id, out ulong start) || start == 0UL)
        {
            return 0f;
        }

        float dt = (Time.GetTicksMsec() - start) / 1000f;

        const float life = 1.20f;

        if (dt >= life)
        {
            return 0f;
        }

        float decay = 1f - (dt / life);
        return Mathf.Sin(dt * 19f) * 10f * decay * decay;
    }

    private static void EnsureTapeTicker()
    {
        if (_tapeTicker != null && GodotObject.IsInstanceValid(_tapeTicker))
        {
            return;
        }

        try
        {
            if ((Engine.GetMainLoop() as SceneTree)?.Root is not Node root)
            {
                return;
            }

            _tapeTicker = new TapeHandTicker { Name = "RayModTapeHandTicker" };
            root.AddChild(_tapeTicker);
            _tapeTicker.SetProcess(true);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>把某个角度喂给 holder（走它自己的 <c>SetTargetAngle</c>，不直接改 Rotation）。</summary>
    private static void FeedAngle(Control holder, float angle)
    {
        try
        {
            var mi = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder")
                ?.GetMethod("SetTargetAngle", new[] { typeof(float) });

            if (mi == null)
            {
                return;
            }

            TapeFeeding = true;

            try
            {
                mi.Invoke(holder, new object[] { angle });
            }
            finally
            {
                TapeFeeding = false;
            }
        }
        catch (Exception ex)
        {
            TapeFeeding = false;
            Diag("tapefeed", $"[录像带·手牌] FeedAngle 异常：{ex.GetType().Name} {ex.Message} inner={ex.InnerException?.GetType().Name}:{ex.InnerException?.Message}", 6);
        }
    }

    /// <summary>
    /// 把某个落位喂给 holder（走它自己的 <c>SetTargetPosition</c>，位移交给它的 <c>AnimPosition</c> 平滑）。
    /// <para>第一次喂之前先记下基准落位 —— 否则读回来的已经是我们抖过的值，会逐帧累积漂走。</para>
    /// </summary>
    private static void FeedPosition(Control holder, ulong id)
    {
        try
        {
            Type? type = AccessTools_TypeByName("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder");

            if (type == null)
            {
                return;
            }

            if (!TapeBasePos.ContainsKey(id))
            {
                if (type.GetProperty("TargetPosition")?.GetValue(holder) is not Vector2 current)
                {
                    return;
                }

                TapeBasePos[id] = current;
            }

            var mi = type.GetMethod("SetTargetPosition", new[] { typeof(Vector2) });

            if (mi == null)
            {
                return;
            }

            TapeFeeding = true;

            try
            {
                mi.Invoke(holder, new object[] { TapeBasePos[id] + PosJitterOf(id) });
            }
            finally
            {
                TapeFeeding = false;
            }
        }
        catch (Exception ex)
        {
            TapeFeeding = false;
            Diag("tapepos", $"[录像带·手牌] FeedPosition 异常：{ex.GetType().Name} {ex.Message} inner={ex.InnerException?.GetType().Name}:{ex.InnerException?.Message}", 6);
        }
    }

    /// <summary>牌被拖起来时它的<b>实际位置</b>会跟着鼠标走，离我们自己喂的落位很远。</summary>
    /// <para><b>为什么不能只靠 <c>LerpToMouse</c> 的补丁</b>：那条钩子只在按住并移动时每帧跑一次，
    /// 自动化点按太快、或拖拽由别的入口驱动时会漏；位置偏离是<b>现象</b>，不依赖任何钩子。</para>
    /// <para>阈值 70px 的依据：正常落位靠 <c>AnimPosition</c>（7/s 一阶低通）追我们的目标，
    /// 单帧最多差几十像素；而拖拽时鼠标一动就是上百像素。</para>
    private static bool IsHeldByGame(Control holder, ulong id)
    {
        if (!TapeBasePos.TryGetValue(id, out Vector2 basePosition))
        {
            return false;
        }

        // ① 首选判据：左键按着、且这张牌就在鼠标附近 ⇒ 游戏正在拖它。
        //    这个判据不会把"鼠标悬停"误判成拖拽（悬停时不按键），所以晃动照常生效。
        try
        {
            if (Godot.Input.IsMouseButtonPressed(Godot.MouseButton.Left)
                && holder.GetViewport() is Viewport viewport)
            {
                Vector2 mouse = viewport.GetMousePosition();
                Vector2 center = holder.GetGlobalTransform().Origin;

                if (center.DistanceTo(mouse) < 320f)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 忽略
        }

        // ② 兜底判据：实际位置离我们喂的落位太远（悬停放大会抬 40~60px，所以阈值放到 160px）
        Vector2 actual = holder.Position;
        Vector2 want = basePosition + PosJitterOf(id);

        return actual.DistanceTo(want) > 160f;
    }

    private static void TickTape()
    {
        Diag("tapetick", $"[录像带·手牌] tick active={TapeHandActive} holders={TapeHolders.Count} base={TapeBaseAngle.Count}", 4);

        if (!TapeHandActive)
        {
            return;
        }

        CheckTapeExpiry();

        if (!TapeHandActive)
        {
            return;
        }

        if (!Enabled)
        {
            ClearTapeHand();
            return;
        }

        ulong now = Time.GetTicksMsec();
        int dead = 0;
        int outside = 0;
        int noBase = 0;
        int throttled = 0;
        int fed = 0;
        int skipped = 0;

        for (int i = TapeHolders.Count - 1; i >= 0; i--)
        {
            Control? holder = TapeHolders[i];

            if (!IsAlive(holder))
            {
                TapeHolders.RemoveAt(i);
                dead++;
                continue;
            }

            if (holder is not Node node || !node.IsInsideTree())
            {
                TapeHolders.RemoveAt(i);
                outside++;
                continue;
            }

            ulong id = holder.GetInstanceId();

            // 正在被拖的牌交给游戏自己摆（否则两边互相拉扯 ⇒ 抽搐）
            if (IsDragging(id) || IsHeldByGame(holder, id))
            {
                skipped++;
                Diag("tapeskip", "[录像带·手牌] 跳过被拖拽的牌（不喂角度/落位）", 40);
                continue;
            }

            if (!TapeBaseAngle.TryGetValue(id, out float baseAngle))
            {
                noBase++;
                continue;
            }

            if (TapeAppliedAt.TryGetValue(id, out ulong last) && now - last < TapeApplyIntervalMs)
            {
                throttled++;
                continue;
            }

            TapeAppliedAt[id] = now;
            PollHover(holder, id);
            FeedAngle(holder, baseAngle + TiltOf(id) + WobbleOf(id));
            FeedPosition(holder, id);
            fed++;
        }

        Diag("tapeloop", $"[录像带·手牌] 循环 fed={fed} throttled={throttled} noBase={noBase} dead={dead} outside={outside} rot={ProbeRot()} wob={TapeHoverStart.Count} skip={skipped}", 60);
    }

    private static string ProbeRot()
    {
        try
        {
            if (TapeHolders.Count == 0 || TapeHolders[0] is not Control first)
            {
                return "n/a";
            }

            return $"{first.RotationDegrees:F1}/tilt{TiltOf(first.GetInstanceId()):F1}";
        }
        catch
        {
            return "err";
        }
    }

    /// <summary>启动自检：三个反射目标是否都拿到了（打一行日志，方便确认补丁挂上）。</summary>
    internal static void LogTapeProbe(bool holder, bool angle, bool hover)
    {
        try
        {
            Log.Info($"[录像带·手牌] 软解析：Holder={holder} SetTargetAngle={angle} Hover={hover}" +
                     $" ⇒ 挂载={holder && angle && hover}");
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>挂在 <c>SceneTree.Root</c> 上的心跳（换房间/读档都回收不到它）。</summary>
    private sealed partial class TapeHandTicker : Node
    {
        public override void _Ready() => SetProcess(true);

        public override void _Process(double delta) => TickTape();
    }
}
