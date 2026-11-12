using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace ForgeSkin.Plugin;

// In-game skin picker: every target with a loaded skin, its skins by name plus vanilla. The component stays disabled
// while closed, so Unity calls its OnGUI only while the list is open; closed, the runner checks one key per frame.
public sealed class Picker : MonoBehaviour
{
    public Picker(IntPtr pointer) : base(pointer) { }

    internal static KeyCode Key = KeyCode.F8;
    internal static Picker? View;
    static bool _open;
    static CursorLockMode _lock;
    static bool _visible;
    static Vector2 _scroll;
    static readonly Il2CppReferenceArray<GUILayoutOption> NoOptions = new(0);

    [HideFromIl2Cpp]
    internal static void Poll()
    {
        if (!Input.GetKeyDown(Key) && !(_open && Input.GetKeyDown(KeyCode.Escape))) return;
        _open = !_open;
        if (_open) (_lock, _visible) = (Cursor.lockState, Cursor.visible);
        else (Cursor.lockState, Cursor.visible) = (_lock, _visible);
        View!.enabled = _open;
    }

    public void OnGUI()
    {
        // OnGUI runs after the game's Update and LateUpdate, so the cursor stays free even if the game locks it each frame.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        GUILayout.BeginArea(new Rect(20, 20, 440, Mathf.Min(640, Screen.height - 40)), "ForgeSkin", GUI.skin.window);
        _scroll = GUILayout.BeginScrollView(_scroll, NoOptions);
        if (Skins.Candidates.Count == 0) GUILayout.Label("No skins loaded", NoOptions);
        foreach (var (key, skins) in Skins.Candidates)
        {
            var current = Skins.Targets.GetValueOrDefault(key, "");
            GUILayout.Label($"{skins[0].Doc.Target.Weapon ?? key} ({key})", NoOptions);
            foreach (var skin in skins) Button(key, skin.Doc.Name.Length > 0 ? skin.Doc.Name : skin.Doc.Id, skin.Doc.Id, current);
            Button(key, "原版 / Vanilla", "", current);
        }
        GUI.color = Color.white;
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    [HideFromIl2Cpp]
    static void Button(string key, string text, string id, string current)
    {
        GUI.color = id == current ? Color.green : Color.white;
        if (GUILayout.Button(text, NoOptions) && id != current) Skins.Choose(key, id);
    }
}
