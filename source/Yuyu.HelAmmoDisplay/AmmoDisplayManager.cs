using System.Globalization;
using System.Text;
using Gear;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Yuyu.HelAmmoDisplay;

internal static class AmmoDisplayManager
{
    private const uint HelArchetypeId = 66;
    private const string DisplayRootName = "Yuyu_HEL_AmmoDisplay";

    private static DisplayState activeDisplay;

    internal static void OnWield(BulletWeapon weapon)
    {
        if (weapon == null)
        {
            return;
        }

        try
        {
            ReloadConfiguration();
            if (!IsHelFirstPersonWeapon(weapon))
            {
                if (MatchesActiveWeapon(weapon))
                {
                    DestroyActiveDisplay();
                }

                return;
            }

            if (!DisplayConfig.Enabled.Value)
            {
                DestroyActiveDisplay();
                return;
            }

            if (!MatchesActiveWeapon(weapon))
            {
                DestroyActiveDisplay();
                activeDisplay = new DisplayState(weapon);
            }

            TryAttach(activeDisplay, true);
        }
        catch (Exception exception)
        {
            LogFailure("OnWield", exception);
        }
    }

    internal static void OnUpdate(BulletWeapon weapon)
    {
        if (weapon == null || !IsHelFirstPersonWeapon(weapon))
        {
            return;
        }

        try
        {
            if (!DisplayConfig.Enabled.Value)
            {
                DestroyActiveDisplay();
                return;
            }

            if (!MatchesActiveWeapon(weapon))
            {
                DestroyActiveDisplay();
                activeDisplay = new DisplayState(weapon);
            }

            DisplayState state = activeDisplay;
            if (Time.unscaledTime >= state.NextConfigurationCheck)
            {
                state.NextConfigurationCheck = Time.unscaledTime + 0.35f;
                ReloadConfiguration();
                if (BuildConfigurationSignature() != state.ConfigurationSignature)
                {
                    DestroyActiveDisplay();
                    activeDisplay = new DisplayState(weapon);
                    state = activeDisplay;
                    if (TryAttach(state, false))
                    {
                        UpdateDisplay(weapon, state);
                        HelAmmoDisplayPlugin.LogSource.LogInfo(
                            "HEL display refreshed from changed configuration.");
                    }

                    return;
                }
            }

            if (!TryAttach(state, false))
            {
                return;
            }

            UpdateDisplay(weapon, state);
        }
        catch (Exception exception)
        {
            LogFailure("Update", exception);
            DestroyActiveDisplay();
        }
    }

    internal static void OnUnWield(BulletWeapon weapon)
    {
        if (weapon != null && MatchesActiveWeapon(weapon))
        {
            DestroyActiveDisplay();
        }
    }

    internal static void OnDestroy(BulletWeapon weapon)
    {
        if (weapon != null && MatchesActiveWeapon(weapon))
        {
            DestroyActiveDisplay();
        }
    }

    internal static void Dispose()
    {
        DestroyActiveDisplay();
    }

    private static bool IsHelFirstPersonWeapon(BulletWeapon weapon)
    {
        if (weapon == null || !weapon.m_isFirstPerson)
        {
            return false;
        }

        var archetype = weapon.ArchetypeData;
        return archetype != null && archetype.persistentID == HelArchetypeId;
    }

    private static bool MatchesActiveWeapon(BulletWeapon weapon)
    {
        return activeDisplay != null
            && weapon != null
            && activeDisplay.WeaponPointer == weapon.Pointer;
    }

    private static bool TryAttach(DisplayState state, bool logSuccess)
    {
        if (state == null)
        {
            return false;
        }

        if (state.Root != null)
        {
            return true;
        }

        if (Time.unscaledTime < state.NextAttachAttempt)
        {
            return false;
        }

        state.NextAttachAttempt = Time.unscaledTime + 0.35f;

        var weapon = state.Weapon;
        if (weapon == null)
        {
            return false;
        }

        var modelHolder = weapon.m_itemModelHolder;
        if (modelHolder == null)
        {
            return false;
        }

        Transform anchor = FindAnchor(
            modelHolder,
            DisplayConfig.AnchorObjectName.Value,
            out MeshFilter meshFilter,
            out string anchorDescription);
        if (anchor == null || anchor == modelHolder || meshFilter?.sharedMesh == null)
        {
            if (Time.unscaledTime >= state.NextAnchorWarning)
            {
                state.NextAnchorWarning = Time.unscaledTime + 5f;
                HelAmmoDisplayPlugin.LogSource.LogWarning(
                    $"HEL display anchor '{DisplayConfig.AnchorObjectName.Value}' "
                    + "did not resolve to a mesh part transform; display will not be attached.");
            }

            return false;
        }

        Bounds modelBounds = BoundsInModelSpace(meshFilter, modelHolder);

        Vector3 modelPosition;
        float targetWidth = DisplayConfig.PanelWidth.Value;
        float targetHeight = DisplayConfig.PanelHeight.Value;
        float scale = Mathf.Max(0.01f, DisplayConfig.PanelScale.Value);
        targetWidth *= scale;
        targetHeight *= scale;

        if (DisplayConfig.UseAbsoluteAnchor.Value)
        {
            modelPosition = new Vector3(
                DisplayConfig.AnchorLocalX.Value,
                DisplayConfig.AnchorLocalY.Value,
                DisplayConfig.AnchorLocalZ.Value);
        }
        else if (DisplayConfig.UsePartNormalizedAnchor.Value
            && meshFilter.sharedMesh != null)
        {
            Vector3 normalized = new Vector3(
                DisplayConfig.NormalizedAnchorX.Value,
                DisplayConfig.NormalizedAnchorY.Value,
                DisplayConfig.NormalizedAnchorZ.Value);
            Vector3 minimum = modelBounds.center - modelBounds.extents;
            modelPosition = minimum + Vector3.Scale(modelBounds.size, normalized);
        }
        else if (DisplayConfig.AutoPlaceOnReceiverTop.Value
            && meshFilter.sharedMesh != null)
        {
            // Sit the panel's bottom edge on the actual part surface: sample the
            // mesh vertices under the panel footprint instead of using the whole
            // part's bounding box, which can be several centimetres too high.
            modelPosition = modelBounds.center;
            float searchX = modelPosition.x;
            float searchZ = modelPosition.z;
            float surfaceY = SurfaceHeight(
                meshFilter,
                modelHolder,
                searchX,
                searchZ,
                modelBounds.center.y + modelBounds.extents.y);
            modelPosition.y = surfaceY
                + DisplayConfig.SurfaceClearance.Value
                + targetHeight * 0.5f;
        }
        else
        {
            modelPosition = new Vector3(0f, 0.035f, 0.04f);
        }

        if (!DisplayConfig.UsePartNormalizedAnchor.Value
            && !DisplayConfig.UseAbsoluteAnchor.Value)
        {
            modelPosition += new Vector3(
                DisplayConfig.OffsetX.Value,
                DisplayConfig.OffsetY.Value,
                DisplayConfig.OffsetZ.Value);
        }

        // The editor authors positions in the weapon model space of the Forge
        // preview, which is offset from the space GTFO actually draws the weapon
        // in. ModelZOffset bridges that gap without touching the saved editor
        // coordinates.
        Vector3 placement = modelPosition + new Vector3(
            0f,
            0f,
            DisplayConfig.ModelZOffset.Value);
        Vector3 worldPosition = modelHolder.TransformPoint(placement);
        Vector3 anchorLocalPosition = anchor.InverseTransformPoint(worldPosition);
        // The DLY editor builds its panel in the model YZ plane facing +X, while
        // a Unity quad is built in the XY plane. surfaceBaseRotation maps the
        // quad onto that plane; the user rotation must then be applied in model
        // space on top of it (Qt and Unity use the same Euler order), so the
        // multiplication order matches the editor preview.
        Quaternion surfaceBaseRotation = Quaternion.Euler(0f, -90f, 0f);
        Quaternion panelRotation = Quaternion.Euler(
            DisplayConfig.RotationX.Value,
            DisplayConfig.RotationY.Value,
            DisplayConfig.RotationZ.Value) * surfaceBaseRotation;
        Quaternion desiredWorldRotation = modelHolder.rotation * panelRotation;
        Quaternion anchorLocalRotation =
            Quaternion.Inverse(anchor.rotation) * desiredWorldRotation;

        var root = new GameObject(DisplayRootName);
        Camera hudCamera = DisplayConfig.HudMode.Value ? ResolveCamera(weapon) : null;
        // In HUD mode use the Default layer so the main view camera always draws
        // it (the weapon pass uses layer 8 and does not include Default).
        int layer = hudCamera != null ? 0 : anchor.gameObject.layer;
        SetLayerRecursively(root, layer);

        if (hudCamera != null)
        {
            // Screen-space HUD: parent to the view camera so it is locked to the
            // screen and cannot drift with the weapon render pass.
            const float hudDepth = 0.5f;
            float hudWidth = Mathf.Max(
                0.0005f,
                DisplayConfig.HudScreenWidth.Value * scale);
            float aspect = DisplayConfig.PanelWidth.Value > 0.0001f
                ? DisplayConfig.PanelHeight.Value / DisplayConfig.PanelWidth.Value
                : 0.42f;
            Vector3 centre = hudCamera.ViewportToWorldPoint(
                new Vector3(0.5f, 0.5f, hudDepth));
            Vector3 edge = hudCamera.ViewportToWorldPoint(
                new Vector3(0.5f + hudWidth, 0.5f, hudDepth));
            float worldWidth = Vector3.Distance(centre, edge);
            targetWidth = worldWidth;
            targetHeight = worldWidth * aspect;
            Vector3 hudWorld = hudCamera.ViewportToWorldPoint(
                new Vector3(
                    0.5f + DisplayConfig.HudOffsetX.Value,
                    0.5f + DisplayConfig.HudOffsetY.Value,
                    hudDepth));
            root.transform.SetParent(hudCamera.transform, false);
            root.transform.position = hudWorld;
            root.transform.rotation = hudCamera.transform.rotation;
            root.transform.localScale = Vector3.one;
        }
        else
        {
            root.transform.SetParent(anchor, false);
            root.transform.localPosition = anchorLocalPosition;
            root.transform.localRotation = anchorLocalRotation;
            root.transform.localScale = Vector3.one;
        }

        GameObject border = CreatePanelQuad(
            "Border",
            root.transform,
            targetWidth + 0.0022f * scale,
            targetHeight + 0.0022f * scale,
            new Vector3(0f, 0f, 0f),
            DisplayConfig.BorderColor.Value,
            new Color(0.24f, 0.85f, 0.96f, 1f),
            DisplayConfig.BorderOpacity.Value,
            out Material borderMaterial,
            out Texture2D borderTexture);

        GameObject background = CreatePanelQuad(
            "Background",
            root.transform,
            targetWidth,
            targetHeight,
            new Vector3(0f, 0f, -0.00015f),
            DisplayConfig.PanelColor.Value,
            new Color(0.02f, 0.035f, 0.04f, 0.95f),
            DisplayConfig.PanelOpacity.Value,
            out Material backgroundMaterial,
            out Texture2D backgroundTexture);

        Color digitColor = ParseColor(DisplayConfig.TextColor.Value, new Color(0.73f, 0.98f, 1f, 1f));
        Texture2D digitTexture = CreateSolidTexture(digitColor, $"{DisplayRootName}_Digits");
        Material digitMaterial = CreateUnlitMaterial(digitTexture, digitColor);
        DigitDisplay digitDisplay = CreateDigitDisplay(
            root.transform,
            targetHeight,
            scale,
            layer,
            digitMaterial);

        state.Root = root;
        state.DigitDisplay = digitDisplay;
        state.Anchor = anchor;
        state.ModelHolder = modelHolder;
        state.ModelPosition = modelPosition;
        state.AnchorLocalPosition = anchorLocalPosition;
        state.AnchorLocalRotation = anchorLocalRotation;
        state.ConfigurationSignature = BuildConfigurationSignature();
        state.BorderMaterial = borderMaterial;
        state.BackgroundMaterial = backgroundMaterial;
        state.DigitMaterial = digitMaterial;
        state.BorderTexture = borderTexture;
        state.BackgroundTexture = backgroundTexture;
        state.DigitTexture = digitTexture;
        state.LastText = FormatClip(weapon.GetCurrentClip());
        state.DigitDisplay.SetValue(state.LastText);

        if (DisplayConfig.Diagnostics.Value)
        {
            state.DebugMarkers = CreateDebugMarkers(modelHolder, layer);
            LogDiagnostics(modelHolder, anchor, meshFilter, root, modelPosition);
            LogRenderers("item", modelHolder);
            if (modelHolder.parent != null)
            {
                LogRenderers("itemParent", modelHolder.parent);
            }
        }

        if (logSuccess)
        {
            HelAmmoDisplayPlugin.LogSource.LogInfo(
                $"HEL display parented under '{TransformPath(anchor)}' "
                + $"[{anchorDescription}] "
                + "localPosition=("
                + $"{FormatFloat(anchorLocalPosition.x)}, "
                + $"{FormatFloat(anchorLocalPosition.y)}, "
                + $"{FormatFloat(anchorLocalPosition.z)}) "
                + "localRotation=("
                + $"{FormatFloat(anchorLocalRotation.eulerAngles.x)}, "
                + $"{FormatFloat(anchorLocalRotation.eulerAngles.y)}, "
                + $"{FormatFloat(anchorLocalRotation.eulerAngles.z)}) "
                + "modelHolderScale=("
                + $"{FormatFloat(modelHolder.lossyScale.x)}, "
                + $"{FormatFloat(modelHolder.lossyScale.y)}, "
                + $"{FormatFloat(modelHolder.lossyScale.z)}) "
                + "anchorWorldPosition=("
                + $"{FormatFloat(anchor.position.x)}, "
                + $"{FormatFloat(anchor.position.y)}, "
                + $"{FormatFloat(anchor.position.z)}) "
                + $"(clip={state.LastText}).");
        }

        UpdateDisplay(weapon, state);
        return true;
    }

    private static List<GameObject> CreateDebugMarkers(
        Transform modelHolder,
        int layer)
    {
        var markers = new List<GameObject>();
        AddDebugMarker(modelHolder, new Vector3(0f, 0.12f, 0f), new Color(1f, 1f, 1f, 1f), "HEL_DBG_Center", layer, markers);
        AddDebugMarker(modelHolder, new Vector3(0f, 0.12f, 0.12f), new Color(1f, 0.15f, 0.1f, 1f), "HEL_DBG_ZPlus", layer, markers);
        AddDebugMarker(modelHolder, new Vector3(0f, 0.12f, -0.12f), new Color(0.1f, 1f, 0.2f, 1f), "HEL_DBG_ZMinus", layer, markers);
        AddDebugMarker(modelHolder, new Vector3(0.10f, 0.12f, 0f), new Color(0.2f, 0.4f, 1f, 1f), "HEL_DBG_XPlus", layer, markers);
        return markers;
    }

    private static void AddDebugMarker(
        Transform parent,
        Vector3 localPosition,
        Color color,
        string name,
        int layer,
        List<GameObject> markers)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = name;
        marker.layer = layer;
        Collider collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        MeshRenderer renderer = marker.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            Shader shader = Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var material = new Material(shader);
                material.name = name + "_Mat";
                material.color = color;
                renderer.sharedMaterial = material;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        marker.transform.SetParent(parent, false);
        marker.transform.localPosition = localPosition;
        marker.transform.localRotation = Quaternion.identity;
        marker.transform.localScale = Vector3.one * 0.008f;
        markers.Add(marker);
    }

    private static void LogDiagnostics(
        Transform modelHolder,
        Transform anchor,
        MeshFilter meshFilter,
        GameObject root,
        Vector3 modelPosition)
    {
        var text = new StringBuilder();
        text.AppendLine("HEL diagnostics:");
        text.AppendLine(
            $"  modelHolder='{TransformPath(modelHolder)}' "
            + $"localPos={FormatVector(modelHolder.localPosition)} "
            + $"localEuler={FormatVector(modelHolder.localEulerAngles)} "
            + $"localScale={FormatVector(modelHolder.localScale)} "
            + $"lossyScale={FormatVector(modelHolder.lossyScale)} "
            + $"worldPos={FormatVector(modelHolder.position)}");
        text.AppendLine(
            $"  anchor='{TransformPath(anchor)}' "
            + $"localPos={FormatVector(anchor.localPosition)} "
            + $"localEuler={FormatVector(anchor.localEulerAngles)} "
            + $"localScale={FormatVector(anchor.localScale)} "
            + $"lossyScale={FormatVector(anchor.lossyScale)} "
            + $"worldPos={FormatVector(anchor.position)}");
        text.AppendLine(
            $"  requestedModelPos={FormatVector(modelPosition)} "
            + $"panelWorldPos={FormatVector(root.transform.position)} "
            + $"panelLossyScale={FormatVector(root.transform.lossyScale)}");
        if (meshFilter?.sharedMesh != null)
        {
            Bounds source = meshFilter.sharedMesh.bounds;
            Vector3 minimum = source.center - source.extents;
            Vector3 maximum = source.center + source.extents;
            Bounds modelBounds = BoundsInModelSpace(meshFilter, modelHolder);
            text.AppendLine(
                $"  anchorMesh='{meshFilter.sharedMesh.name}' "
                + $"localMin={FormatVector(minimum)} "
                + $"localMax={FormatVector(maximum)} "
                + $"modelMin={FormatVector(modelBounds.min)} "
                + $"modelMax={FormatVector(modelBounds.max)}");
        }

        for (Transform current = anchor;
             current != null && current != modelHolder;
             current = current.parent)
        {
            text.AppendLine(
                $"  chain '{current.name}' "
                + $"localPos={FormatVector(current.localPosition)} "
                + $"localEuler={FormatVector(current.localEulerAngles)} "
                + $"localScale={FormatVector(current.localScale)}");
        }

        HelAmmoDisplayPlugin.LogSource.LogInfo(text.ToString());
    }

    private static string FormatVector(Vector3 value)
    {
        return "("
            + $"{FormatFloat(value.x)}, "
            + $"{FormatFloat(value.y)}, "
            + $"{FormatFloat(value.z)})";
    }

    private static void UpdateDisplay(BulletWeapon weapon, DisplayState state)
    {
        if (state?.Root == null || state.DigitDisplay == null || state.Anchor == null)
        {
            return;
        }

        Camera camera = ResolveCamera(weapon);
        bool shouldShow = DisplayConfig.Enabled.Value
            && weapon.m_isWielded
            && Time.timeScale > 0.001f
            && camera != null;

        FirstPersonItemHolder holder = weapon.FPItemHolder;
        if (holder != null)
        {
            shouldShow &= !holder.ForceItemHidden;
            shouldShow &= !holder.ItemHiddenTrigger;
            shouldShow &= !holder.ItemDownTrigger;
        }

        if (state.Root.activeSelf != shouldShow)
        {
            state.Root.SetActive(shouldShow);
        }

        if (!shouldShow)
        {
            return;
        }

        if (DisplayConfig.FollowItemCamera.Value)
        {
            state.Root.transform.rotation = camera.transform.rotation;
        }

        string text = FormatClip(weapon.GetCurrentClip());
        if (text != state.LastText)
        {
            state.DigitDisplay.SetValue(text);
            state.LastText = text;
        }

        TickInGameAdjustment(state, camera);

        if (DisplayConfig.Diagnostics.Value
            && Time.unscaledTime >= state.NextScreenLog)
        {
            state.NextScreenLog = Time.unscaledTime + 1f;
            Vector3 screen = camera.WorldToScreenPoint(state.Root.transform.position);
            var chain = new StringBuilder();
            Transform node = state.ModelHolder;
            for (int depth = 0; node != null && depth < 5; depth++, node = node.parent)
            {
                chain.Append($"'{node.name}'");
                chain.Append($"@{FormatVector(node.position)}");
                chain.Append($" rot={FormatVector(node.eulerAngles)}");
                chain.Append(' ');
            }

            HelAmmoDisplayPlugin.LogSource.LogInfo(
                "HEL chain "
                + chain.ToString()
                + $"| anchorWorld={FormatVector(state.Anchor.position)} "
                + $"anchorLayer={state.Anchor.gameObject.layer} "
                + $"rootLayer={state.Root.layer} "
                + $"rootParent='{(state.Root.transform.parent != null ? state.Root.transform.parent.name : "<none>")}' "
                + $"| panelWorld={FormatVector(state.Root.transform.position)} "
                + $"screen=({FormatFloat(screen.x)}, {FormatFloat(screen.y)}, {FormatFloat(screen.z)}) "
                + $"| camera='{camera.name}' fov={FormatFloat(camera.fieldOfView)} "
                + $"mask={camera.cullingMask} "
                + $"camPos={FormatVector(camera.transform.position)} "
                + $"camEuler={FormatVector(camera.transform.eulerAngles)} "
                + $"res={Screen.width}x{Screen.height}");

            if (state.DebugMarkers != null)
            {
                foreach (GameObject marker in state.DebugMarkers)
                {
                    if (marker == null)
                    {
                        continue;
                    }

                    Vector3 markerScreen = camera.WorldToScreenPoint(marker.transform.position);
                    HelAmmoDisplayPlugin.LogSource.LogInfo(
                        "HEL marker "
                        + $"'{marker.name}' "
                        + $"modelLocal={FormatVector(marker.transform.localPosition)} "
                        + $"world={FormatVector(marker.transform.position)} "
                        + $"screen=({FormatFloat(markerScreen.x)}, {FormatFloat(markerScreen.y)}, {FormatFloat(markerScreen.z)})");
                }
            }
        }
    }

    private static void LogRenderers(string label, Transform root)
    {
        try
        {
            var text = new StringBuilder();
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            text.AppendLine(
                $"HEL renderers under '{root.name}' ({label}) count={renderers.Length}");
            int index = 0;
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || index++ >= 40)
                {
                    continue;
                }

                Bounds bounds = renderer.bounds;
                string kind = renderer is SkinnedMeshRenderer
                    ? "SkinnedMeshRenderer"
                    : "MeshRenderer";
                text.AppendLine(
                    $"  {kind} '{renderer.name}' "
                    + $"enabled={renderer.enabled} "
                    + $"layer={renderer.gameObject.layer} "
                    + $"center={FormatVector(bounds.center)} "
                    + $"size={FormatVector(bounds.size)}");
            }

            HelAmmoDisplayPlugin.LogSource.LogInfo(text.ToString());
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Could not list renderers under '{label}': {exception.Message}");
        }
    }

    private static void TickInGameAdjustment(DisplayState state, Camera camera)
    {
        if (state == null || state.Root == null || state.ModelHolder == null)
        {
            return;
        }

        if (!DisplayConfig.InGameAdjust.Value)
        {
            state.AdjustMode = false;
            return;
        }

        if (Input.GetKeyDown(KeyCode.F6))
        {
            state.AdjustMode = !state.AdjustMode;
            HelAmmoDisplayPlugin.LogSource.LogInfo(
                state.AdjustMode
                    ? "HEL adjust mode ON: arrow keys = X/Y, PgUp/PgDn = Z, "
                        + "Shift = fine step, [ ] = rotate Y, F6 = exit and save."
                    : "HEL adjust mode OFF. Position saved in the plugin config.");
            return;
        }

        if (!state.AdjustMode)
        {
            return;
        }

        float step = Mathf.Max(0.0001f, DisplayConfig.AdjustStep.Value);
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            step *= 0.1f;
        }

        var delta = Vector3.zero;
        if (Input.GetKeyDown(KeyCode.LeftArrow))
        {
            delta.x -= step;
        }

        if (Input.GetKeyDown(KeyCode.RightArrow))
        {
            delta.x += step;
        }

        if (Input.GetKeyDown(KeyCode.DownArrow))
        {
            delta.y -= step;
        }

        if (Input.GetKeyDown(KeyCode.UpArrow))
        {
            delta.y += step;
        }

        if (Input.GetKeyDown(KeyCode.PageDown))
        {
            delta.z -= step;
        }

        if (Input.GetKeyDown(KeyCode.PageUp))
        {
            delta.z += step;
        }

        float rotationStep = 0f;
        if (Input.GetKeyDown(KeyCode.LeftBracket))
        {
            rotationStep -= 5f;
        }

        if (Input.GetKeyDown(KeyCode.RightBracket))
        {
            rotationStep += 5f;
        }

        if (delta == Vector3.zero && Mathf.Approximately(rotationStep, 0f))
        {
            return;
        }

        if (DisplayConfig.HudMode.Value)
        {
            // HUD mode: move the screen-space panel and rebuild so the layout
            // picks up the new viewport offset / width.
            float hudStep = 0.0015f;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                hudStep *= 0.1f;
            }

            float offsetX = DisplayConfig.HudOffsetX.Value;
            float offsetY = DisplayConfig.HudOffsetY.Value;
            float width = DisplayConfig.HudScreenWidth.Value;
            if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                offsetX -= hudStep;
            }

            if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                offsetX += hudStep;
            }

            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                offsetY += hudStep;
            }

            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                offsetY -= hudStep;
            }

            if (Input.GetKeyDown(KeyCode.PageUp))
            {
                width += hudStep * 0.2f;
            }

            if (Input.GetKeyDown(KeyCode.PageDown))
            {
                width -= hudStep * 0.2f;
            }

            DisplayConfig.HudOffsetX.Value = Mathf.Clamp(offsetX, -0.6f, 0.6f);
            DisplayConfig.HudOffsetY.Value = Mathf.Clamp(offsetY, -0.6f, 0.6f);
            DisplayConfig.HudScreenWidth.Value = Mathf.Clamp(width, 0.01f, 0.3f);
            // Force a rebuild at the next config watcher tick.
            state.ConfigurationSignature = string.Empty;

            try
            {
                HelAmmoDisplayPlugin.Instance?.Config.Save();
            }
            catch (Exception exception)
            {
                HelAmmoDisplayPlugin.LogSource.LogWarning(
                    $"Could not save HEL HUD position: {exception.Message}");
            }

            HelAmmoDisplayPlugin.LogSource.LogInfo(
                "HEL hud adjust "
                + $"offsetX={FormatFloat(DisplayConfig.HudOffsetX.Value)} "
                + $"offsetY={FormatFloat(DisplayConfig.HudOffsetY.Value)} "
                + $"width={FormatFloat(DisplayConfig.HudScreenWidth.Value)}");
            return;
        }

        if (delta != Vector3.zero && camera != null)
        {
            // The arrow keys act along the screen axes (camera right/up/forward),
            // so dragging the panel feels like moving it on screen. Convert the
            // resulting world offset back into the weapon model space the panel
            // position is stored in.
            Vector3 worldDelta = camera.transform.right * delta.x
                + camera.transform.up * delta.y
                + camera.transform.forward * delta.z;
            delta = state.ModelHolder.InverseTransformDirection(worldDelta);
        }

        state.ModelPosition += delta;
        state.Root.transform.position = state.ModelHolder.TransformPoint(
            state.ModelPosition + new Vector3(
                0f,
                0f,
                DisplayConfig.ModelZOffset.Value));
        if (!Mathf.Approximately(rotationStep, 0f))
        {
            DisplayConfig.RotationY.Value = Mathf.Repeat(
                DisplayConfig.RotationY.Value + rotationStep + 180f,
                360f) - 180f;
            Quaternion baseRotation = Quaternion.Euler(0f, -90f, 0f);
            Quaternion panelRotation = Quaternion.Euler(
                DisplayConfig.RotationX.Value,
                DisplayConfig.RotationY.Value,
                DisplayConfig.RotationZ.Value) * baseRotation;
            state.Root.transform.rotation =
                state.ModelHolder.rotation * panelRotation;
        }

        DisplayConfig.UseAbsoluteAnchor.Value = true;
        DisplayConfig.AnchorLocalX.Value = state.ModelPosition.x;
        DisplayConfig.AnchorLocalY.Value = state.ModelPosition.y;
        DisplayConfig.AnchorLocalZ.Value = state.ModelPosition.z;
        // Keep the change signature in sync so the periodic config watcher does
        // not rebuild the display while the player is dragging it.
        state.ConfigurationSignature = BuildConfigurationSignature();

        try
        {
            HelAmmoDisplayPlugin.Instance?.Config.Save();
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Could not save HEL display position: {exception.Message}");
        }

        HelAmmoDisplayPlugin.LogSource.LogInfo(
            "HEL adjust "
            + $"position={FormatVector(state.ModelPosition)} "
            + $"rotationY={FormatFloat(DisplayConfig.RotationY.Value)} "
            + $"step={FormatFloat(step)}");
    }

    private static float SurfaceHeight(
        MeshFilter meshFilter,
        Transform modelHolder,
        float x,
        float z,
        float fallback)
    {
        try
        {
            Mesh mesh = meshFilter.sharedMesh;
            if (mesh == null)
            {
                return fallback;
            }

            Vector3[] vertices = mesh.vertices;
            Transform meshTransform = meshFilter.transform;
            const float radius = 0.012f;
            float best = float.MinValue;
            int found = 0;
            foreach (Vector3 vertex in vertices)
            {
                Vector3 modelPoint = modelHolder.InverseTransformPoint(
                    meshTransform.TransformPoint(vertex));
                if (Mathf.Abs(modelPoint.x - x) > radius
                    || Mathf.Abs(modelPoint.z - z) > radius)
                {
                    continue;
                }

                if (modelPoint.y > best)
                {
                    best = modelPoint.y;
                }

                found++;
            }

            return found > 0 ? best : fallback;
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Could not sample the panel surface: {exception.Message}");
            return fallback;
        }
    }

    private static Camera ResolveCamera(BulletWeapon weapon)
    {
        FirstPersonItemHolder holder = weapon.FPItemHolder;
        // m_LookCamera is the player's view camera. m_ItemLayerCamera is only the
        // item-layer compositing camera and is often parked at the item holder,
        // so it must not be preferred for billboarding or projection maths.
        if (holder?.m_LookCamera != null)
        {
            return holder.m_LookCamera;
        }

        if (holder?.m_ItemLayerCamera != null)
        {
            return holder.m_ItemLayerCamera;
        }

        return Camera.main;
    }

    private static Transform FindAnchor(
        Transform modelHolder,
        string anchorName,
        out MeshFilter selectedFilter,
        out string description)
    {
        selectedFilter = null;
        description = "model holder fallback";
        if (modelHolder == null)
        {
            return null;
        }

        Transform bestTransform = null;
        MeshFilter bestFilter = null;
        int bestScore = int.MinValue;
        string bestMeshName = string.Empty;
        string bestMaterialName = string.Empty;

        foreach (MeshFilter filter in modelHolder.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter?.sharedMesh == null)
            {
                continue;
            }

            MeshRenderer renderer = filter.GetComponent<MeshRenderer>()
                ?? filter.GetComponentInChildren<MeshRenderer>(true);
            string meshName = filter.sharedMesh.name ?? string.Empty;
            string materialName = renderer?.sharedMaterial?.name ?? string.Empty;
            int score = ScoreMeshCandidate(filter, renderer, meshName, materialName, anchorName);

            if (score > bestScore)
            {
                bestScore = score;
                bestTransform = filter.transform;
                bestFilter = filter;
                bestMeshName = meshName;
                bestMaterialName = materialName;
            }
        }

        if (bestTransform != null && bestScore > 0)
        {
            selectedFilter = bestFilter;
            description = $"mesh='{bestMeshName}', material='{bestMaterialName}', score={bestScore}";
            return bestTransform;
        }

        if (!string.IsNullOrWhiteSpace(anchorName))
        {
            foreach (Transform transform in modelHolder.GetComponentsInChildren<Transform>(true))
            {
                if (transform.name == anchorName)
                {
                    selectedFilter = FindMeshFilter(transform);
                    description = "exact transform-name fallback";
                    return transform;
                }
            }
        }

        return modelHolder;
    }

    private static int ScoreMeshCandidate(
        MeshFilter filter,
        MeshRenderer renderer,
        string meshName,
        string materialName,
        string anchorName)
    {
        int score = 0;
        string transformName = filter.name ?? string.Empty;

        if (meshName.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }
        else if (meshName.Contains(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 700;
        }

        if (materialName.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 600;
        }
        else if (materialName.Contains(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 400;
        }

        if (transformName.Equals(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 300;
        }
        else if (transformName.Contains(anchorName, StringComparison.OrdinalIgnoreCase))
        {
            score += 200;
        }

        if (meshName.Contains("Pistol", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (meshName.Contains("Machinegun", StringComparison.OrdinalIgnoreCase)
            && !meshName.Contains("Pistol", StringComparison.OrdinalIgnoreCase))
        {
            score -= 200;
        }

        if (filter.gameObject.activeInHierarchy)
        {
            score += 20;
        }

        if (renderer?.enabled == true)
        {
            score += 10;
        }

        return score;
    }

    private static MeshFilter FindMeshFilter(Transform anchor)
    {
        MeshFilter filter = anchor.GetComponent<MeshFilter>();
        if (filter?.sharedMesh != null)
        {
            return filter;
        }

        filter = anchor.GetComponentInChildren<MeshFilter>(true);
        return filter?.sharedMesh != null ? filter : null;
    }

    private static string TransformPath(Transform transform)
    {
        if (transform == null)
        {
            return "<none>";
        }

        var path = transform.name;
        Transform current = transform.parent;
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }

        return path;
    }

    private static Bounds BoundsInModelSpace(
        MeshFilter meshFilter,
        Transform modelHolder)
    {
        Bounds source = meshFilter.sharedMesh.bounds;
        Vector3 center = source.center;
        Vector3 extents = source.extents;
        bool initialized = false;
        Bounds result = new Bounds(Vector3.zero, Vector3.zero);

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localCorner = center + Vector3.Scale(
                        extents,
                        new Vector3(x, y, z));
                    Vector3 worldCorner = meshFilter.transform.TransformPoint(localCorner);
                    Vector3 modelCorner = modelHolder.InverseTransformPoint(worldCorner);
                    if (!initialized)
                    {
                        result = new Bounds(modelCorner, Vector3.zero);
                        initialized = true;
                    }
                    else
                    {
                        result.Encapsulate(modelCorner);
                    }
                }
            }
        }

        return result;
    }

    private static void ReloadConfiguration()
    {
        try
        {
            HelAmmoDisplayPlugin.Instance?.Config.Reload();
        }
        catch (Exception exception)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                $"Could not reload HEL display configuration: {exception.Message}");
        }
    }

    private static string BuildConfigurationSignature()
    {
        return string.Join(
            "|",
            new[]
            {
                DisplayConfig.Enabled.Value ? "1" : "0",
                DisplayConfig.Diagnostics.Value ? "1" : "0",
                DisplayConfig.HudMode.Value ? "1" : "0",
                DisplayConfig.HudOffsetX.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.HudOffsetY.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.HudScreenWidth.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.InGameAdjust.Value ? "1" : "0",
                DisplayConfig.AdjustStep.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.ModelZOffset.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.AutoPlaceOnReceiverTop.Value ? "1" : "0",
                DisplayConfig.FollowItemCamera.Value ? "1" : "0",
                DisplayConfig.AnchorObjectName.Value ?? string.Empty,
                DisplayConfig.UseAbsoluteAnchor.Value ? "1" : "0",
                DisplayConfig.AnchorLocalX.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.AnchorLocalY.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.AnchorLocalZ.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.UsePartNormalizedAnchor.Value ? "1" : "0",
                DisplayConfig.NormalizedAnchorX.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.NormalizedAnchorY.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.NormalizedAnchorZ.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.OffsetX.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.OffsetY.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.OffsetZ.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.RotationX.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.RotationY.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.RotationZ.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.PanelWidth.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.PanelHeight.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.PanelScale.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.SurfaceClearance.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.TextCharacterSize.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.TextColor.Value ?? string.Empty,
                DisplayConfig.PanelColor.Value ?? string.Empty,
                DisplayConfig.PanelOpacity.Value.ToString("R", CultureInfo.InvariantCulture),
                DisplayConfig.BorderColor.Value ?? string.Empty,
                DisplayConfig.BorderOpacity.Value.ToString("R", CultureInfo.InvariantCulture)
            });
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static DigitDisplay CreateDigitDisplay(
        Transform parent,
        float panelHeight,
        float panelScale,
        int layer,
        Material material)
    {
        const int slotCount = 3;
        const int segmentCount = 7;

        float userScale = Mathf.Clamp(
            DisplayConfig.TextCharacterSize.Value / 0.00155f,
            0.25f,
            3f);
        float digitHeight = Mathf.Clamp(
            panelHeight * 0.72f * userScale,
            panelHeight * 0.35f,
            panelHeight * 1.2f);
        float digitWidth = digitHeight * 0.68f;
        float thickness = Mathf.Max(panelScale * 0.0008f, digitHeight * 0.14f);
        float digitGap = digitWidth * 0.38f;

        var slots = new DigitSlot[slotCount];
        for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            var slotObject = new GameObject($"Digit_{slotIndex}");
            slotObject.layer = layer;
            slotObject.transform.SetParent(parent, false);

            var segments = new GameObject[segmentCount];
            float halfWidth = digitWidth * 0.5f;
            float halfHeight = digitHeight * 0.5f;
            float quarterHeight = digitHeight * 0.25f;
            float verticalLength = halfHeight + thickness * 0.3f;
            float horizontalLength = digitWidth + thickness * 0.25f;

            segments[0] = CreateSegment(slotObject.transform, "A", 0f, halfHeight, horizontalLength, thickness, layer, material);
            segments[1] = CreateSegment(slotObject.transform, "B", halfWidth, quarterHeight, thickness, verticalLength, layer, material);
            segments[2] = CreateSegment(slotObject.transform, "C", halfWidth, -quarterHeight, thickness, verticalLength, layer, material);
            segments[3] = CreateSegment(slotObject.transform, "D", 0f, -halfHeight, horizontalLength, thickness, layer, material);
            segments[4] = CreateSegment(slotObject.transform, "E", -halfWidth, -quarterHeight, thickness, verticalLength, layer, material);
            segments[5] = CreateSegment(slotObject.transform, "F", -halfWidth, quarterHeight, thickness, verticalLength, layer, material);
            segments[6] = CreateSegment(slotObject.transform, "G", 0f, 0f, horizontalLength, thickness, layer, material);

            slots[slotIndex] = new DigitSlot(slotObject, segments);
        }

        return new DigitDisplay(slots, digitWidth, digitGap);
    }

    private static GameObject CreateSegment(
        Transform parent,
        string name,
        float x,
        float y,
        float width,
        float height,
        int layer,
        Material material)
    {
        GameObject segment = GameObject.CreatePrimitive(PrimitiveType.Quad);
        segment.name = $"Segment_{name}";
        segment.layer = layer;
        segment.transform.SetParent(parent, false);
        segment.transform.localPosition = new Vector3(x, y, 0f);
        segment.transform.localRotation = Quaternion.identity;
        segment.transform.localScale = new Vector3(width, height, 1f);

        Collider collider = segment.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        MeshRenderer renderer = segment.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = 3;
        }

        return segment;
    }

    private static GameObject CreatePanelQuad(
        string name,
        Transform parent,
        float width,
        float height,
        Vector3 localPosition,
        string htmlColor,
        Color fallbackColor,
        float opacity,
        out Material material,
        out Texture2D texture)
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = name;
        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = localPosition;
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale = new Vector3(width, height, 1f);
        quad.layer = parent.gameObject.layer;

        Collider collider = quad.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        Color color = ParseColor(htmlColor, fallbackColor);
        color.a = Mathf.Clamp01(opacity);
        texture = CreateSolidTexture(color, $"{DisplayRootName}_{name}");
        material = CreateUnlitMaterial(texture, color);

        MeshRenderer renderer = quad.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sortingOrder = name == "Border" ? 0 : 1;
        }

        return quad;
    }

    private static Texture2D CreateSolidTexture(Color color, string name)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.name = name;
        texture.SetPixel(0, 0, color);
        texture.Apply(false, true);
        return texture;
    }

    private static Material CreateUnlitMaterial(Texture2D texture, Color color)
    {
        Shader shader = Shader.Find("Sprites/Default")
            ?? Shader.Find("UI/Default")
            ?? Shader.Find("Unlit/Texture");

        if (shader == null)
        {
            HelAmmoDisplayPlugin.LogSource.LogWarning(
                "No unlit UI shader was found; the panel will use Unity's primitive material.");
            return null;
        }

        var material = new Material(shader);
        material.name = $"{DisplayRootName}_Panel";
        material.mainTexture = texture;
        material.color = color;
        return material;
    }

    private static Color ParseColor(string html, Color fallback)
    {
        return !string.IsNullOrWhiteSpace(html)
            && ColorUtility.TryParseHtmlString(html.Trim(), out Color parsed)
            ? parsed
            : fallback;
    }

    private static string FormatClip(int value)
    {
        return Mathf.Max(0, value).ToString("D2", CultureInfo.InvariantCulture);
    }

    private static void SetLayerRecursively(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        Transform transform = gameObject.transform;
        for (int index = 0; index < transform.childCount; index++)
        {
            SetLayerRecursively(transform.GetChild(index).gameObject, layer);
        }
    }

    private static void DestroyActiveDisplay()
    {
        DisplayState state = activeDisplay;
        activeDisplay = null;
        if (state == null)
        {
            return;
        }

        if (state.Root != null)
        {
            Object.Destroy(state.Root);
        }

        if (state.DebugMarkers != null)
        {
            foreach (GameObject marker in state.DebugMarkers)
            {
                if (marker != null)
                {
                    Object.Destroy(marker);
                }
            }

            state.DebugMarkers = null;
        }

        if (state.BorderMaterial != null)
        {
            Object.Destroy(state.BorderMaterial);
        }

        if (state.BackgroundMaterial != null)
        {
            Object.Destroy(state.BackgroundMaterial);
        }

        if (state.DigitMaterial != null)
        {
            Object.Destroy(state.DigitMaterial);
        }

        if (state.BorderTexture != null)
        {
            Object.Destroy(state.BorderTexture);
        }

        if (state.BackgroundTexture != null)
        {
            Object.Destroy(state.BackgroundTexture);
        }

        if (state.DigitTexture != null)
        {
            Object.Destroy(state.DigitTexture);
        }
    }

    private static void LogFailure(string stage, Exception exception)
    {
        HelAmmoDisplayPlugin.LogSource?.LogWarning(
            $"HEL ammo display disabled after {stage} failure: {exception}");
    }

    private sealed class DisplayState
    {
        internal DisplayState(BulletWeapon weapon)
        {
            BulletWeapon = weapon;
            WeaponPointer = weapon.Pointer;
        }

        internal IntPtr WeaponPointer { get; }
        internal BulletWeapon Weapon => BulletWeapon != null && BulletWeapon.Pointer == WeaponPointer
            ? BulletWeapon
            : null;

        internal BulletWeapon BulletWeapon { get; set; }
        internal GameObject Root { get; set; }
        internal DigitDisplay DigitDisplay { get; set; }
        internal Transform Anchor { get; set; }
        internal Transform ModelHolder { get; set; }
        internal Vector3 ModelPosition { get; set; }
        internal bool AdjustMode { get; set; }
        internal List<GameObject> DebugMarkers { get; set; }
        internal Vector3 AnchorLocalPosition { get; set; }
        internal Quaternion AnchorLocalRotation { get; set; }
        internal Material BorderMaterial { get; set; }
        internal Material BackgroundMaterial { get; set; }
        internal Material DigitMaterial { get; set; }
        internal Texture2D BorderTexture { get; set; }
        internal Texture2D BackgroundTexture { get; set; }
        internal Texture2D DigitTexture { get; set; }
        internal string LastText { get; set; }
        internal float NextAttachAttempt { get; set; }
        internal float NextAnchorWarning { get; set; }
        internal float NextScreenLog { get; set; }
        internal float NextConfigurationCheck { get; set; }
        internal string ConfigurationSignature { get; set; } = string.Empty;
    }

    private sealed class DigitDisplay
    {
        private static readonly int[] SegmentMasks =
        {
            0b0111111,
            0b0000110,
            0b1011011,
            0b1001111,
            0b1100110,
            0b1101101,
            0b1111101,
            0b0000111,
            0b1111111,
            0b1101111
        };

        private readonly DigitSlot[] slots;
        private readonly float digitWidth;
        private readonly float digitGap;
        private string currentValue;

        internal DigitDisplay(DigitSlot[] slots, float digitWidth, float digitGap)
        {
            this.slots = slots;
            this.digitWidth = digitWidth;
            this.digitGap = digitGap;
        }

        internal void SetValue(string value)
        {
            if (value == currentValue)
            {
                return;
            }

            currentValue = value;
            if (value.Length < 2)
            {
                value = "0" + value;
            }

            if (value.Length > slots.Length)
            {
                value = value.Substring(value.Length - slots.Length);
            }

            int firstSlot = slots.Length - value.Length;
            float step = digitWidth + digitGap;
            float startX = -(value.Length - 1) * step * 0.5f;

            for (int index = 0; index < slots.Length; index++)
            {
                DigitSlot slot = slots[index];
                int characterIndex = index - firstSlot;
                bool visible = characterIndex >= 0;
                slot.Root.SetActive(visible);

                if (!visible)
                {
                    continue;
                }

                slot.Root.transform.localPosition = new Vector3(
                    startX + characterIndex * step,
                    0f,
                    -0.00035f);
                slot.SetDigit(value[characterIndex]);
            }
        }

        internal static int GetSegmentMask(int digit)
        {
            return SegmentMasks[digit];
        }
    }

    private sealed class DigitSlot
    {
        private readonly GameObject[] segments;

        internal DigitSlot(GameObject root, GameObject[] segments)
        {
            Root = root;
            this.segments = segments;
        }

        internal GameObject Root { get; }

        internal void SetDigit(char digit)
        {
            int digitIndex = digit - '0';
            int mask = digitIndex >= 0 && digitIndex <= 9
                ? DigitDisplay.GetSegmentMask(digitIndex)
                : 0;

            for (int index = 0; index < segments.Length; index++)
            {
                segments[index].SetActive((mask & (1 << index)) != 0);
            }
        }
    }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.OnWield))]
internal static class BulletWeaponOnWieldPatch
{
    private static void Postfix(BulletWeapon __instance)
    {
        EmissiveTest.OnWield(__instance);
        EmissiveDisplay.OnWield(__instance);
        AmmoDisplayManager.OnWield(__instance);
    }
}

[HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.OnUnWield))]
internal static class BulletWeaponOnUnWieldPatch
{
    private static void Postfix(BulletWeapon __instance)
    {
        AmmoDisplayManager.OnUnWield(__instance);
    }
}

[HarmonyPatch(typeof(BulletWeapon), "Update")]
internal static class BulletWeaponUpdatePatch
{
    private static void Postfix(BulletWeapon __instance)
    {
        AmmoDisplayManager.OnUpdate(__instance);
        EmissiveTest.OnUpdate(__instance);
        EmissiveDisplay.OnUpdate(__instance);
    }
}

[HarmonyPatch(typeof(BulletWeapon), "OnDestroy")]
internal static class BulletWeaponOnDestroyPatch
{
    private static void Postfix(BulletWeapon __instance)
    {
        AmmoDisplayManager.OnDestroy(__instance);
    }
}


