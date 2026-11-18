using UnityEngine;

namespace ForgeCore;

/// <summary>The game's own world markers (<c>GuiManager.NavMarkerLayer</c>): place one on an object and take it off again. Styling, text and
/// colour are set on the returned <see cref="NavMarker"/> by the caller. Main thread, local to this machine.</summary>
public static class NavMarkers
{
    /// <summary>The game's marker layer exists now.</summary>
    public static bool Available => GuiManager.NavMarkerLayer is { WasCollected: false };

    /// <summary>The marker layer itself, for the caller that has to read it (a freshly placed marker's parts); null when it is not available.</summary>
    public static NavMarkerLayer? Layer => GuiManager.NavMarkerLayer is { WasCollected: false } layer ? layer : null;

    /// <summary>Places a marker that follows <paramref name="tracking"/>. Null when the layer is not available or the game refuses.</summary>
    public static NavMarker? Place(NavMarkerOption options, GameObject tracking, string title, float destroyDelay = 0f)
    {
        var marker = Layer?.PlaceCustomMarker(options, tracking, title, destroyDelay, false);
        return marker == null || marker.WasCollected ? null : marker;
    }

    /// <summary>Takes a marker off the layer. Safe when the marker or the layer is already gone.</summary>
    public static void Remove(NavMarker marker)
    {
        if (!marker.WasCollected) Layer?.RemoveMarker(marker);
    }
}
