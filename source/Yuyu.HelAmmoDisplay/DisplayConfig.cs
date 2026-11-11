using BepInEx.Configuration;

namespace Yuyu.HelAmmoDisplay;

internal static class DisplayConfig
{
    private const string Section = "HEL Ammo Display";

    internal static ConfigEntry<bool> Enabled { get; private set; }
    internal static ConfigEntry<bool> EmissiveTest { get; private set; }
    internal static ConfigEntry<bool> EmissiveDisplay { get; private set; }
    internal static ConfigEntry<float> EmissiveU { get; private set; }
    internal static ConfigEntry<float> EmissiveV { get; private set; }
    internal static ConfigEntry<float> EmissiveWidth { get; private set; }
    internal static ConfigEntry<bool> Diagnostics { get; private set; }
    internal static ConfigEntry<bool> HudMode { get; private set; }
    internal static ConfigEntry<float> HudOffsetX { get; private set; }
    internal static ConfigEntry<float> HudOffsetY { get; private set; }
    internal static ConfigEntry<float> HudScreenWidth { get; private set; }
    internal static ConfigEntry<bool> InGameAdjust { get; private set; }
    internal static ConfigEntry<float> AdjustStep { get; private set; }
    internal static ConfigEntry<float> ModelZOffset { get; private set; }
    internal static ConfigEntry<bool> AutoPlaceOnReceiverTop { get; private set; }
    internal static ConfigEntry<bool> FollowItemCamera { get; private set; }
    internal static ConfigEntry<string> AnchorObjectName { get; private set; }
    internal static ConfigEntry<bool> UseAbsoluteAnchor { get; private set; }
    internal static ConfigEntry<float> AnchorLocalX { get; private set; }
    internal static ConfigEntry<float> AnchorLocalY { get; private set; }
    internal static ConfigEntry<float> AnchorLocalZ { get; private set; }
    internal static ConfigEntry<bool> UsePartNormalizedAnchor { get; private set; }
    internal static ConfigEntry<float> NormalizedAnchorX { get; private set; }
    internal static ConfigEntry<float> NormalizedAnchorY { get; private set; }
    internal static ConfigEntry<float> NormalizedAnchorZ { get; private set; }
    internal static ConfigEntry<float> OffsetX { get; private set; }
    internal static ConfigEntry<float> OffsetY { get; private set; }
    internal static ConfigEntry<float> OffsetZ { get; private set; }
    internal static ConfigEntry<float> RotationX { get; private set; }
    internal static ConfigEntry<float> RotationY { get; private set; }
    internal static ConfigEntry<float> RotationZ { get; private set; }
    internal static ConfigEntry<float> PanelWidth { get; private set; }
    internal static ConfigEntry<float> PanelHeight { get; private set; }
    internal static ConfigEntry<float> PanelScale { get; private set; }
    internal static ConfigEntry<float> SurfaceClearance { get; private set; }
    internal static ConfigEntry<float> TextCharacterSize { get; private set; }
    internal static ConfigEntry<string> TextColor { get; private set; }
    internal static ConfigEntry<string> PanelColor { get; private set; }
    internal static ConfigEntry<float> PanelOpacity { get; private set; }
    internal static ConfigEntry<string> BorderColor { get; private set; }
    internal static ConfigEntry<float> BorderOpacity { get; private set; }

    internal static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            Section,
            "Enabled",
            true,
            "Enable the HEL first-person ammunition display.");

        EmissiveTest = config.Bind(
            Section,
            "EmissiveTest",
            false,
            "Paint a test marker into the HEL front part's emissive texture "
            + "(verified the texture-skin attachment path).");

        EmissiveDisplay = config.Bind(
            Section,
            "EmissiveDisplay",
            true,
            "Draw the live magazine count into the HEL front part's emissive texture, "
            + "so the digits are part of the weapon material (glued to the model).");

        EmissiveU = config.Bind(
            Section,
            "EmissiveU",
            0.5f,
            new ConfigDescription(
                "Horizontal centre of the display on the emissive texture (0..1).",
                new AcceptableValueRange<float>(0f, 1f)));

        EmissiveV = config.Bind(
            Section,
            "EmissiveV",
            0.5f,
            new ConfigDescription(
                "Vertical centre of the display on the emissive texture (0..1).",
                new AcceptableValueRange<float>(0f, 1f)));

        EmissiveWidth = config.Bind(
            Section,
            "EmissiveWidth",
            0.10f,
            new ConfigDescription(
                "Display width as a fraction of the emissive texture.",
                new AcceptableValueRange<float>(0.005f, 0.5f)));

        Diagnostics = config.Bind(
            Section,
            "Diagnostics",
            false,
            "Log weapon-part transforms and draw model-space debug markers "
            + "(white=origin, red=+Z, green=-Z, blue=+Y).");

        InGameAdjust = config.Bind(
            Section,
            "InGameAdjust",
            true,
            "While the HEL display is visible: press F6 to toggle in-game adjustment, "
            + "then use arrow keys (X/Y), PageUp/PageDown (Z) and Shift for fine steps.");

        HudMode = config.Bind(
            Section,
            "HudMode",
            true,
            "Render the counter as a screen-space HUD locked to the camera (stable, "
            + "independent of the weapon render pass). Disable to use the 3D weapon-space panel.");

        HudOffsetX = config.Bind(
            Section,
            "HudOffsetX",
            0.16f,
            new ConfigDescription(
                "HUD horizontal offset from screen centre, in screen widths.",
                new AcceptableValueRange<float>(-0.6f, 0.6f)));

        HudOffsetY = config.Bind(
            Section,
            "HudOffsetY",
            -0.12f,
            new ConfigDescription(
                "HUD vertical offset from screen centre, in screen heights.",
                new AcceptableValueRange<float>(-0.6f, 0.6f)));

        HudScreenWidth = config.Bind(
            Section,
            "HudScreenWidth",
            0.055f,
            new ConfigDescription(
                "HUD width as a fraction of the screen width.",
                new AcceptableValueRange<float>(0.01f, 0.3f)));

        AdjustStep = config.Bind(
            Section,
            "AdjustStep",
            0.005f,
            new ConfigDescription(
                "In-game adjustment step in metres (Shift divides it by 10).",
                new AcceptableValueRange<float>(0.0005f, 0.05f)));

        ModelZOffset = config.Bind(
            Section,
            "ModelZOffset",
            -0.29f,
            new ConfigDescription(
                "Constant Z offset (metres) between the weapon model space used by the "
                + "editor and the space in which the weapon is actually drawn. The editor "
                + "coordinates are kept untouched; this offset is applied when placing the panel.",
                new AcceptableValueRange<float>(-1f, 1f)));

        AutoPlaceOnReceiverTop = config.Bind(
            Section,
            "AutoPlaceOnReceiverTop",
            true,
            "Place the panel at the center of the selected part's top surface. Disable to use the raw offset values.");

        FollowItemCamera = config.Bind(
            Section,
            "FollowItemCamera",
            true,
            "Keep the panel facing the first-person item camera.");

        AnchorObjectName = config.Bind(
            Section,
            "AnchorObjectName",
            "Receiver_UZI_1",
            "Name of the weapon model transform used as the display anchor.");

        UseAbsoluteAnchor = config.Bind(
            Section,
            "UseAbsoluteAnchor",
            false,
            "Use AnchorLocalX/Y/Z directly as weapon-model-space position. OffsetX/Y/Z are ignored in this mode.");

        AnchorLocalX = config.Bind(
            Section,
            "AnchorLocalX",
            0f,
            new ConfigDescription("Absolute X position in weapon-model space.", new AcceptableValueRange<float>(-2f, 2f)));

        AnchorLocalY = config.Bind(
            Section,
            "AnchorLocalY",
            0f,
            new ConfigDescription("Absolute Y position in weapon-model space.", new AcceptableValueRange<float>(-2f, 2f)));

        AnchorLocalZ = config.Bind(
            Section,
            "AnchorLocalZ",
            0f,
            new ConfigDescription("Absolute Z position in weapon-model space.", new AcceptableValueRange<float>(-2f, 2f)));

        UsePartNormalizedAnchor = config.Bind(
            Section,
            "UsePartNormalizedAnchor",
            true,
            "Place the panel by normalized X/Y/Z coordinates inside AnchorObjectName's runtime mesh bounds.");

        NormalizedAnchorX = config.Bind(
            Section,
            "NormalizedAnchorX",
            0.5f,
            new ConfigDescription("Normalized X inside the selected part bounds; 0 is minimum, 1 is maximum.", new AcceptableValueRange<float>(-4f, 5f)));

        NormalizedAnchorY = config.Bind(
            Section,
            "NormalizedAnchorY",
            1f,
            new ConfigDescription("Normalized Y inside the selected part bounds; 0 is minimum, 1 is maximum.", new AcceptableValueRange<float>(-4f, 5f)));

        NormalizedAnchorZ = config.Bind(
            Section,
            "NormalizedAnchorZ",
            0.5f,
            new ConfigDescription("Normalized Z inside the selected part bounds; 0 is minimum, 1 is maximum.", new AcceptableValueRange<float>(-4f, 5f)));

        OffsetX = config.Bind(
            Section,
            "OffsetX",
            0f,
            new ConfigDescription("Horizontal offset from the anchor.", new AcceptableValueRange<float>(-0.2f, 0.2f)));

        OffsetY = config.Bind(
            Section,
            "OffsetY",
            0.03f,
            new ConfigDescription("Lengthwise offset from the anchor.", new AcceptableValueRange<float>(-0.2f, 0.2f)));

        OffsetZ = config.Bind(
            Section,
            "OffsetZ",
            0f,
            new ConfigDescription("Vertical/surface offset from the anchor.", new AcceptableValueRange<float>(-0.2f, 0.2f)));

        RotationX = config.Bind(
            Section,
            "RotationX",
            0f,
            new ConfigDescription("Panel X rotation in degrees when FollowItemCamera is false.", new AcceptableValueRange<float>(-180f, 180f)));

        RotationY = config.Bind(
            Section,
            "RotationY",
            0f,
            new ConfigDescription("Panel Y rotation in degrees when FollowItemCamera is false.", new AcceptableValueRange<float>(-180f, 180f)));

        RotationZ = config.Bind(
            Section,
            "RotationZ",
            0f,
            new ConfigDescription("Panel Z rotation in degrees when FollowItemCamera is false.", new AcceptableValueRange<float>(-180f, 180f)));

        PanelWidth = config.Bind(
            Section,
            "PanelWidth",
            0.030f,
            new ConfigDescription(
                "Panel width in world units when AutoPlaceOnReceiverTop is disabled or no mesh bounds are available.",
                new AcceptableValueRange<float>(0.005f, 0.1f)));

        PanelHeight = config.Bind(
            Section,
            "PanelHeight",
            0.0125f,
            new ConfigDescription(
                "Panel height in world units when AutoPlaceOnReceiverTop is disabled or no mesh bounds are available.",
                new AcceptableValueRange<float>(0.003f, 0.05f)));

        PanelScale = config.Bind(
            Section,
            "PanelScale",
            1f,
            new ConfigDescription("Overall display scale.", new AcceptableValueRange<float>(0.2f, 3f)));

        SurfaceClearance = config.Bind(
            Section,
            "SurfaceClearance",
            0.0015f,
            new ConfigDescription(
                "Distance above the receiver surface to prevent z-fighting.",
                new AcceptableValueRange<float>(0f, 0.02f)));

        TextCharacterSize = config.Bind(
            Section,
            "TextCharacterSize",
            0.00155f,
            new ConfigDescription(
                "Seven-segment digit scale. The default 0.00155 is the normal size.",
                new AcceptableValueRange<float>(0.0002f, 0.01f)));

        TextColor = config.Bind(
            Section,
            "TextColor",
            "#B9FBFF",
            "Digit color in HTML hex format.");

        PanelColor = config.Bind(
            Section,
            "PanelColor",
            "#05090AF2",
            "Panel background color in HTML hex format (RRGGBBAA).");

        PanelOpacity = config.Bind(
            Section,
            "PanelOpacity",
            0.95f,
            new ConfigDescription("Panel background opacity.", new AcceptableValueRange<float>(0f, 1f)));

        BorderColor = config.Bind(
            Section,
            "BorderColor",
            "#3EDAF5",
            "Panel border color in HTML hex format.");

        BorderOpacity = config.Bind(
            Section,
            "BorderOpacity",
            1f,
            new ConfigDescription("Panel border opacity.", new AcceptableValueRange<float>(0f, 1f)));
    }
}
