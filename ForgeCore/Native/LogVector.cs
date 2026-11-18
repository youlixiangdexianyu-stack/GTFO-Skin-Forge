using UnityEngine;

namespace ForgeCore;

public static partial class Log
{
    /// <summary>A position for a message, formatted like <see cref="Pos(float, float, float)"/>.</summary>
    public static string Pos(Vector3 position) => Pos(position.x, position.y, position.z);
}
