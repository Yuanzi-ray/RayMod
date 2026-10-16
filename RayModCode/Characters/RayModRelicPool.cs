using Godot;
using STS2RitsuLib.Scaffolding.Content;

namespace RayMod.Characters;

public sealed class RayModRelicPool : TypeListRelicPoolModel
{
    public override string EnergyColorName => "RayMod";
    public override Color LabOutlineColor => RayModCharacter.ThemeColor;

    // 遗物实验室和文本也会读取池子的能量图标路径。
    // 资源路径以 res:// 开头，并且要能在 PCK 内找到对应文件。
    public override string? BigEnergyIconPath => $"{Entry.ResPath}/images/characters/energy_big.png";
    public override string? TextEnergyIconPath => $"{Entry.ResPath}/images/characters/energy_text.png";
}
