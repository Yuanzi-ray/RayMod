using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Ammo;
using RayMod.Powers;

// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;
using LogType = MegaCrit.Sts2.Core.Logging.LogType;

namespace RayMod.Art;

/// <summary>
/// ★★ <b>「填装形态」开出来之后，莱伊身边的常驻特效</b>（2026-10-19）。
///
/// <para><b>⚠️ 设计约束（踩过一次，别再犯）</b>：<b>不能用"一圈东西绕她公转"</b> ——
/// 那是<b>子弹模式</b>已经做过的「伪 3D 环轨」母题（一圈椭圆绕她转、后半段沉到她身后）。
/// 再做一次就是自我重复。所以这里换<b>卡图里的另一条母题</b>。</para>
///
/// <para><b>卡图 <c>images/cards/LoadingPosture.png</c> 上有两条线索</b>：
/// ① <b>一条巨大的奶油金螺旋带缠着她</b>；② <b>满天黄铜子弹在下落</b>。
/// 本类做的就是这两条 —— <b>连续的光带</b>而不是离散物体、<b>垂直下落</b>而不是水平公转：</para>
///
/// <list type="bullet">
///   <item><b>缠体的螺旋光带</b>：绕她竖直轴盘 <see cref="HelixTurns"/> 圈，
///         慢速自旋 + 呼吸式明暗；转到她身后那半圈<b>变暗变细</b>（透视）。</item>
///   <item><b>落下的黄铜弹壳</b>：从她头顶上方缓缓坠下、落地前淡出。
///         数量随<b>弹药</b>变化 —— 弹药多 = 弹壳更多，把"我在装填"这件事直接读出来。</item>
/// </list>
///
/// <para><b>生命周期</b>：<see cref="LoadingPosturePower.AfterApplied"/> 挂载、
/// <see cref="RetireReloadAura"/> 收场；节点自己每 0.5s 自查（能力还在不在 / 特效开关还开不开），
/// 任何一条收场路径漏了也不会留幽灵在场上。</para>
///
/// <para>⚠️ 坐标：父节点是 <c>room.CombatVfxContainer</c>（<b>Control，世界坐标容器</b>），
/// 位置一律用 <see cref="CardFieldVfx.ChestPos"/> 算再写 <c>GlobalPosition</c>。</para>
/// </summary>
internal partial class CardFieldVfx
{
    private static readonly Logger AuraLog = new("RayMod.ReloadAura", LogType.Generic);

    private static ReloadAura? _reloadAura;

    /// <summary>
    /// 「填装形态」身边那套的开关。<b>2026-10-19 用户实机确认能看到</b>，所以为 true。
    ///
    /// <para><b>★ 记一次教训</b>：我一度把它判成"画不出来"并准备关掉发版 ——
    /// 依据是我抽帧后在某一张截图里没找到它。但<b>用户进游戏就看得见</b>。
    /// 错在我：抽帧是在赌时间窗（0.5 秒级别的事件、2fps 采样），漏帧率极高；
    /// 这一整轮已经因为同样的方法误判过螺旋、误判过拖尾。
    /// <b>结论：视觉是否存在，以用户实机反馈为准；抽帧只能用来定位"哪一帧有"，不能用来断言"没有"。</b></para>
    /// </summary>
    internal const bool SideAuraEnabled = true;

    /// <summary>由 <c>LoadingPosturePower.AfterApplied</c> 调。</summary>
    internal static void AttachReloadAura(Player? owner)
    {
        if (!SideAuraEnabled)
        {
            return;
        }

        if (!Enabled || owner == null || TestModeIsOn())
        {
            return;
        }

        if (!IsLocalPlayer(owner))
        {
            return;
        }

        try
        {
            if (_reloadAura != null && GodotObject.IsInstanceValid(_reloadAura))
            {
                _reloadAura.Reconfigure(owner);
                return;
            }

            Node? container = VfxParentForEffects();

            if (container == null)
            {
                AuraLog.Warn("[填装形态·身边] 拿不到 CombatVfxContainer ⇒ 跳过");
                return;
            }

            var aura = new ReloadAura();
            container.AddChild(aura);
            aura.Configure(owner);
            _reloadAura = aura;

            AuraLog.Info("[填装形态·身边] 已挂上（缠体螺旋光带 + 落弹壳）");
        }
        catch (Exception e)
        {
            AuraLog.Warn($"[填装形态·身边] 挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>收场：能力掉了 / 战斗结束 / 设置里关了特效。</summary>
    internal static void RetireReloadAura()
    {
        if (_reloadAura != null && GodotObject.IsInstanceValid(_reloadAura))
        {
            _reloadAura.Retire();
        }

        _reloadAura = null;
    }

    /// <summary>莱伊身边的常驻件：缠体螺旋光带 + 下落黄铜弹壳。</summary>
    private sealed partial class ReloadAura : Node2D
    {
        // ───────────────────────── 调参 ─────────────────────────

        /// <summary>螺旋带的半径（世界像素）。</summary>
        private const float HelixRadius = 105f;

        /// <summary>螺旋带的竖直高度（她身上从头顶到脚下）。</summary>
        private const float HelixHeight = 250f;

        /// <summary>盘几圈。</summary>
        private const float HelixTurns = 1.75f;

        /// <summary>带子自旋速度（弧度/秒）。很慢 —— 常驻件不抢视线。</summary>
        private const float HelixSpin = 0.75f;

        /// <summary>沿螺旋采样多少个点（越多越平滑，但每帧都要画）。</summary>
        private const int HelixSamples = 84;

        /// <summary>采样点的基准半径（像素）—— 靠叠加成带。</summary>
        private const float HelixDot = 7.5f;

        /// <summary>螺旋带中心相对胸口的高度（负 = 往上，让带子覆盖她全身）。</summary>
        private const float HelixLiftY = -20f;

        /// <summary>最多几枚下落的弹壳。</summary>
        private const int MaxCasings = 10;

        /// <summary>弹壳下落速度区间（像素/秒）。</summary>
        private const float CasingFallMin = 55f;
        private const float CasingFallMax = 120f;

        /// <summary>弹壳横向散布（±像素）。</summary>
        private const float CasingSpreadX = 130f;

        /// <summary>弹壳生成点相对胸口的高度（负 = 头顶上方）。</summary>
        private const float CasingSpawnY = -230f;

        /// <summary>弹壳落到这个高度（相对胸口，正 = 脚下）就淡出。</summary>
        private const float CasingEndY = 130f;

        // 卡图实测色
        private static readonly Color Band = new(0.957f, 0.800f, 0.169f);      // #f4cc2b
        private static readonly Color BandHot = new(1f, 0.965f, 0.859f);       // #fff6db
        private static readonly Color Brass = new(0.890f, 0.678f, 0.275f);     // #e3ad46

        // ───────────────────────── 状态 ─────────────────────────

        private Player _owner = null!;
        private int _ammo = 1;
        private float _t;
        private float _poll;
        private float _fade;
        private float _flash;
        private bool _retiring;
        private bool _subscribed;
        private bool _loggedAnchor;
        private Vector2 _anchor;

        /// <summary>下落弹壳：(出生时间, 横向偏移比例 -1..1, 下落速度)。</summary>
        private readonly Godot.Collections.Array<float[]> _casings = new();

        private Random _rng = new();

        internal void Configure(Player owner)
        {
            _rng = new Random(owner.GetHashCode() ^ 0x5EED);

            if (!_subscribed)
            {
                _subscribed = true;
                AmmoEvents.Register(OnAmmoChanged);
            }

            Reconfigure(owner);
            SetProcess(true);
            ZIndex = 1;
        }

        /// <summary>换主人 / SL 重开后重新认人（和巨眼同一套路）。</summary>
        internal void Reconfigure(Player owner)
        {
            _owner = owner;
            _retiring = false;
            _ammo = SafeAmmo();
        }

        internal void Retire() => _retiring = true;

        private int SafeAmmo()
        {
            try
            {
                return Mathf.Clamp(AmmoCmd.GetAmmo(_owner), 0, 7);
            }
            catch
            {
                return 1;
            }
        }

        private void OnAmmoChanged(Player player, int oldValue, int newValue)
        {
            if (player != _owner)
            {
                return;
            }

            _ammo = Mathf.Clamp(newValue, 0, 7);
            _flash = 1f;
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.05f);
                _t += dt;
                _poll += dt;

                if (_poll >= 0.5f)
                {
                    _poll = 0f;

                    bool stillOn;

                    try
                    {
                        stillOn = Enabled && _owner.Creature != null
                                  && _owner.Creature.GetPower<LoadingPosturePower>() != null;
                    }
                    catch
                    {
                        stillOn = false;
                    }

                    if (!stillOn)
                    {
                        _retiring = true;
                    }
                }

                _fade = Mathf.Clamp(_fade + (_retiring ? -dt / 0.45f : dt / 0.60f), 0f, 1f);

                if (_retiring && _fade <= 0.001f)
                {
                    Cleanup();
                    QueueFree();
                    return;
                }

                _flash = Mathf.Max(0f, _flash - dt / 0.25f);

                UpdateAnchor();
                UpdateCasings(dt);
                QueueRedraw();
            }
            catch
            {
                Cleanup();
                QueueFree();
            }
        }

        private void Cleanup()
        {
            if (_subscribed)
            {
                _subscribed = false;
                AmmoEvents.Unregister(OnAmmoChanged);
            }

            _reloadAura = null;
        }

        /// <summary>每帧重读她的胸口坐标（她会走位、会被击退）。</summary>
        private void UpdateAnchor()
        {
            try
            {
                object? creature = _owner.Creature;

                if (creature == null)
                {
                    return;
                }

                _anchor = ChestPos(creature);

                // ★★ 必须把**节点自己**放到锚点上，然后画相对偏移。
                //   踩过的坑：一开始我把节点钉在世界原点、用"全局坐标当局部坐标"画，
                //   结果什么都不显示 —— 因为父容器是 Control（有自己的变换），
                //   局部 (480,644) ≠ 全局 (480,644)。本项目所有场上特效都是
                //   `AddChild` → 再 `GlobalPosition = 世界坐标` 这个顺序。
                GlobalPosition = _anchor;

                if (!_loggedAnchor)
                {
                    _loggedAnchor = true;
                    AuraLog.Info($"[填装形态·身边] 锚点 = {_anchor}（胸口）→ 节点 GlobalPosition 已同步");
                }
            }
            catch
            {
                // 保持上一次的锚点
            }
        }

        /// <summary>
        /// 维护下落弹壳。
        /// <b>数量跟着弹药走</b>：弹药 0 → 0 枚，弹药 7 → 7~10 枚，
        /// 于是"弹药越多 = 她身边落下的弹壳越多"，把装填状态直接读出来。
        /// </summary>
        private void UpdateCasings(float dt)
        {
            // 目标数量：弹药越多越多（0..7 → 0..MaxCasings）
            int want = Mathf.RoundToInt(Mathf.Clamp(_ammo, 0, 7) / 7f * MaxCasings);

            // 补足
            while (_casings.Count < want)
            {
                _casings.Add(new[]
                {
                    (float)_t,
                    (float)(_rng.NextDouble() * 2.0 - 1.0),
                    Mathf.Lerp(CasingFallMin, CasingFallMax, (float)_rng.NextDouble()),
                });
            }

            // 超出的移除（从最老的开始）
            while (_casings.Count > want)
            {
                _casings.RemoveAt(0);
            }
        }

        public override void _Draw()
        {
            if (_fade <= 0.001f)
            {
                return;
            }

            // ★ 节点自己已经在锚点上（见 UpdateAnchor 里写 GlobalPosition），
            //   所以这里画**相对锚点**的局部偏移，中心就是 (0,0)。
            Vector2 local = Vector2.Zero;
            float a = _fade;
            float flash = FieldFxEase.OutCubic(_flash);

            DrawHelix(local, a, flash);

            foreach (float[] c in _casings)
            {
                DrawCasing(local, c, a);
            }
        }

        /// <summary>
        /// 缠体的螺旋光带：沿竖直轴盘 <see cref="HelixTurns"/> 圈，按深度（sin）做前后景明暗。
        /// 用一串叠加的圆点连成带 —— 比 Line2D 更好控粗细，而且不需要贴图。
        /// </summary>
        private void DrawHelix(Vector2 center, float fade, float flash)
        {
            float top = center.Y + HelixLiftY - HelixHeight * 0.5f;
            float bottom = center.Y + HelixLiftY + HelixHeight * 0.5f;

            for (int i = 0; i < HelixSamples; i++)
            {
                float u = (float)i / (HelixSamples - 1);

                float ang = _t * HelixSpin + u * HelixTurns * Mathf.Tau;

                float x = center.X + Mathf.Cos(ang) * HelixRadius;
                float y = Mathf.Lerp(top, bottom, u);

                // depth: -1 = 最靠近镜头，+1 = 转到她身后
                float depth = Mathf.Sin(ang);
                float near = Mathf.InverseLerp(1f, -1f, depth);

                // 两端（头顶 / 脚下）收细，读成"从她身侧绕过去"而不是一根硬棒
                float taper = Mathf.Sin(Mathf.Pi * u);

                float r = HelixDot * Mathf.Lerp(0.45f, 1.0f, near) * (0.55f + 0.45f * taper);
                float alpha = Mathf.Lerp(0.10f, 0.55f, near) * fade * (0.55f + 0.45f * taper)
                              * (1f + 1.1f * flash);

                if (alpha <= 0.004f || r <= 0.3f)
                {
                    continue;
                }

                // 靠近镜头的点用更亮的暖白，远处的用金黄
                Color c = Band.Lerp(BandHot, near * 0.75f);
                c.A = alpha;

                DrawCircle(new Vector2(x, y), r, c);
            }
        }

        /// <summary>一枚正在下落的黄铜弹壳。</summary>
        private void DrawCasing(Vector2 center, float[] c, float fade)
        {
            float bornAt = c[0];
            float offset = c[1];
            float speed = c[2];

            float age = _t - bornAt;

            if (age < 0f)
            {
                return;
            }

            float travel = age * speed;
            float total = CasingEndY - CasingSpawnY;

            if (travel > total)
            {
                // 落到底了：重置这一枚（改出生时间，换个横向位置）
                c[0] = (float)_t;
                return;
            }

            float u = travel / total;

            float x = center.X + offset * CasingSpreadX;
            // 下落途中轻微左右摆（弹壳不是直着掉的）
            x += Mathf.Sin(age * 2.4f + offset * 3f) * 5f;

            float y = center.Y + CasingSpawnY + travel;

            // 出生淡入、落底淡出
            float alpha = Mathf.Min(1f, u / 0.12f) * Mathf.Clamp((1f - u) / 0.25f, 0f, 1f) * fade * 0.75f;

            if (alpha <= 0.004f)
            {
                return;
            }

            var col = new Color(Brass.R, Brass.G, Brass.B, alpha);

            // 弹壳：小竖条 + 一点高光，程序化
            const float w = 5.5f;
            const float h = 13f;

            DrawRect(new Rect2(x - w * 0.5f, y - h * 0.5f, w, h * 0.78f), col);

            var lit = new Color(BandHot.R, BandHot.G, BandHot.B, alpha * 0.7f);
            DrawRect(new Rect2(x - w * 0.16f, y - h * 0.42f, w * 0.32f, h * 0.5f), lit);
        }
    }
}
