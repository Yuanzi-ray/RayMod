using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using RayMod.Audio;
using RayMod.Powers;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// ★★★ <b>「巨兽」的常驻巨眼</b>（2026-10-13 用户定稿的设计）。
///
/// <para><b>用户原话</b>："能不能做到：用<b>烟雾粒子组成巨兽眼睛的影子</b>，
/// 但是<b>鼠标靠近的地方粒子散开</b>？" +"巨兽开出来之后<b>眼睛就常驻了</b>啊，但是你的鼠标过去会打散"。</para>
///
/// <para><b>所以这是"能力型"特效，不是"出牌演出"</b>：<see cref="FeranmutsPower"/> 被施加时挂上
/// （见 <see cref="FeranmutsPower.AfterApplied"/>），<b>整场战斗常驻</b>；能力掉了 / 战斗结束才散掉。
/// 形状来自 `_tools\make_giant_eye_cloud.py` 采出来的点云（<see cref="FeranmutsCloudSpec"/>：
/// 眼廓 300 + 虹膜羽 198 + 虹膜外圈 84 + 竖瞳边 70 + 填充 130 + 花瓣 48 = 818 颗）。</para>
///
/// <para><b>三层结构（各有各的混合模式，所以必须分节点）</b>：</para>
/// <list type="number">
/// <item><b>本节点</b>（普通混合）画"**眼后那团暗**"（`fx_eye_shadow.png`）——
/// 用户 2026-10-13 指出"这么细的粒子可能只有黑底才看得清"，**先压暗再画粒子**是最有效的一招，
/// 而且正好是"巨兽的<b>影子</b>"该有的读法。</item>
/// <item><b>子层 `Particles`</b>（<b>加色</b>）：每颗粒子两层 —— 柔光晕（`fx_glow_solid`，2.6×）+ 硬核
/// （`fx_star_05` / 软烟）。加色的光晕在战场上"自己发光"，硬核保证形状清楚。
/// ⚠️ <b>铁律 78</b>：这一层必须画在**它自己**的 `CanvasItem` 上（`canvas.DrawTexture`），
/// 不能调父节点的绘制方法（那会静默什么都不画）。</item>
/// <item><b>物理</b>：每颗粒子一个弹簧回原位 + 鼠标径向推开 + 阻尼（见 <see cref="Update"/>）。
/// 鼠标走了自己飘回来 —— 这是"散开"能读出来的关键（否则会瞬间弹回去，像假的一样）。</item>
/// </list>
///
/// <para>⚠️ 与预览共用同一批数字：几何/物理常量要和
/// <c>_tools\make_giant_eye_cloud.py</c> 的 <c>PHYS</c> 保持一致（预览是判断观感的唯一便宜手段）。</para>
/// </summary>
internal partial class CardFieldVfx
{
    /// <summary>巨眼开关。<b>跟着设置页的「出牌特效」类别走</b>（模组设置 → RayMod → 莱伊 · 特效）
    /// —— 用户定的口径："开关按类别分，不按卡分"（所以巨兽**没有**自己的开关）。
    /// 关掉之后：① 不再挂新的 ② 已经挂着的会在 0.5s 自查里被收掉。</summary>
    internal static bool FeranmutsEyeEnabled =>
        VoiceState.VfxEnabled && VoiceState.FieldVfxEnabled;

    private static readonly Logger EyeLog = new("RayMod.CardVfx", LogType.Generic);

    // ── 几何（与 `make_giant_eye_cloud.py` 的 PHYS 一致）────────────────────────
    /// <summary>眼睛宽度 = 屏宽 × 它（常驻物件，别太大挡视线）。
    /// 2026-10-13 用户实机："**有点大**" ⇒ 0.38 → **0.28**。</summary>
    private const float EyeScreenWFrac = 0.28f;

    /// <summary>
    /// 眼睛中心在屏幕上的位置（占屏宽/高）。
    /// ★ 2026-10-13 用户实机："**位置不在中间，现在偏右**" ⇒ X 0.56 → **0.50**（摆正），
    /// Y 0.34 → **0.36**（略高于正中，别压着手牌）。
    /// </summary>
    private const float EyeCenterXFrac = 0.50f;
    private const float EyeCenterYFrac = 0.36f;

    /// <summary>
    /// **颜色档 → 取色**（与 `_tools\make_giant_eye_cloud.py` 的 `PALETTE` **一一对应**）。
    ///
    /// <para>★ 2026-10-13 用户："巨兽眼睛**颜色太单一**了" → 加了颜色档；
    /// 随后"**配色可以再贴近一点，感觉现在不够像**" ⇒ 这些值**不是凭记忆写的，是从卡面量出来的**
    /// （`image_palette` 量虹膜区 + 定点采样；虹膜区实测：蓝 44% / 青 27% / 紫 6% / 粉 0.6%）。
    /// 第一版那档"艳品红"太跳 —— 卡面其实是**淡薰衣草紫** `#d2abfa`。</para>
    /// </summary>
    private static readonly Color[] EyePalette =
    {
        new(0.75f, 0.94f, 1.00f),      // 0 眼白 / 眼廓（卡面最亮青白 #c9f2fe）
        new(0.22f, 0.53f, 0.78f),      // 1 虹膜底 / 外圈（卡面中带蓝 #3788c5）
        new(0.49f, 0.89f, 0.97f),      // 2 虹膜亮羽（青 #7ce2f7）
        new(0.82f, 0.67f, 0.98f),      // 3 虹膜紫羽（淡薰衣草紫 #d2abfa）
        new(0.10f, 0.26f, 0.46f),      // 4 竖瞳边（深藏青，压出那条黑缝）
        new(0.38f, 0.72f, 0.92f),      // 5 花瓣 / 下缘板（卡面下缘板实测 (78,180,233)）
    };

    /// <summary>眼白的高度 / 宽度（点云就是以"眼高"为单位的，这个比例来自眼型本身）。</summary>
    private const float EyeHOverWFrac = 0.66f;

    // ── 物理（与 PHYS 一致）──────────────────────────────────────────────────
    private const float PushRadius = 0.42f;      // 鼠标影响半径（× 眼高）
    private const float PushStrength = 3.6f;     // 推开加速度（× 眼高/秒²）
    private const float SpringK = 7.5f;          // 回原位的弹簧
    private const float Damping = 3.4f;          // 阻尼（越小越飘）
    private const float Drift = 0.030f;          // 静止时的缓慢飘动（× 眼高/秒）
    private const float MaxPush = 0.55f;         // 被推开的位移上限（× 眼高）
    private const float HaloScale = 2.6f;        // 柔光晕相对硬核的倍率

    // ── ★★ 出牌冲击（2026-10-13 用户："出牌后所有粒子都散开，然后再回来"）─────────
    /// <summary>冲击初速（× 眼高/秒）—— 每颗再乘自己的敏感度，所以散得有快有慢。</summary>
    private const float BurstSpeed = 2.45f;

    /// <summary>冲击时的切向分量（× 眼高/秒）—— 纯径向会像"均匀膨胀"，加一点旋才有"炸开"的味道。</summary>
    private const float BurstSwirl = 0.85f;

    /// <summary>冲击后弹簧被压弱的时长：这段时间里粒子"还不想回来"。</summary>
    private const float BurstLoose = 0.70f;

    /// <summary>弹簧最弱时的比例（0.16 = 几乎不拉回去）。</summary>
    private const float BurstSpringMin = 0.16f;

    /// <summary>冲击时粒子最暗到多少（散开时"变淡"，聚回来再变亮 = 重新凝聚的感觉）。</summary>
    private const float BurstAlphaMin = 0.30f;

    /// <summary>粒子贴图表（下标 1..4 与点云档案的 `Tex` 字段对应）。</summary>
    private static readonly string[] EyeTexPaths =
    {
        "/images/vfx/fx_star_05.png",     // 1 硬核（70%）
        "/images/vfx/fx_smoke_1.png",     // 2 软烟
        "/images/vfx/fx_smoke_2.png",     // 3
        "/images/vfx/fx_smoke_3.png",     // 4
    };

    private static readonly string EyeHaloPath = "/images/vfx/fx_glow_solid.png";
    private static readonly string EyeShadowPath = "/images/vfx/fx_eye_shadow.png";

    private static GiantEye? _giantEye;

    /// <summary>能力被施加时调用（见 <see cref="FeranmutsPower.AfterApplied"/>）。</summary>
    internal static void AttachGiantEye(Player? owner)
    {
        if (!Enabled || !FeranmutsEyeEnabled || owner == null || TestModeIsOn())
        {
            return;
        }

        // ★★ 联机门控（2026-10-13）：**只给本地玩家自己挂**。
        //    两个理由，缺一不可：
        //    ① 这只眼是**屏幕坐标系**的常驻物件（`Reconfigure` 里
        //       `GlobalPosition = (屏宽 × EyeCenterXFrac, 屏高 × EyeCenterYFrac)`），
        //       它压根没挂在莱伊身上 —— 队友打出「巨兽」时在我屏幕正中长出一只眼，
        //       位置毫无意义；
        //    ② `_giantEye` 是**静态单例**，`Reconfigure(owner)` 会**就地改主人**
        //       ⇒ 两人都有「巨兽」时后打的人会把先打的人那只眼顶掉，而且下面 0.5s
        //       自查判的是 `_owner` 的能力 ⇒ 后者的能力一掉就把前者的眼也收走。
        //    单机下 `IsMe` 恒真 ⇒ 行为零变化。
        //    ⚠️ 若将来想让队友的巨兽也可见，正确做法是把它锚到 owner **的生物节点**
        //       并把 `_giantEye` 改成按 NetId 分键的字典 —— 不是把这道门去掉。
        if (!IsLocalPlayer(owner))
        {
            return;
        }

        try
        {
            if (_giantEye != null && GodotObject.IsInstanceValid(_giantEye))
            {
                _giantEye.Reconfigure(owner);          // SL / 重开后重新认人
                return;
            }

            Node? container = VfxParentForEffects();

            if (container == null)
            {
                EyeLog.Info("[巨兽·巨眼] 拿不到 CombatVfxContainer，跳过");
                return;
            }

            var eye = new GiantEye();
            container.AddChild(eye);
            eye.Configure(owner, container);
            _giantEye = eye;
        }
        catch (Exception e)
        {
            EyeLog.Warn($"[巨兽·巨眼] 挂载失败：{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>收场（能力掉了 / 战斗结束时也可以直接调）。</summary>
    internal static void RetireGiantEye()
    {
        if (_giantEye != null && GodotObject.IsInstanceValid(_giantEye))
        {
            _giantEye.Retire();
        }

        _giantEye = null;
    }

    /// <summary>
    /// ★★ <b>出牌冲击</b>：打出任意一张牌时，<b>整只眼睛的粒子全部炸散、再自己聚回来</b>
    /// （用户 2026-10-13："出牌后所有粒子都散开，然后再回来"）。
    /// 由 <see cref="FeranmutsPower.BeforeCardPlayed"/> 调用。
    ///
    /// <para><b>★ 2026-10-13 联机适配：必须带 <paramref name="owner"/> 进来判归属。</b>
    /// 调用方（Power 的 <c>BeforeCardPlayed</c>）已经正确判过"是不是自己打出的牌"，
    /// 但那个判据只管得住**它自己那一份 Power**：联机时队友的「巨兽」Power
    /// 也会在**我的客户端**上跑一遍 <c>BeforeCardPlayed</c> 并调到这里 ——
    /// 而 <c>_giantEye</c> 是静态单例（并且现在只属于本地玩家）
    /// ⇒ 不判的话**队友出牌会震我的眼睛**（正是那条注释想避免的事）。</para>
    /// </summary>
    internal static void BurstGiantEye(Player? owner)
    {
        if (!IsLocalPlayer(owner))
        {
            return;
        }

        if (_giantEye != null && GodotObject.IsInstanceValid(_giantEye))
        {
            _giantEye.Burst();
        }
    }

    /// <summary>
    /// 常驻巨眼。<b>本节点画"眼后的暗"（普通混合）</b>，粒子画在加色的子层上。
    /// </summary>
    private sealed partial class GiantEye : Node2D
    {
        /// <summary>加色粒子层（自己的 `_Draw`、自己的材质 —— 见铁律 78 与 78 的由来）。</summary>
        private sealed partial class ParticleLayer : Node2D
        {
            internal Action<ParticleLayer>? Painter;

            public override void _Draw() => Painter?.Invoke(this);
        }

        private Player _owner = null!;
        private ParticleLayer _layer = null!;
        private Texture2D?[] _texs = Array.Empty<Texture2D?>();
        private Texture2D? _halo, _shadow;

        private Vector2[] _home = Array.Empty<Vector2>();
        private Vector2[] _pos = Array.Empty<Vector2>();
        private Vector2[] _vel = Array.Empty<Vector2>();
        private float[] _sf = Array.Empty<float>();
        private float[] _alpha = Array.Empty<float>();
        private float[] _react = Array.Empty<float>();
        private float[] _phase = Array.Empty<float>();
        private float[] _spin = Array.Empty<float>();
        private float[] _rot0 = Array.Empty<float>();
        private int[] _tex = Array.Empty<int>();
        private int[] _hue = Array.Empty<int>();

        private float _eyeH;              // 眼睛高度（世界单位 = 容器像素）
        private float _t;
        private float _poll;
        private float _fade;              // 淡入/淡出
        private bool _retiring;
        private bool _logged;
        private bool _mouseLogged;
        private float _burstT = 999f;     // 距上次"出牌冲击"的秒数（999 = 没在冲击中）
        private float _burstAlpha = 1f;   // 冲击时的整体透明度（散开变淡、聚回变亮）
        private int _bursts;
        private Random? _rng;

        internal void Configure(Player owner, Node container)
        {
            _texs = new Texture2D?[EyeTexPaths.Length];

            for (int i = 0; i < EyeTexPaths.Length; i++)
            {
                _texs[i] = ResourceLoader.Load<Texture2D>(Entry.ResPath + EyeTexPaths[i]);
            }

            _halo = ResourceLoader.Load<Texture2D>(Entry.ResPath + EyeHaloPath);
            _shadow = ResourceLoader.Load<Texture2D>(Entry.ResPath + EyeShadowPath);

            if (_texs[0] == null || _halo == null || _shadow == null)
            {
                EyeLog.Warn("[巨兽·巨眼] 贴图缺失 ⇒ 跳过（查 pck 是否含 fx_star_05 / fx_glow_solid / fx_eye_shadow）");
                QueueFree();
                return;
            }

            // 加色粒子层（画在父节点之上；它只画粒子，父节点画那团暗）
            _layer = new ParticleLayer { Name = "Particles", Material = AdditiveMaterial() };
            AddChild(_layer);
            _layer.Painter = DrawParticles;

            ReadCloud();
            Reconfigure(owner);
            SetProcess(true);                    // ★ 显式开启（别赌"重写了 _Process 就会自动开"）
            _fade = 0f;
        }

        /// <summary>把点云档案读进数组（只做一次）。</summary>
        private void ReadCloud()
        {
            int n = FeranmutsCloudSpec.Count;
            _home = new Vector2[n];
            _pos = new Vector2[n];
            _vel = new Vector2[n];
            _sf = new float[n];
            _alpha = new float[n];
            _react = new float[n];
            _phase = new float[n];
            _spin = new float[n];
            _rot0 = new float[n];
            _tex = new int[n];
            _hue = new int[n];
            float[] p = FeranmutsCloudSpec.Particles;

            for (int i = 0; i < n; i++)
            {
                int b = i * FeranmutsCloudSpec.Stride;
                _home[i] = new Vector2(p[b + FeranmutsCloudSpec.Ux], p[b + FeranmutsCloudSpec.Uy]);
                _sf[i] = p[b + FeranmutsCloudSpec.Sf];
                _alpha[i] = p[b + FeranmutsCloudSpec.Alpha];
                _react[i] = p[b + FeranmutsCloudSpec.React];
                _phase[i] = p[b + FeranmutsCloudSpec.Phase];
                _spin[i] = p[b + FeranmutsCloudSpec.Spin];
                _rot0[i] = p[b + FeranmutsCloudSpec.Rot];
                _tex[i] = Mathf.Clamp((int)p[b + FeranmutsCloudSpec.Tex], 1, EyeTexPaths.Length);
                _hue[i] = Mathf.Abs((int)p[b + FeranmutsCloudSpec.Hue]) % EyePalette.Length;
                _pos[i] = Vector2.Zero;           // 从"她的位置"聚出来（第一帧会自己飞到位）
            }
        }

        /// <summary>认人 + 按容器尺寸量出眼睛的实际大小与落点。</summary>
        internal void Reconfigure(Player owner)
        {
            _owner = owner;
            var screen = (GetParent() as Control)?.Size ?? GetViewportRect().Size;

            if (screen.X < 16f || screen.Y < 16f)
            {
                screen = GetViewportRect().Size;
            }

            float eyeW = screen.X * EyeScreenWFrac;
            _eyeH = eyeW * EyeHOverWFrac;
            GlobalPosition = new Vector2(screen.X * EyeCenterXFrac, screen.Y * EyeCenterYFrac);

            if (!_logged)
            {
                _logged = true;
                EyeLog.Info($"[巨兽·巨眼] 已挂上：{FeranmutsCloudSpec.Count} 颗（屏幕 {screen.X:F0}×{screen.Y:F0}，" +
                            $"眼睛宽 {eyeW:F0} · 眼高 {_eyeH:F0} · 中心 {GlobalPosition}）");
            }
        }

        internal void Retire() => _retiring = true;

        /// <summary>
        /// 出牌冲击：给每颗粒子一个**向外的初速**（+ 一点切向旋），并把弹簧暂时压弱 ⇒
        /// 整只眼睛"炸散"；随后弹簧恢复，粒子自己飘回来重新成眼。
        /// </summary>
        internal void Burst()
        {
            _burstT = 0f;
            _bursts++;
            _rng ??= new Random();

            for (int i = 0; i < _pos.Length; i++)
            {
                Vector2 home = _home[i] * _eyeH;
                Vector2 dir = home.LengthSquared() > 1f ? home.Normalized() : new Vector2(_rng.NextSingle() - 0.5f, _rng.NextSingle() - 0.5f).Normalized();
                var perp = new Vector2(-dir.Y, dir.X);
                float speed = BurstSpeed * _eyeH * (0.55f + 0.85f * _react[i]);

                _vel[i] = dir * speed + perp * (BurstSwirl * _eyeH * (_rng.NextSingle() * 2f - 1f));
            }

            EyeLog.Info($"[巨兽·巨眼] 出牌冲击：{_pos.Length} 颗粒子散开（第 {_bursts} 次）");
        }

        public override void _Process(double delta)
        {
            try
            {
                float dt = Mathf.Min((float)delta, 0.05f);      // 掉帧时别让物理炸掉
                _t += dt;
                _poll += dt;
                _burstT = Mathf.Min(_burstT + dt, 30f);         // 距上次"出牌冲击"的时间

                if (_poll >= 0.5f)
                {
                    _poll = 0f;

                    // ★ 判据两条：① 能力还在（读异常当"还在"，见 §S35.4）② **设置页的开关还开着**
                    //   —— 后者让"在设置里关掉特效"能在 0.5 秒内把已经挂着的巨眼也收掉。
                    bool stillOn;

                    try
                    {
                        stillOn = _owner.Creature.GetPower<FeranmutsPower>() != null && FeranmutsEyeEnabled;
                    }
                    catch (Exception)
                    {
                        stillOn = FeranmutsEyeEnabled;
                    }

                    if (!stillOn)
                    {
                        _retiring = true;
                    }
                }

                _fade = Mathf.Clamp(_fade + (_retiring ? -dt / 0.45f : dt / 0.70f), 0f, 1f);

                if (_retiring && _fade <= 0.001f)
                {
                    QueueFree();
                    return;
                }

                Update(dt);
                QueueRedraw();

                if (_layer != null && GodotObject.IsInstanceValid(_layer))
                {
                    _layer.QueueRedraw();
                }
            }
            catch (Exception)
            {
                QueueFree();                    // 特效出错绝不能拖累战斗
            }
        }

        /// <summary>弹簧 + 鼠标推开 + 阻尼（用户要的"鼠标靠近的地方粒子散开"）。</summary>
        private void Update(float dt)
        {
            Vector2 mouse = GetGlobalMousePosition() - GlobalPosition;
            float rmax = PushRadius * _eyeH;

            // 一次性诊断：鼠标第一次靠近时把**局部位**打出来（万一还是没反应，
            // 这一行能立刻分辨"坐标系不对"还是"物理太弱"）。
            if (!_mouseLogged && rmax > 0.5f && mouse.Length() < rmax * 2f)
            {
                _mouseLogged = true;
                EyeLog.Info($"[巨兽·巨眼] 鼠标进入影响圈：局部位 {mouse}（节点全局 {GlobalPosition}，半径 {rmax:F0}）");
            }

            float damp = Mathf.Max(0f, 1f - Damping * dt);
            float maxOff = MaxPush * _eyeH * 2.4f;                   // 安全上限：别让粒子跑没影

            // ★ 出牌冲击：弹簧先被压到 BurstSpringMin（粒子"还不想回来"），
            //   再在 BurstLoose 秒内恢复；同时整体透明度从 BurstAlphaMin 淡回来 —— 散开变淡、聚回变亮。
            float burstK = Mathf.Clamp(_burstT / BurstLoose, 0f, 1f);
            float springScale = Mathf.Lerp(BurstSpringMin, 1f, burstK);
            _burstAlpha = Mathf.Lerp(BurstAlphaMin, 1f, burstK);
            float burstMaxOff = maxOff * Mathf.Lerp(2.6f, 1f, burstK);   // 炸开时允许跑得更远

            for (int i = 0; i < _home.Length; i++)
            {
                // 静止时也在缓慢飘（否则是一张"死"的图）
                var breathe = new Vector2(
                    Mathf.Sin(_t * 0.62f + _phase[i]) * Drift * _eyeH,
                    Mathf.Cos(_t * 0.81f + _phase[i] * 1.3f) * Drift * _eyeH);
                Vector2 home = _home[i] * _eyeH + breathe;

                Vector2 acc = (home - _pos[i]) * (SpringK * springScale);

                if (rmax > 0.5f)
                {
                    Vector2 d = _pos[i] - mouse;
                    float dist = d.Length();

                    if (dist < rmax && dist > 0.001f)
                    {
                        float k = 1f - dist / rmax;

                        // ★★★ 必须 × `_eyeH`：常量表里的 `PushStrength` 是"**眼高的倍数/秒²**"，
                        //    而弹簧那一项 `(home-pos)*SpringK` 是**像素**单位。
                        //    2026-10-13 实机 bug（用户："鼠标移过去无反应"）：这里漏了 × _eyeH
                        //    ⇒ 推开加速度只有几 px/s²、而弹簧是几百 px/s² ⇒ **推开被弹簧彻底压死**，
                        //    算了半天"物理是对的"，实际位移 ≈ 0。
                        acc += d / dist * (PushStrength * _react[i] * k * k * _eyeH);
                    }
                }

                _vel[i] = (_vel[i] + acc * dt) * damp;
                _pos[i] += _vel[i] * dt;

                Vector2 off = _pos[i] - home;

                if (off.LengthSquared() > burstMaxOff * burstMaxOff)
                {
                    _pos[i] = home + off.Normalized() * burstMaxOff;
                }
            }
        }

        /// <summary>本节点只画"眼后那团暗"（普通混合 —— 加色层负责粒子）。</summary>
        public override void _Draw()
        {
            if (_shadow == null || _fade <= 0.01f || _eyeH < 4f)
            {
                return;
            }

            DrawTexture2D(this, _shadow, Vector2.Zero, _eyeH * 1.95f, 0f,
                          new Color(1f, 1f, 1f, 0.85f * _fade * Mathf.Lerp(0.45f, 1f, _burstAlpha)), 1f);
        }

        /// <summary>粒子层（**加色**）：柔光晕 + 硬核 —— 只在**这一层**上画（铁律 78）。</summary>
        private void DrawParticles(ParticleLayer canvas)
        {
            if (_fade <= 0.01f || _eyeH < 4f)
            {
                return;
            }

            for (int i = 0; i < _pos.Length; i++)
            {
                Texture2D? tex = _texs[Mathf.Clamp(_tex[i], 1, _texs.Length) - 1];

                if (tex == null)
                {
                    continue;
                }

                float a = _alpha[i] * _fade * _burstAlpha;
                float deg = _rot0[i] + _spin[i] * _t;
                Color col = EyePalette[_hue[i]];

                // ① 柔光晕（加色）—— 让它在真实战场上"自己发亮"（颜色跟着本颗的色档走）
                DrawTexture2D(canvas, _halo, _pos[i], _sf[i] * _eyeH * HaloScale,
                              Mathf.DegToRad(deg),
                              new Color(col.R * 0.72f, col.G * 0.78f, col.B * 0.95f, a * 0.42f), 1.6f);

                // ② 硬核 —— 形状与颜色都靠它
                DrawTexture2D(canvas, tex, _pos[i], _sf[i] * _eyeH,
                              Mathf.DegToRad(deg), new Color(col.R, col.G, col.B, Mathf.Min(1f, a * 1.35f)), 1.25f);
            }
        }

        /// <summary>按"目标宽度 + 旋转"把一张贴图画到**指定**的 CanvasItem 上（铁律 78）。</summary>
        private static void DrawTexture2D(CanvasItem canvas, Texture2D? tex, Vector2 center, float width,
                                          float angle, Color color, float gain)
        {
            if (tex == null || color.A <= 0.004f || width < 0.5f)
            {
                return;
            }

            float s = width / Mathf.Max(1f, tex.GetWidth());
            var c = new Color(color.R * gain, color.G * gain, color.B * gain, color.A);
            canvas.DrawSetTransform(center, angle, Vector2.One * s);
            canvas.DrawTexture(tex, new Vector2(-tex.GetWidth() / 2f, -tex.GetHeight() / 2f), c);
            canvas.DrawSetTransform(Vector2.Zero, 0f, Vector2.One);      // 用完必须复位
        }

        private static CanvasItemMaterial AdditiveMaterial() => new()
        {
            BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
        };
    }
}
