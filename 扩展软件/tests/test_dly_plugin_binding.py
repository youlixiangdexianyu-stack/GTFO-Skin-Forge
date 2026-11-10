from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PLUGIN_SOURCE = (
    ROOT
    / "source"
    / "Yuyu.HelAmmoDisplay"
    / "AmmoDisplayManager.cs"
)


def test_display_root_is_parented_to_the_resolved_part_transform():
    source = PLUGIN_SOURCE.read_text(encoding="utf-8")

    assert "root.transform.SetParent(anchor, false);" in source
    assert "root.transform.localPosition = anchorLocalPosition;" in source
    assert "root.transform.localRotation = anchorLocalRotation;" in source
    assert "anchor.InverseTransformPoint(worldPosition)" in source


def test_update_loop_does_not_override_bound_world_position():
    source = PLUGIN_SOURCE.read_text(encoding="utf-8")

    assert "SetPositionAndRotation" not in source
    assert "state.ModelHolder.TransformPoint(state.BaseOffset)" not in source
    assert "state.Root.transform.rotation = camera.transform.rotation;" in source


def test_editor_absolute_position_has_priority_over_normalized_fallback():
    source = PLUGIN_SOURCE.read_text(encoding="utf-8")

    absolute = source.index("if (DisplayConfig.UseAbsoluteAnchor.Value)")
    normalized = source.index(
        "else if (DisplayConfig.UsePartNormalizedAnchor.Value"
    )
    assert absolute < normalized
