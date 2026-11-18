using UnityEngine;

namespace ForgeUI;

/// <summary>A <c>Rect</c>'s edges and size read from its four fields: in the game's interop every <c>Rect</c> property (<c>width</c>,
/// <c>xMax</c>, <c>center</c>) is a native call, and the menu and HUD drawing reads them on every drawn frame (contract P8, hot-path getters).</summary>
internal static class RectReads
{
    internal static float Width(this Rect r) => r.m_Width;
    internal static float Height(this Rect r) => r.m_Height;
    internal static float XMin(this Rect r) => r.m_XMin;
    internal static float YMin(this Rect r) => r.m_YMin;
    internal static float XMax(this Rect r) => r.m_XMin + r.m_Width;
    internal static float YMax(this Rect r) => r.m_YMin + r.m_Height;
    internal static float CenterX(this Rect r) => r.m_XMin + r.m_Width * 0.5f;
    internal static float CenterY(this Rect r) => r.m_YMin + r.m_Height * 0.5f;
}
