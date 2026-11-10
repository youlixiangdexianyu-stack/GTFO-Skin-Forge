from __future__ import annotations

import json
import os
from pathlib import Path

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtWidgets import QApplication
from PySide6.QtGui import QVector3D

from gtfo_skin_forge.dly import (
    DLY_PLUGIN_FOLDER,
    DlyDisplayPreset,
    find_gtfo_profile_root,
    load_dly_preset,
    plugin_config_path,
    save_dly_preset,
    write_plugin_config,
)
from gtfo_skin_forge.dly_viewer import DlyPreviewView
from gtfo_skin_forge.dly_workspace import DlyWorkspace
from gtfo_skin_forge.ui import MainWindow


def test_dly_preset_round_trip_and_part_selection(tmp_path):
    preset = DlyDisplayPreset(
        offset_x=0.012,
        rotation_y=25.0,
        text_color="#12ABCD",
        preview_clip=73,
        panel_opacity=0.42,
        border_opacity=0.35,
        last_transform_tool="rotate",
        fixed_view_preference="top",
        surface_snap_enabled=True,
        use_part_normalized_anchor=True,
        normalized_anchor_x=0.25,
        normalized_anchor_y=0.75,
        normalized_anchor_z=0.6,
    )
    preset.select_part("mag", reset_offsets=False)
    path = tmp_path / "test.gtfdly"
    save_dly_preset(path, preset)
    loaded = load_dly_preset(path)

    assert loaded.anchor_part_id == "mag"
    assert loaded.anchor_name == "Mag_9mm_50rdns_A"
    assert loaded.offset_x == pytest.approx(0.012)
    assert loaded.rotation_y == pytest.approx(25.0)
    assert loaded.text_color == "#12ABCD"
    assert loaded.preview_clip == 73
    assert loaded.panel_opacity == pytest.approx(0.42)
    assert loaded.border_opacity == pytest.approx(0.35)
    assert loaded.last_transform_tool == "rotate"
    assert loaded.fixed_view_preference == "top"
    assert loaded.surface_snap_enabled is True
    assert loaded.use_part_normalized_anchor is True
    assert loaded.normalized_anchor_x == pytest.approx(0.25)
    assert loaded.normalized_anchor_y == pytest.approx(0.75)
    assert loaded.normalized_anchor_z == pytest.approx(0.6)


def test_find_profile_and_write_plugin_config_preserves_unknown_keys(
    tmp_path,
):
    profile = tmp_path / "profile"
    plugin_dir = profile / DLY_PLUGIN_FOLDER
    plugin_dir.mkdir(parents=True)
    (plugin_dir / "Yuyu.HelAmmoDisplay.dll").write_bytes(b"dll")
    (profile / "winhttp.dll").write_bytes(b"doorstop")

    found = find_gtfo_profile_root(start=profile / "nested")
    assert found == profile

    config_path = plugin_config_path(profile)
    config_path.parent.mkdir(parents=True, exist_ok=True)
    config_path.write_text(
        "\n".join(
            [
                "[HEL Ammo Display]",
                "Enabled = false",
                "CustomKeepMe = 42",
                "",
                "[Other]",
                "Value = untouched",
            ]
        ),
        encoding="utf-8",
    )
    preset = DlyDisplayPreset(
        anchor_part_id="sight",
        anchor_name="Front_Pistol_Sight_2_E",
        anchor_local_x=0.004,
        anchor_local_y=0.05,
        anchor_local_z=-0.02,
        use_absolute_anchor=True,
        offset_x=0.004,
        rotation_z=-12.0,
        panel_width=0.034,
        face_player=False,
        use_part_normalized_anchor=True,
    )
    write_plugin_config(profile, preset)
    text = config_path.read_text(encoding="utf-8")

    assert "Enabled = true" in text
    assert "AnchorObjectName = Front_Pistol_Sight_2_E" in text
    assert "OffsetX = 0" in text
    assert "OffsetY = 0" in text
    assert "OffsetZ = 0" in text
    assert "RotationZ = -12" in text
    assert "FollowItemCamera = false" in text
    assert "AutoPlaceOnReceiverTop = false" in text
    assert "UseAbsoluteAnchor = true" in text
    assert "UsePartNormalizedAnchor = false" in text
    assert "NormalizedAnchorX = 0.5" in text
    assert "NormalizedAnchorY = 1" in text
    assert "NormalizedAnchorZ = 0.5" in text
    assert "PanelOpacity = 0.95" in text
    assert "BorderOpacity = 1" in text
    assert "CustomKeepMe = 42" in text
    assert "Value = untouched" in text


def test_dly_workspace_is_separate_and_does_not_change_skin_state(
    forge_data,
    monkeypatch,
):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    assert window.workspace_tabs.count() == 2
    original_item = window.current_item.id if window.current_item else ""
    original_part = window.current_part_id
    original_rows = window.table.rowCount()

    monkeypatch.setattr(
        "gtfo_skin_forge.dly_workspace.DlyWorkspace.ensure_preview",
        lambda self: None,
    )
    window._set_workspace("dly")
    app.processEvents()

    assert window.workspace_stack.currentIndex() == 1
    assert window.skin_workspace.isHidden()
    workspace = window.dly_workspace
    assert workspace is not None
    assert workspace.parts_table.rowCount() == 8
    assert workspace.preset.anchor_part_id == "receiver"
    assert workspace.parts_table.cellWidget(0, 0).isCheckable()
    assert not workspace.display_object_button.isHidden()
    assert workspace.parts_table.columnCount() == 3
    assert not workspace.screen_object_buttons["receiver"].isHidden()
    assert workspace.screen_object_buttons["front"].isHidden()
    assert workspace.transform_tool_buttons["move"].isChecked()
    assert "G" in workspace.transform_tool_buttons["move"].text()

    workspace.parts_table.selectRow(3)
    app.processEvents()
    assert workspace.preset.anchor_part_id == "mag"
    assert workspace.preset.anchor_name == "Mag_9mm_50rdns_A"
    assert not workspace.screen_object_buttons["mag"].isHidden()
    workspace.screen_object_buttons["mag"].click()
    app.processEvents()
    assert workspace._display_object_selected is True
    assert workspace.display_object_button.isChecked()
    assert workspace.screen_object_buttons["mag"].isChecked()
    assert not workspace.parts_table.selectedItems()

    workspace.offset_spins["x"].setValue(12.5)
    workspace.rotation_spins["y"].setValue(15.0)
    workspace.face_player_check.click()
    workspace.panel_opacity_spin.setValue(40)
    workspace.border_opacity_spin.setValue(55)
    workspace.surface_snap_button.click()
    workspace.parts_table.cellWidget(0, 0).click()
    workspace._on_overlay_moved(0.0125, 0.001, -0.003)
    assert workspace.preset.offset_x == pytest.approx(0.0125)
    assert workspace.preset.offset_y == pytest.approx(0.001)
    assert workspace.preset.offset_z == pytest.approx(-0.003)
    assert workspace.preset.rotation_y == pytest.approx(15.0)
    assert workspace.preset.face_player is False
    assert workspace.preset.panel_opacity == pytest.approx(0.40)
    assert workspace.preset.border_opacity == pytest.approx(0.55)
    assert workspace.preset.surface_snap_enabled is True
    assert "front" in workspace.preset.hidden_parts

    window._set_workspace("skin")
    app.processEvents()
    assert window.workspace_stack.currentIndex() == 0
    assert (window.current_item.id if window.current_item else "") == original_item
    assert window.current_part_id == original_part
    assert window.table.rowCount() == original_rows
    window.close()
    app.processEvents()


def test_saved_dly_project_is_json_compatible(forge_data):
    path = forge_data / "projects" / "sample.gtfdly"
    save_dly_preset(path, DlyDisplayPreset(anchor_part_id="front"))
    value = json.loads(path.read_text(encoding="utf-8"))
    assert value["schema_version"] == 1
    assert value["target_item_id"] == "auto_pistol"
    assert value["anchor_part_id"] == "front"


def test_old_dly_preset_gets_blender_interaction_defaults(tmp_path):
    path = tmp_path / "legacy.gtfdly"
    path.write_text(
        json.dumps(
            {
                "anchor_part_id": "receiver",
                "panel_opacity": 0.0,
                "border_opacity": 0.0,
            }
        ),
        encoding="utf-8",
    )
    preset = load_dly_preset(path)
    assert preset.panel_opacity == 0.0
    assert preset.border_opacity == 0.0
    assert preset.last_transform_tool == "move"
    assert preset.fixed_view_preference == "iso"
    assert preset.surface_snap_enabled is False


def test_zero_opacity_display_keeps_editor_outline_and_pick_proxy(
    monkeypatch,
):
    app = QApplication.instance() or QApplication([])
    view = DlyPreviewView()
    monkeypatch.setattr(
        view,
        "bounds_for_part",
        lambda _part_id: (
            [0.0, 0.0, -0.02],
            [0.02, 0.03, 0.02],
        ),
    )
    preset = DlyDisplayPreset(
        panel_opacity=0.0,
        border_opacity=0.0,
        use_absolute_anchor=True,
        anchor_local_x=0.01,
        anchor_local_y=0.03,
        anchor_local_z=0.0,
    )
    view._preset = preset
    view._build_display_overlay(preset)

    assert len(view._overlay_pickers) == 1
    assert any(
        entity.objectName() == "DlyDisplayPickProxy"
        for entity in view._dly_entities
    )
    assert preset.normalized_anchor_x == pytest.approx(0.5)
    assert preset.normalized_anchor_y == pytest.approx(1.0)
    assert preset.normalized_anchor_z == pytest.approx(0.5)
    view.select_overlay()
    assert view._overlay_selected is True
    view.close()
    app.processEvents()


def test_g_axis_transform_changes_only_locked_gun_axis(monkeypatch):
    app = QApplication.instance() or QApplication([])
    view = DlyPreviewView()
    view._overlay_transform = object()
    view._overlay_world_position = QVector3D(0.01, 0.02, 0.03)
    view._preset.face_player = False
    committed = []
    monkeypatch.setattr(
        view,
        "_axis_screen_delta",
        lambda axis, _dx, _dy: QVector3D(0.004, 0.0, 0.0),
    )
    monkeypatch.setattr(
        view,
        "_commit_anchor",
        lambda anchor: committed.append(
            (anchor.x(), anchor.y(), anchor.z())
        ),
    )

    view._start_modal_transform("move")
    view._modal_axis = "x"
    view._apply_transform_delta(
        "move",
        "x",
        40.0,
        0.0,
        view._overlay_world_position,
        (0.0, 0.0, 0.0),
        1.0,
    )
    view._commit_modal_transform()

    assert committed == [pytest.approx((0.014, 0.02, 0.03))]
    assert view._modal_transform is None
    view.close()
    app.processEvents()


def test_blender_modal_cancel_restores_position_rotation_and_scale():
    app = QApplication.instance() or QApplication([])
    view = DlyPreviewView()
    view._overlay_transform = object()
    view._overlay_scale_transform = None
    view._overlay_world_position = QVector3D(0.02, 0.03, -0.01)
    view._preset.anchor_local_x = 0.02
    view._preset.anchor_local_y = 0.03
    view._preset.anchor_local_z = -0.01
    view._preset.rotation_x = 4.0
    view._preset.rotation_y = 5.0
    view._preset.rotation_z = 6.0
    view._preset.panel_scale = 1.2
    view._commit_anchor = lambda _anchor: None
    view._set_overlay_rotation = lambda: None

    view._start_modal_transform("rotate")
    view._preset.rotation_x = 40.0
    view._preset.rotation_y = 50.0
    view._preset.rotation_z = 60.0
    view._preset.panel_scale = 2.1
    view._cancel_modal_transform()

    assert view._preset.rotation_x == pytest.approx(4.0)
    assert view._preset.rotation_y == pytest.approx(5.0)
    assert view._preset.rotation_z == pytest.approx(6.0)
    assert view._preset.panel_scale == pytest.approx(1.2)
    assert view._preset.face_player is True
    assert view._modal_transform is None
    view.close()
    app.processEvents()


def test_nearest_view_snap_uses_camera_direction():
    app = QApplication.instance() or QApplication([])
    view = DlyPreviewView()
    center = QVector3D(0.0, 0.0, 0.0)
    view.camera.setViewCenter(center)
    view.camera.setPosition(QVector3D(0.30, 0.02, 0.01))
    assert view.nearest_fixed_view() == "right"
    view.camera.setPosition(QVector3D(0.01, 0.02, -0.30))
    assert view.nearest_fixed_view() == "front"
    view.close()
    app.processEvents()


def test_dly_workspace_applies_preset_to_selected_profile(
    tmp_path,
    forge_data,
    monkeypatch,
):
    app = QApplication.instance() or QApplication([])
    profile = tmp_path / "profile"
    plugin_dir = profile / DLY_PLUGIN_FOLDER
    plugin_dir.mkdir(parents=True)
    (profile / "winhttp.dll").write_bytes(b"doorstop")
    app_root = profile / "子弹显示测试" / "扩展软件"
    app_root.mkdir(parents=True)
    monkeypatch.setattr(
        "gtfo_skin_forge.dly_workspace.is_gtfo_running",
        lambda: False,
    )
    workspace = DlyWorkspace(app_root=app_root)
    workspace.preset.select_part("front")
    workspace.preset.offset_z = 0.018
    workspace.preset.face_player = False
    workspace._apply_to_profile()

    text = plugin_config_path(profile).read_text(encoding="utf-8")
    assert "AnchorObjectName = Front_UZI_2" in text
    assert "OffsetZ = 0.018" in text
    assert "FollowItemCamera = false" in text
    assert (plugin_dir / "Yuyu.HelAmmoDisplay.dll").is_file()
    workspace.close()
    app.processEvents()
