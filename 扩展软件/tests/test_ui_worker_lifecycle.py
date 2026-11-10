from __future__ import annotations

import os
import time
from pathlib import Path

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtGui import QColor, QKeySequence, QPixmap
from PySide6.QtCore import Qt
from PySide6.QtWidgets import QApplication, QTableWidgetItem

from gtfo_skin_forge.ui import ImageViewerDialog, MainWindow
from gtfo_skin_forge.models import (
    EmissiveSettings,
    MatchAnalysis,
    SkinProject,
    SlotMatch,
)


def _pump_until_finished(window: MainWindow, timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while window.worker is not None and time.monotonic() < deadline:
        QApplication.processEvents()
        time.sleep(0.01)
    QApplication.processEvents()


def test_texture_worker_can_run_twice(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    completed: list[int] = []
    assert not window.auto_backup_check.isChecked()

    window._start_worker("test", lambda report: 1, lambda value: completed.append(value))
    _pump_until_finished(window)
    window._start_worker("test", lambda report: 2, lambda value: completed.append(value))
    _pump_until_finished(window)

    assert completed == [1, 2]
    assert window.worker is None
    window.close()
    app.processEvents()


def test_emissive_controls_only_show_for_recognized_emissive(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    assert window.current_item is not None
    assert window.current_part_id is not None
    if window.current_game is None:
        pytest.skip("GTFO install unavailable")
    analysis = MatchAnalysis(item_id=window.current_item.id, folder=Path("."))
    analysis.slots[window.current_part_id] = {
        "Albedo": SlotMatch(
            window.current_part_id,
            "Albedo",
            Path("albedo.png"),
            1.0,
            1.0,
        ),
        "Emissive": SlotMatch(window.current_part_id, "Emissive"),
    }
    window.current_analysis = analysis
    window.current_project = SkinProject(
        name="test",
        item_id=window.current_item.id,
    )
    window._load_emissive_controls()
    assert window.emissive_group.isHidden()
    analysis.slots[window.current_part_id]["Emissive"].path = Path("emissive.png")
    window._load_emissive_controls()
    assert not window.emissive_group.isHidden()
    assert set(window.emissive_channel_spins) == {0, 1, 2}
    window.close()
    app.processEvents()


def test_emissive_step_buttons_change_by_point_one_and_refresh_3d(
    forge_data,
    monkeypatch,
):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    if window.current_item is None or window.current_part_id is None:
        pytest.skip("catalog item unavailable")
    analysis = MatchAnalysis(item_id=window.current_item.id, folder=Path("."))
    analysis.slots[window.current_part_id] = {
        "Emissive": SlotMatch(
            window.current_part_id,
            "Emissive",
            Path("emissive.png"),
            1.0,
            1.0,
        )
    }
    window.current_analysis = analysis
    window.current_project = SkinProject(
        name="test",
        item_id=window.current_item.id,
        emissive={
            window.current_part_id: EmissiveSettings(
                enabled=True,
                color=(1.0, 1.0, 1.0, 1.0),
            )
        },
    )
    window._load_emissive_controls()
    spin = window.emissive_channel_spins[0]
    assert {
        window.emissive_layout.getItemPosition(
            window.emissive_layout.indexOf(widget)
        )[0]
        for widget in window.emissive_channel_widgets.values()
    } == {0}
    refresh_calls: list[bool] = []
    monkeypatch.setattr(window, "_ensure_3d_view", lambda: None)
    monkeypatch.setattr(
        window,
        "_refresh_3d_preview",
        lambda: refresh_calls.append(True),
    )
    window.preview_tabs.setCurrentIndex(1)
    refresh_calls.clear()

    spin.stepDown()
    assert spin.value() == pytest.approx(0.9)
    spin.stepUp()
    assert spin.value() == pytest.approx(1.0)
    assert len(refresh_calls) == 2

    window.close()
    app.processEvents()


def test_long_filenames_do_not_lock_splitter_layout(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    window.resize(1480, 920)
    window.show()
    app.processEvents()
    long_name = "very_long_texture_file_name_" * 15 + ".png"
    if window.table.rowCount() and window.table.columnCount() > 3:
        window.table.setItem(0, 3, QTableWidgetItem(long_name))
    window._set_folder_label("D:/" + long_name)
    window.warning_list.addItem(long_name)
    window.main_splitter.setSizes([230, 720, 430])
    app.processEvents()
    widths = window.main_splitter.sizes()
    assert widths[0] >= 140
    assert widths[1] >= 260
    assert widths[2] >= 220
    assert sum(widths) <= window.main_splitter.width() + 10
    window.close()
    app.processEvents()


def test_channel_selection_controls_clear_button_state(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    window.show()
    app.processEvents()
    assert not window.clear_slot_button.isEnabled()
    assert window.clear_slot_button.text() == "清除当前通道"
    window.table.setCurrentCell(0, 3)
    app.processEvents()
    assert window.clear_slot_button.isEnabled()
    assert "基础色" in window.selected_channel_label.text()
    assert window.clear_slot_button.text() == "清除：基础色"
    window.table.setCurrentCell(0, 0)
    app.processEvents()
    assert not window.clear_slot_button.isEnabled()
    assert window.selected_channel_label.text() == "当前通道：未选择"
    window.close()
    app.processEvents()


def test_backup_path_button_reopens_directory_picker(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    calls: list[bool] = []
    window.ensure_backup_directory_interactive = (
        lambda force=False: calls.append(force) or True
    )
    window.backup_path_label.click()
    app.processEvents()
    assert calls == [True]
    window.close()
    app.processEvents()


def test_preview_tile_activates_image_viewer(forge_data, tmp_path, monkeypatch):
    app = QApplication.instance() or QApplication([])
    opened: list[Path] = []
    monkeypatch.setattr(
        MainWindow,
        "_open_preview_image",
        lambda self, path: opened.append(path),
    )
    window = MainWindow()
    image = tmp_path / "preview.png"
    image.write_bytes(b"not-an-image")
    tile = window.preview_tiles["Albedo"]
    tile.set_file(image)
    tile.activated.emit(image)
    assert opened == [image]
    window.close()
    app.processEvents()


def test_auto_backup_checkbox_is_remembered(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    window.settings.backup_dir = str(forge_data / "backups")
    window._refresh_install_manager()
    window.auto_backup_check.setChecked(True)
    app.processEvents()
    assert window.settings.auto_backup is True
    window.close()
    app.processEvents()

    reopened = MainWindow()
    assert reopened.auto_backup_check.isChecked()
    reopened.close()
    app.processEvents()


def test_image_viewer_supports_fit_and_wheel_zoom(forge_data, tmp_path):
    app = QApplication.instance() or QApplication([])
    image = tmp_path / "large-preview.png"
    pixmap = QPixmap(1200, 800)
    pixmap.fill(QColor("#cc3333"))
    assert pixmap.save(str(image))
    viewer = ImageViewerDialog(image)
    viewer.show()
    app.processEvents()
    viewer.image_view.fit_image()
    fitted = viewer.image_view.transform().m11()
    assert fitted > 0
    viewer.image_view.zoom_by(1.5)
    assert viewer.image_view.transform().m11() > fitted
    viewer.image_view.actual_size()
    assert abs(viewer.image_view.transform().m11() - 1.0) < 0.001
    viewer.close()
    app.processEvents()


def test_preview_has_lazy_2d_and_3d_tabs(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    assert window.preview_tabs.count() == 2
    assert window.preview_tabs.tabText(0) == "2D 通道"
    assert window.preview_tabs.tabText(1) == "3D 视图"
    assert window.preview_tabs.currentIndex() == 0
    assert window.three_d_view is None
    window.close()
    app.processEvents()


def test_catalog_tree_uses_main_secondary_and_attachment_sections(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    tree = window.catalog_tree

    assert [tree.topLevelItem(index).text(0) for index in range(3)] == [
        "主武器",
        "副武器",
        "枪械配件",
    ]
    attachments = tree.topLevelItem(2)
    assert [attachments.child(index).text(0) for index in range(attachments.childCount())] == [
        "弹匣",
        "子弹",
        "枪灯",
        "瞄具",
        "镜片",
    ]
    assert window._find_tree_item("gun_light_a") is not None
    window.close()
    app.processEvents()


def test_gun_light_is_enabled_by_default_and_can_be_disabled(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    tree_item = window._find_tree_item("pdw")
    assert tree_item is not None
    window.catalog_tree.setCurrentItem(tree_item)
    app.processEvents()

    row = next(
        index
        for index in range(window.table.rowCount())
        if window.table.item(index, 0).data(Qt.ItemDataRole.UserRole)
        == "flashlight"
    )
    checkbox = window.table.item(row, 0)
    assert checkbox.checkState() == Qt.CheckState.Checked
    assert window.table.item(row, 3).text() != "未启用"

    checkbox.setCheckState(Qt.CheckState.Unchecked)
    app.processEvents()

    assert "flashlight" not in window._optional_enabled
    window.close()
    app.processEvents()


def test_preview_lighting_settings_are_remembered(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    window._preview_ambient_intensity_changed(1.37)
    window._preview_ambient_color_changed("#aabbcc")
    window._preview_emissive_visibility_changed(False)
    window._preview_auto_rotate_changed(False)
    window.close()
    app.processEvents()

    reopened = MainWindow()
    assert reopened.settings.preview_ambient_intensity == pytest.approx(1.37)
    assert reopened.settings.preview_ambient_color == "#aabbcc"
    assert reopened.settings.preview_emissive_enabled is False
    assert reopened.settings.preview_auto_rotate is False
    reopened.close()
    app.processEvents()


def test_window_geometry_and_splitter_sizes_are_remembered(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    window.resize(1320, 820)
    window.show()
    app.processEvents()
    window.main_splitter.setSizes([230, 650, 440])
    app.processEvents()
    expected_sizes = window.main_splitter.sizes()
    window.close()
    app.processEvents()

    reopened = MainWindow()
    reopened.show()
    app.processEvents()
    assert reopened.width() >= 1120
    assert reopened.height() >= 720
    assert reopened.settings.splitter_sizes == pytest.approx(
        expected_sizes,
        abs=3,
    )
    expected_total = sum(expected_sizes)
    actual_total = sum(reopened.main_splitter.sizes())
    assert [
        value / actual_total
        for value in reopened.main_splitter.sizes()
    ] == pytest.approx(
        [value / expected_total for value in expected_sizes],
        abs=0.03,
    )
    reopened.close()
    app.processEvents()


def test_undo_restores_previous_emissive_edit(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    assert window.current_item is not None
    window.current_part_id = "front"
    window.current_project = SkinProject(
        name="undo",
        item_id=window.current_item.id,
        emissive={
            "front": EmissiveSettings(
                enabled=True,
                color=(0.3, 0.3, 0.3, 1.0),
                intensity=1.0,
            )
        },
    )

    window._emissive_channel_changed(0, 0.8)

    assert window.current_project.emissive["front"].color[0] == 0.8
    assert window.undo_action.isEnabled()
    assert window.undo_action.shortcut() == QKeySequence(
        QKeySequence.StandardKey.Undo
    )
    window.undo_action.trigger()
    app.processEvents()
    assert window.current_project.emissive["front"].color[0] == pytest.approx(0.3)
    assert not window.undo_action.isEnabled()

    window.close()
    app.processEvents()


def test_ammo_showcase_is_enabled_and_has_texture_channels(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    tree_item = window._find_tree_item("small_pistol")
    assert tree_item is not None
    window.catalog_tree.setCurrentItem(tree_item)
    app.processEvents()

    assert {"ammo_bullet", "ammo_shell"} <= window._optional_enabled
    for part_id, label in (
        ("ammo_bullet", "整颗"),
        ("ammo_shell", "空弹壳"),
    ):
        ammo_row = next(
            index
            for index in range(window.table.rowCount())
            if window.table.item(index, 0).data(Qt.ItemDataRole.UserRole)
            == part_id
        )
        assert window.table.item(ammo_row, 0).checkState() == Qt.CheckState.Checked
        assert window.table.item(ammo_row, 0).text().startswith(label)
        assert window.table.item(ammo_row, 1).text() == "ammo"
        assert window.table.item(ammo_row, 3).text() != "未启用"
    first_part_row = next(
        index
        for index in range(window.table.rowCount())
        if window.table.item(index, 0).data(Qt.ItemDataRole.UserRole)
        == "front"
    )
    window.table.item(first_part_row, 0).setCheckState(Qt.CheckState.Unchecked)
    app.processEvents()
    assert "front" in window._disabled_parts

    window.close()
    app.processEvents()


def test_revolver_speedloader_is_placed_between_light_and_bullet(forge_data):
    app = QApplication.instance() or QApplication([])
    window = MainWindow()
    tree_item = window._find_tree_item("hel_left")
    assert tree_item is not None
    window.catalog_tree.setCurrentItem(tree_item)
    app.processEvents()

    rows = {
        str(window.table.item(index, 0).data(Qt.ItemDataRole.UserRole)): index
        for index in range(window.table.rowCount())
    }
    assert rows["ammo_speedloader"] == rows["flashlight"] + 1
    assert rows["ammo_bullet"] == rows["ammo_speedloader"] + 1
    speedloader = window.table.item(rows["ammo_speedloader"], 0)
    assert speedloader.checkState() == Qt.CheckState.Checked
    assert speedloader.text() == "快速装弹器 · 左轮"

    window.close()
    app.processEvents()
