using System;
using Godot;
using MegaCrit.Sts2.Core.Logging;
// Godot 里也有一个 Logger，会和 MegaCrit 的撞名，所以起别名。
using Logger = MegaCrit.Sts2.Core.Logging.Logger;

namespace RayMod.Art;

/// <summary>
/// <b>玩家选中的皮肤</b>（全局单例 + 落盘）。
///
/// <para><b>为什么自己写持久化，而不是直接用 RitsuLib 的数据存储</b>：
/// 这里的读盘时机很关键 —— 它必须发生在<b>第一场战斗的形象场景实例化之前</b>，
/// 也就是 <c>Entry.Initialize()</c> 里。用自己的 <c>ConfigFile</c> 就完全掌控时机和失败行为
/// （读不到就用默认皮肤，绝不因为一个配置文件把游戏搞崩）。</para>
///
/// <para>存档位置：<c>user://raymod_skin.cfg</c>（Godot 的 user 目录，跟着存档走）。</para>
///
/// <para><b>⚠️ 换肤是"下一场战斗生效"</b>：战斗中的形象节点已经建好了，
/// 中途换骨架会把正在播的动画重置掉（见交接文档铁律 18）。
/// 要做"当场就换"必须在换完骨架之后重播动画 —— 那是注册表里
/// <c>CombatIdleAnimationGuardPatch</c> 的活，目前没接。</para>
/// </summary>
public static class RaySkinState
{
    private const string ConfigPath = "user://raymod_skin.cfg";
    private const string ConfigSection = "skin";
    private const string ConfigKey = "selected";

    private static readonly Logger Log = new("RayMod.Skin", LogType.Generic);

    private static RaySkin _selected = RaySkinCatalog.Default;
    private static bool _loaded;

    /// <summary>当前选中的皮肤。读它<b>不会</b>触发落盘；写它也不会（落盘要显式调 <see cref="Save"/>）。</summary>
    public static RaySkin Selected
    {
        get
        {
            EnsureLoaded();
            return _selected;
        }

        set
        {
            // 不认识的枚举值（存档手改 / 版本回退）直接忽略，不让坏值把形象搞没。
            if (!RaySkinCatalog.IsKnown(value))
            {
                Log.Warn($"[皮肤] 忽略无法识别的皮肤值：{value}");
                return;
            }

            _selected = value;
        }
    }

    /// <summary>读一次盘（幂等）。<c>Entry.Initialize()</c> 会显式调用，好让日志里早点出现这一行。</summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            var config = new ConfigFile();
            Error err = config.Load(ConfigPath);

            if (err != Error.Ok)
            {
                // 首次运行：文件还不存在，这不是错误。
                Log.Info($"[皮肤] 没有存档（{ConfigPath}），用默认皮肤 {RaySkinCatalog.DisplayName(_selected)}");
                return;
            }

            string raw = config.GetValue(ConfigSection, ConfigKey, _selected.ToString()).AsString();

            if (Enum.TryParse(raw, ignoreCase: true, out RaySkin parsed) && RaySkinCatalog.IsKnown(parsed))
            {
                _selected = parsed;
                Log.Info($"[皮肤] 读到选择：{RaySkinCatalog.DisplayName(_selected)}");
            }
            else
            {
                Log.Warn($"[皮肤] 存档里的皮肤 id 无法识别（'{raw}'）→ 回退到默认 {RaySkinCatalog.DisplayName(_selected)}");
            }
        }
        catch (Exception e)
        {
            // 配置读失败绝不能影响游戏：用默认皮肤继续。
            Log.Warn($"[皮肤] 读取失败，用默认皮肤继续：{e.Message}");
        }
    }

    /// <summary>把当前选择写盘。设置页改动后会调它。</summary>
    public static void Save()
    {
        try
        {
            var config = new ConfigFile();
            config.SetValue(ConfigSection, ConfigKey, _selected.ToString());
            Error err = config.Save(ConfigPath);

            if (err == Error.Ok)
            {
                Log.Info($"[皮肤] 已保存：{RaySkinCatalog.DisplayName(_selected)}（下一场战斗生效）");
            }
            else
            {
                Log.Warn($"[皮肤] 保存失败：{err}（本次游戏内仍然生效，重启会丢）");
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[皮肤] 保存异常：{e.Message}");
        }
    }
}
