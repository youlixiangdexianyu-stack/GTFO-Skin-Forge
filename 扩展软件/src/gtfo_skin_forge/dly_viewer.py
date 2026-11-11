from __future__ import annotations

import math
from pathlib import Path

import numpy as np
from PySide6.Qt3DCore import Qt3DCore
from PySide6.Qt3DExtras import Qt3DExtras
from PySide6.Qt3DRender import Qt3DRender
from PySide6.QtCore import QEvent, Qt, QTimer, Signal
from PySide6.QtGui import (
    QColor,
    QMouseEvent,
    QQuaternion,
    QVector3D,
)

from .assembly import FORGE_SCENE_TO_GAME_SCALE
from .dly import DlyDisplayPreset
from .models import CatalogItem, GameInstall, SkinProject
from .viewer3d import Weapon3DView


_SEGMENT_MASKS = {
    "0": 0b0111111,
    "1": 0b0000110,
    "2": 0b1011011,
    "3": 0b1001111,
    "4": 0b1100110,
    "5": 0b1101101,
    "6": 0b1111101,
    "7": 0b0000111,
    "8": 0b1111111,
    "9": 0b1101111,
}


class DlyPreviewView(Weapon3DView):
    part_picked = Signal(str)
    overlay_selected = Signal()
    overlay_moved = Signal(float, float, float)
    overlay_rotated = Signal(float, float, float)
    overlay_scaled = Signal(float)
    transform_status = Signal(str)
    fixed_view_changed = Signal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.status.setText(
            "点击枪模选择部件 · 拖动屏幕贴面移动 · 拖彩色轴单轴移动"
        )
        self.emissive_check.setVisible(False)
        self.auto_rotate_check.setChecked(False)
        self._set_auto_rotate(False, notify=False)
        self._preset = DlyDisplayPreset()
        self.selected_part_id = self._preset.anchor_part_id
        self._pickers: list[Qt3DRender.QObjectPicker] = []
        self._dly_entities: list[Qt3DCore.QEntity] = []
        self._part_highlight_entities: list[Qt3DCore.QEntity] = []
        self._dly_materials: list[Qt3DCore.QNode] = []
        self._overlay_root: Qt3DCore.QEntity | None = None
        self._overlay_content_root: Qt3DCore.QEntity | None = None
        self._overlay_transform: Qt3DCore.QTransform | None = None
        self._overlay_scale_transform: Qt3DCore.QTransform | None = None
        self._overlay_world_position = QVector3D(0.0, 0.0, 0.0)
        self._overlay_selected = True
        self._overlay_pickers: list[Qt3DRender.QObjectPicker] = []
        self._gizmo_pickers: list[Qt3DRender.QObjectPicker] = []
        self._gizmo_groups: dict[str, Qt3DCore.QEntity] = {}
        self._gizmo_anchor_transform: Qt3DCore.QTransform | None = None
        self._drag_active = False
        self._drag_last_position = None
        self._drag_start_position = QVector3D(0.0, 0.0, 0.0)
        self._drag_plane_normal = QVector3D(0.0, 1.0, 0.0)
        self._axis_drag_axis: str | None = None
        self._axis_drag_action = "move"
        self._axis_drag_start_mouse = None
        self._axis_drag_start_position = QVector3D(0.0, 0.0, 0.0)
        self._axis_drag_start_rotation = (0.0, 0.0, 0.0)
        self._axis_drag_start_scale = 1.0
        self._axis_drag_last_position = None
        self._fixed_view = "iso"
        self._transform_tool = "move"
        self._modal_transform: str | None = None
        self._modal_axis: str | None = None
        self._modal_start_mouse = None
        self._modal_start_position = QVector3D(0.0, 0.0, 0.0)
        self._modal_start_rotation = (0.0, 0.0, 0.0)
        self._modal_start_scale = 1.0
        self._modal_start_face_player = True
        self._surface_normal = QVector3D(0.0, 1.0, 0.0)
        self._surface_snap_enabled = False
        self._perspective_projection = True
        self.custom_overlay_image_path: Path | None = None
        self._alt_view_drag_active = False
        self._alt_view_last_position = None
        self._alt_view_dragged = False
        self.container.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self.container.setCursor(Qt.CursorShape.CrossCursor)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)
        self._face_timer = QTimer(self)
        self._face_timer.setInterval(50)
        self._face_timer.timeout.connect(self._face_camera_tick)
        self._face_timer.start()

    def load_hel(
        self,
        game: GameInstall,
        item: CatalogItem,
        preset: DlyDisplayPreset,
    ) -> None:
        project = SkinProject(
            name="DLY HEL Ammo Display",
            item_id=item.id,
            enabled_parts=sorted(
                part.id for part in item.parts if part.optional
            ),
        )
        self._pickers.clear()
        self._loaded_key = None
        # Forge preview meshes are 0.8x game weapon space; load the DLY editor at
        # game scale so the anchor coordinates written to the plugin config are
        # already in game units.
        self.load_weapon(
            game,
            item,
            project,
            unit_scale=FORGE_SCENE_TO_GAME_SCALE,
        )
        self._install_part_pickers()
        self._preset = preset
        self._transform_tool = preset.last_transform_tool
        self._fixed_view = preset.fixed_view_preference
        self._surface_snap_enabled = preset.surface_snap_enabled
        self.set_hidden_parts(set(preset.hidden_parts))
        self.selected_part_id = preset.anchor_part_id
        self.apply_preset(preset)
        self.set_transform_tool(preset.last_transform_tool)
        self.apply_fixed_view(preset.fixed_view_preference)

    def _install_part_pickers(self) -> None:
        if self._pickers:
            return
        for part_id in self._mesh_entities_by_part:
            for entity in self._mesh_entities_by_part[part_id]:
                picker = Qt3DRender.QObjectPicker(entity)
                picker.setHoverEnabled(False)
                picker.setDragEnabled(False)
                entity.addComponent(picker)
                picker.clicked.connect(
                    lambda event, selected=part_id:
                    self._pick_part(selected, event)
                )
                self._pickers.append(picker)

    def _pick_part(self, part_id: str, event=None) -> None:
        self.selected_part_id = part_id
        self._overlay_selected = False
        if self._surface_snap_enabled and event is not None:
            self._place_on_surface(part_id, event)
        else:
            self._preset.select_part(part_id, reset_offsets=True)
            self._preset.use_absolute_anchor = False
            self._surface_normal = QVector3D(0.0, 1.0, 0.0)
            self.apply_preset(self._preset)
        self.part_picked.emit(part_id)

    def select_part(self, part_id: str, reset_offsets: bool = False) -> None:
        self.selected_part_id = part_id
        self._overlay_selected = False
        self._preset.select_part(part_id, reset_offsets=reset_offsets)
        if reset_offsets:
            self._preset.use_absolute_anchor = False
        self.apply_preset(self._preset)

    def select_overlay(self) -> None:
        self._overlay_selected = True
        self._mark_scene_selection()
        self.overlay_selected.emit()
        self.transform_status.emit(
            "已选中子弹显示屏幕；G 移动，R 旋转，S 缩放"
        )

    def set_surface_snap_enabled(self, enabled: bool) -> None:
        self._surface_snap_enabled = bool(enabled)
        self._preset.surface_snap_enabled = bool(enabled)

    def set_part_visibility(self, part_id: str, visible: bool) -> None:
        for entity in self._mesh_entities_by_part.get(part_id, ()):
            entity.setEnabled(visible)

    def set_hidden_parts(self, hidden_parts: set[str]) -> None:
        for part_id in self._mesh_entities_by_part:
            self.set_part_visibility(
                part_id,
                part_id not in hidden_parts,
            )

    def apply_preset(self, preset: DlyDisplayPreset) -> None:
        self._preset = preset
        self.selected_part_id = preset.anchor_part_id
        self._transform_tool = preset.last_transform_tool
        self._surface_snap_enabled = preset.surface_snap_enabled
        self._clear_dly_entities()
        self._build_selection_highlight(preset.anchor_part_id)
        self._build_display_overlay(preset)
        self._face_camera_tick()

    def set_custom_overlay_image(self, path: Path | None) -> None:
        self.custom_overlay_image_path = Path(path) if path else None
        self.apply_preset(self._preset)

    def selected_part_bounds(
        self,
    ) -> tuple[np.ndarray, np.ndarray] | None:
        return self.bounds_for_part(self.selected_part_id)

    def sync_absolute_anchor(self) -> None:
        if self._overlay_transform is None:
            return
        self._commit_anchor(self._overlay_world_position)

    def _clear_dly_entities(self) -> None:
        for entity in self._dly_entities:
            entity.setParent(None)
            entity.deleteLater()
        self._dly_entities.clear()
        self._part_highlight_entities.clear()
        for material in self._dly_materials:
            material.setParent(None)
            material.deleteLater()
        self._dly_materials.clear()
        self._overlay_root = None
        self._overlay_content_root = None
        self._overlay_transform = None
        self._overlay_scale_transform = None
        self._overlay_pickers.clear()
        self._gizmo_pickers.clear()
        self._gizmo_groups.clear()
        self._gizmo_anchor_transform = None

    def _material(
        self,
        color: str,
        *,
        alpha: float = 1.0,
    ) -> Qt3DExtras.QPhongAlphaMaterial | Qt3DExtras.QPhongMaterial:
        qcolor = QColor(color)
        qcolor.setAlphaF(max(0.0, min(1.0, float(alpha))))
        if alpha < 1.0:
            material = Qt3DExtras.QPhongAlphaMaterial(self.root)
            material.setAlpha(qcolor.alphaF())
        else:
            material = Qt3DExtras.QPhongMaterial(self.root)
        material.setAmbient(qcolor)
        material.setDiffuse(qcolor)
        material.setSpecular(QColor("#000000"))
        material.setShininess(1.0)
        for technique in material.effect().techniques():
            for render_pass in technique.renderPasses():
                depth_test = Qt3DRender.QDepthTest(render_pass)
                depth_test.setDepthFunction(
                    Qt3DRender.QDepthTest.DepthFunction.Always
                )
                render_pass.addRenderState(depth_test)
        self._dly_materials.append(material)
        return material

    def _add_box(
        self,
        parent: Qt3DCore.QEntity,
        center: QVector3D,
        size: QVector3D,
        material: Qt3DCore.QNode,
        interactive: bool = False,
    ) -> Qt3DCore.QEntity:
        entity = Qt3DCore.QEntity(parent)
        mesh = Qt3DExtras.QCuboidMesh(entity)
        mesh.setXExtent(max(size.x(), 0.00005))
        mesh.setYExtent(max(size.y(), 0.00005))
        mesh.setZExtent(max(size.z(), 0.00005))
        transform = Qt3DCore.QTransform(entity)
        transform.setTranslation(center)
        entity.addComponent(mesh)
        entity.addComponent(transform)
        entity.addComponent(material)
        if interactive:
            picker = Qt3DRender.QObjectPicker(entity)
            picker.setDragEnabled(True)
            picker.setHoverEnabled(False)
            picker.pressed.connect(self._overlay_pressed)
            picker.moved.connect(self._overlay_dragged)
            picker.released.connect(self._overlay_released)
            entity.addComponent(picker)
            self._overlay_pickers.append(picker)
        self._dly_entities.append(entity)
        return entity

    def _add_pick_box(
        self,
        parent: Qt3DCore.QEntity,
        center: QVector3D,
        size: QVector3D,
    ) -> Qt3DCore.QEntity:
        entity = Qt3DCore.QEntity(parent)
        mesh = Qt3DExtras.QCuboidMesh(entity)
        mesh.setXExtent(max(size.x(), 0.00005))
        mesh.setYExtent(max(size.y(), 0.00005))
        mesh.setZExtent(max(size.z(), 0.00005))
        transform = Qt3DCore.QTransform(entity)
        transform.setTranslation(center)
        picker = Qt3DRender.QObjectPicker(entity)
        picker.setDragEnabled(True)
        picker.setHoverEnabled(False)
        picker.pressed.connect(self._overlay_pressed)
        picker.moved.connect(self._overlay_dragged)
        picker.released.connect(self._overlay_released)
        entity.addComponent(mesh)
        entity.addComponent(transform)
        entity.addComponent(picker)
        entity.setObjectName("DlyDisplayPickProxy")
        self._overlay_pickers.append(picker)
        self._dly_entities.append(entity)
        return entity

    def _build_selection_highlight(self, part_id: str) -> None:
        bounds = self.bounds_for_part(part_id)
        if bounds is None:
            return
        low, high = bounds
        center = (low + high) * 0.5
        size = high - low
        thickness = max(float(np.max(size)) * 0.0035, 0.00025)
        material = self._material("#67f7ff", alpha=0.72)

        cx, cy, cz = (float(center[0]), float(center[1]), float(center[2]))
        sx, sy, sz = (float(size[0]), float(size[1]), float(size[2]))
        x_offsets = (-sx * 0.5, sx * 0.5)
        y_offsets = (-sy * 0.5, sy * 0.5)
        z_offsets = (-sz * 0.5, sz * 0.5)

        for y in y_offsets:
            for z in z_offsets:
                self._part_highlight_entities.append(self._add_box(
                    self.model_root,
                    QVector3D(cx, cy + y, cz + z),
                    QVector3D(sx + thickness, thickness, thickness),
                    material,
                ))
        for x in x_offsets:
            for z in z_offsets:
                self._part_highlight_entities.append(self._add_box(
                    self.model_root,
                    QVector3D(cx + x, cy, cz + z),
                    QVector3D(thickness, sy + thickness, thickness),
                    material,
                ))
        for x in x_offsets:
            for y in y_offsets:
                self._part_highlight_entities.append(self._add_box(
                    self.model_root,
                    QVector3D(cx + x, cy + y, cz),
                    QVector3D(thickness, thickness, sz + thickness),
                    material,
                ))

    def _build_display_overlay(self, preset: DlyDisplayPreset) -> None:
        bounds = self.bounds_for_part(preset.anchor_part_id)
        if bounds is None:
            return
        if preset.use_absolute_anchor:
            anchor = QVector3D(
                float(preset.anchor_local_x),
                float(preset.anchor_local_y),
                float(preset.anchor_local_z),
            )
            low, high = bounds
            preset.offset_x = float(
                anchor.x() - (low[0] + high[0]) * 0.5
            )
            preset.offset_y = float(anchor.y() - high[1])
            preset.offset_z = float(
                anchor.z() - (low[2] + high[2]) * 0.5
            )
        else:
            low, high = bounds
            anchor = QVector3D(
                float((low[0] + high[0]) * 0.5 + preset.offset_x),
                float(high[1] + preset.surface_clearance + preset.offset_y),
                float((low[2] + high[2]) * 0.5 + preset.offset_z),
            )
            self._surface_normal = QVector3D(0.0, 1.0, 0.0)
        self._update_normalized_anchor(preset, anchor, bounds)

        self._overlay_root = Qt3DCore.QEntity(self.model_root)
        self._overlay_transform = Qt3DCore.QTransform(self._overlay_root)
        self._overlay_transform.setTranslation(anchor)
        self._overlay_root.addComponent(self._overlay_transform)
        self._dly_entities.append(self._overlay_root)

        self._overlay_content_root = Qt3DCore.QEntity(
            self._overlay_root
        )
        self._overlay_scale_transform = Qt3DCore.QTransform(
            self._overlay_content_root
        )
        self._overlay_scale_transform.setScale3D(
            QVector3D(
                preset.panel_scale,
                preset.panel_scale,
                preset.panel_scale,
            )
        )
        self._overlay_content_root.addComponent(
            self._overlay_scale_transform
        )
        self._overlay_world_position = anchor

        panel_width = max(0.004, preset.panel_width)
        panel_height = max(0.002, preset.panel_height)
        depth = 0.0007
        border = max(0.00035, min(panel_width, panel_height) * 0.07)
        panel_material = self._material(
            preset.panel_color,
            alpha=preset.panel_opacity,
        )
        border_material = self._material(
            preset.border_color,
            alpha=preset.border_opacity,
        )
        digit_material = self._material(preset.text_color)
        editor_material = self._material("#ffad32")
        handle_material = self._material("#fff2d6")

        content = self._overlay_content_root
        if self.custom_overlay_image_path is not None:
            texture_material = Qt3DExtras.QTextureMaterial(content)
            texture = self._texture(
                texture_material,
                self.custom_overlay_image_path,
            )
            if texture is not None:
                texture_material.setTexture(texture)
            if hasattr(texture_material, "setAlphaBlendingEnabled"):
                texture_material.setAlphaBlendingEnabled(True)
            self._dly_materials.append(texture_material)
            image_entity = self._add_box(
                content,
                QVector3D(depth * 0.55, 0.0, 0.0),
                QVector3D(max(depth, 0.0002), panel_height, panel_width),
                texture_material,
            )
            image_picker = Qt3DRender.QObjectPicker(image_entity)
            image_picker.setDragEnabled(True)
            image_picker.setHoverEnabled(False)
            image_picker.pressed.connect(self._overlay_pressed)
            image_picker.moved.connect(self._overlay_dragged)
            image_picker.released.connect(self._overlay_released)
            image_entity.addComponent(image_picker)
            self._overlay_pickers.append(image_picker)
            for center, size in (
                (
                    QVector3D(depth * 0.9, panel_height * 0.5, 0.0),
                    QVector3D(depth * 0.7, border, panel_width + border * 2),
                ),
                (
                    QVector3D(depth * 0.9, -panel_height * 0.5, 0.0),
                    QVector3D(depth * 0.7, border, panel_width + border * 2),
                ),
                (
                    QVector3D(depth * 0.9, 0.0, panel_width * 0.5),
                    QVector3D(depth * 0.7, panel_height, border),
                ),
                (
                    QVector3D(depth * 0.9, 0.0, -panel_width * 0.5),
                    QVector3D(depth * 0.7, panel_height, border),
                ),
            ):
                self._add_box(content, center, size, editor_material)
            self._build_transform_gizmo(
                anchor,
                max(panel_width * 0.85, 0.012),
            )
            self._mark_scene_selection()
            return
        self._add_box(
            content,
            QVector3D(depth * 0.45, 0.0, 0.0),
            QVector3D(depth, panel_height, panel_width),
            panel_material,
        )
        for center, size in (
            (
                QVector3D(depth * 0.55, panel_height * 0.5, 0.0),
                QVector3D(depth * 0.7, border, panel_width + border * 2),
            ),
            (
                QVector3D(depth * 0.55, -panel_height * 0.5, 0.0),
                QVector3D(depth * 0.7, border, panel_width + border * 2),
            ),
            (
                QVector3D(depth * 0.55, 0.0, panel_width * 0.5),
                QVector3D(depth * 0.7, panel_height, border),
            ),
            (
                QVector3D(depth * 0.55, 0.0, -panel_width * 0.5),
                QVector3D(depth * 0.7, panel_height, border),
            ),
        ):
            self._add_box(content, center, size, border_material)

        editor_depth = depth * 0.95
        editor_thickness = max(
            0.00018,
            min(panel_width, panel_height) * 0.018,
        )
        outline_gap = editor_thickness * 1.8
        outline_width = panel_width + outline_gap * 2
        outline_height = panel_height + outline_gap * 2
        for center, size in (
            (
                QVector3D(editor_depth, outline_height * 0.5, 0.0),
                QVector3D(depth * 0.6, editor_thickness, outline_width),
            ),
            (
                QVector3D(editor_depth, -outline_height * 0.5, 0.0),
                QVector3D(depth * 0.6, editor_thickness, outline_width),
            ),
            (
                QVector3D(editor_depth, 0.0, outline_width * 0.5),
                QVector3D(depth * 0.6, outline_height, editor_thickness),
            ),
            (
                QVector3D(editor_depth, 0.0, -outline_width * 0.5),
                QVector3D(depth * 0.6, outline_height, editor_thickness),
            ),
        ):
            self._add_box(content, center, size, editor_material)

        handle_size = max(0.0012, min(panel_width, panel_height) * 0.16)
        for y_sign in (-1.0, 1.0):
            for z_sign in (-1.0, 1.0):
                self._add_box(
                    content,
                    QVector3D(
                        editor_depth,
                        outline_height * 0.5 * y_sign,
                        outline_width * 0.5 * z_sign,
                    ),
                    QVector3D(
                        depth * 0.8,
                        handle_size,
                        handle_size,
                    ),
                    handle_material,
                )

        self._add_pick_box(
            content,
            QVector3D(depth * 1.15, 0.0, 0.0),
            QVector3D(
                max(depth * 1.2, 0.0008),
                panel_height * 1.2,
                panel_width * 1.2,
            ),
        )
        self._build_seven_segment_values(
            preset,
            panel_width,
            panel_height,
            depth,
            digit_material,
        )
        self._build_transform_gizmo(
            anchor,
            max(panel_width * 0.85, 0.012),
        )
        self._mark_scene_selection()

    def _build_seven_segment_values(
        self,
        preset: DlyDisplayPreset,
        panel_width: float,
        panel_height: float,
        depth: float,
        material: Qt3DCore.QNode,
    ) -> None:
        value = f"{max(0, min(999, int(preset.preview_clip))):02d}"
        count = max(2, len(value))
        digit_height = panel_height * 0.66
        digit_width = digit_height * 0.68
        segment = max(digit_width * 0.13, 0.00022)
        gap = digit_width * 0.36
        step = digit_width + gap
        start = -(count - 1) * step * 0.5
        half_w = digit_width * 0.5
        half_h = digit_height * 0.5
        quarter_h = digit_height * 0.25
        front = depth * 0.85

        for index, digit in enumerate(value):
            z = start + index * step
            mask = _SEGMENT_MASKS.get(digit, 0)
            segments = (
                (0.0, half_h, segment, digit_width),
                (half_w, quarter_h, digit_height * 0.5, segment),
                (half_w, -quarter_h, digit_height * 0.5, segment),
                (0.0, -half_h, segment, digit_width),
                (-half_w, -quarter_h, digit_height * 0.5, segment),
                (-half_w, quarter_h, digit_height * 0.5, segment),
                (0.0, 0.0, segment, digit_width),
            )
            for bit, (z_offset, y, y_extent, z_extent) in enumerate(segments):
                if not (mask & (1 << bit)):
                    continue
                self._add_box(
                    self._overlay_content_root,
                    QVector3D(front, y, z + z_offset),
                    QVector3D(depth * 0.55, max(segment, y_extent), z_extent),
                    material,
                )

    def _build_transform_gizmo(
        self,
        anchor: QVector3D,
        length: float,
    ) -> None:
        gizmo = Qt3DCore.QEntity(self.model_root)
        transform = Qt3DCore.QTransform(gizmo)
        transform.setTranslation(anchor)
        gizmo.addComponent(transform)
        self._dly_entities.append(gizmo)
        self._gizmo_anchor_transform = transform

        thickness = max(0.00065, length * 0.055)
        gap = thickness * 1.4
        colors = {
            "x": "#ff4d4d",
            "y": "#71ee64",
            "z": "#4ea8ff",
        }
        move_group = Qt3DCore.QEntity(gizmo)
        rotate_group = Qt3DCore.QEntity(gizmo)
        scale_group = Qt3DCore.QEntity(gizmo)
        self._gizmo_groups = {
            "move": move_group,
            "rotate": rotate_group,
            "scale": scale_group,
        }
        self._dly_entities.extend(
            (move_group, rotate_group, scale_group)
        )
        for axis, color in colors.items():
            material = self._material(color)
            for sign in (-1.0, 1.0):
                axis_length = length * 0.75
                center_offset = sign * (gap + axis_length * 0.5)
                if axis == "x":
                    center = QVector3D(center_offset, 0.0, 0.0)
                    size = QVector3D(axis_length, thickness, thickness)
                elif axis == "y":
                    center = QVector3D(0.0, center_offset, 0.0)
                    size = QVector3D(thickness, axis_length, thickness)
                else:
                    center = QVector3D(0.0, 0.0, center_offset)
                    size = QVector3D(thickness, thickness, axis_length)
                handle = self._add_box(
                    move_group,
                    center,
                    size,
                    material,
                )
                self._add_gizmo_picker(handle, "move", axis)

            rotate_material = self._material(color, alpha=0.78)
            ring = self._add_torus(
                rotate_group,
                length * 0.62,
                max(thickness * 0.72, 0.00045),
                rotate_material,
                axis,
            )
            self._add_gizmo_picker(ring, "rotate", axis)

            scale_length = length * 0.62
            if axis == "x":
                scale_center = QVector3D(scale_length, 0.0, 0.0)
                scale_size = QVector3D(thickness, thickness, thickness * 2.6)
            elif axis == "y":
                scale_center = QVector3D(0.0, scale_length, 0.0)
                scale_size = QVector3D(thickness * 2.6, thickness, thickness)
            else:
                scale_center = QVector3D(0.0, 0.0, scale_length)
                scale_size = QVector3D(thickness, thickness * 2.6, thickness)
            scale_handle = self._add_box(
                scale_group,
                scale_center,
                scale_size,
                material,
            )
            self._add_gizmo_picker(scale_handle, "scale", None)

        center_material = self._material("#fff4d6", alpha=0.92)
        center = Qt3DCore.QEntity(scale_group)
        center_mesh = Qt3DExtras.QSphereMesh(center)
        center_mesh.setRadius(thickness * 1.45)
        center_mesh.setRings(18)
        center_mesh.setSlices(20)
        center.addComponent(center_mesh)
        center.addComponent(center_material)
        self._dly_entities.append(center)
        self._add_gizmo_picker(center, "scale", None)
        self._update_gizmo_visibility()

    def _add_torus(
        self,
        parent: Qt3DCore.QEntity,
        radius: float,
        minor_radius: float,
        material: Qt3DCore.QNode,
        axis: str,
    ) -> Qt3DCore.QEntity:
        entity = Qt3DCore.QEntity(parent)
        mesh = Qt3DExtras.QTorusMesh(entity)
        mesh.setRadius(radius)
        mesh.setMinorRadius(minor_radius)
        mesh.setRings(64)
        mesh.setSlices(12)
        transform = Qt3DCore.QTransform(entity)
        if axis == "x":
            transform.setRotationY(90.0)
        elif axis == "y":
            transform.setRotationX(90.0)
        entity.addComponent(mesh)
        entity.addComponent(transform)
        entity.addComponent(material)
        self._dly_entities.append(entity)
        return entity

    def _add_gizmo_picker(
        self,
        entity: Qt3DCore.QEntity,
        action: str,
        axis: str | None,
    ) -> None:
        picker = Qt3DRender.QObjectPicker(entity)
        picker.setDragEnabled(True)
        picker.setHoverEnabled(True)
        picker.pressed.connect(
            lambda event, selected_action=action, selected_axis=axis:
            self._axis_pressed(
                selected_axis,
                event,
                action=selected_action,
            )
        )
        picker.moved.connect(self._axis_dragged)
        picker.released.connect(self._axis_released)
        entity.addComponent(picker)
        self._gizmo_pickers.append(picker)

    def set_transform_tool(self, tool: str) -> None:
        if tool not in self._gizmo_groups and tool not in {
            "move",
            "rotate",
            "scale",
        }:
            return
        self._transform_tool = tool
        self._preset.last_transform_tool = tool
        self._update_gizmo_visibility()
        labels = {
            "move": "移动工具：拖动红/绿/蓝轴，或按 G 后锁定 X/Y/Z",
            "rotate": "旋转工具：拖动彩色圆环，或按 R 后锁定 X/Y/Z",
            "scale": "缩放工具：拖动中心控制柄，或按 S 统一缩放",
        }
        self.transform_status.emit(labels[tool])

    def _update_gizmo_visibility(self) -> None:
        for name, group in self._gizmo_groups.items():
            group.setEnabled(name == self._transform_tool)

    def _mark_scene_selection(self) -> None:
        for entity in self._part_highlight_entities:
            entity.setEnabled(not self._overlay_selected)
        for group in self._gizmo_groups.values():
            group.setEnabled(
                self._overlay_selected
                and group is self._gizmo_groups.get(self._transform_tool)
            )

    def _axis_pressed(
        self,
        axis: str | None,
        event,
        *,
        action: str | None = None,
    ) -> None:
        position = event.position()
        self._axis_drag_action = action or self._transform_tool
        self._axis_drag_axis = axis
        self._axis_drag_start_mouse = (
            float(position.x()),
            float(position.y()),
        )
        self._axis_drag_last_position = (
            float(position.x()),
            float(position.y()),
        )
        self._axis_drag_start_position = self._overlay_world_position
        self._axis_drag_start_rotation = (
            self._preset.rotation_x,
            self._preset.rotation_y,
            self._preset.rotation_z,
        )
        self._axis_drag_start_scale = self._preset.panel_scale
        if self._axis_drag_action == "rotate":
            self._preset.face_player = False
            self.preset_changed_by_handle()
            self.overlay_rotated.emit(
                self._preset.rotation_x,
                self._preset.rotation_y,
                self._preset.rotation_z,
            )
        self.controller.setEnabled(False)
        self._overlay_selected = True
        self.overlay_selected.emit()

    def _axis_released(self, _event) -> None:
        self._axis_drag_axis = None
        self._axis_drag_start_mouse = None
        self._axis_drag_last_position = None
        self.controller.setEnabled(True)

    def _axis_dragged(self, event) -> None:
        if (
            self._axis_drag_start_mouse is None
            or self._overlay_transform is None
        ):
            return

        position = event.position()
        delta_x = float(position.x()) - self._axis_drag_start_mouse[0]
        delta_y = float(position.y()) - self._axis_drag_start_mouse[1]
        self._apply_transform_delta(
            self._axis_drag_action,
            self._axis_drag_axis,
            delta_x,
            delta_y,
            self._axis_drag_start_position,
            self._axis_drag_start_rotation,
            self._axis_drag_start_scale,
        )

    def _overlay_pressed(self, event) -> None:
        position = event.position()
        self._drag_last_position = (
            float(position.x()),
            float(position.y()),
        )
        self._drag_active = True
        self._drag_start_position = self._overlay_world_position
        if self._preset.face_player:
            normal = self.camera.position() - self._overlay_world_position
            if normal.lengthSquared() > 0.000001:
                self._drag_plane_normal = normal.normalized()
            else:
                self._drag_plane_normal = QVector3D(0.0, 1.0, 0.0)
        else:
            rotation = QQuaternion.fromEulerAngles(
                self._preset.rotation_x,
                self._preset.rotation_y,
                self._preset.rotation_z,
            )
            self._drag_plane_normal = rotation.rotatedVector(
                QVector3D(1.0, 0.0, 0.0)
            )
        self.controller.setEnabled(False)
        self._overlay_selected = True
        self.overlay_selected.emit()

    def _overlay_released(self, _event) -> None:
        self._drag_active = False
        self._drag_last_position = None
        self.controller.setEnabled(True)

    def _overlay_dragged(self, event) -> None:
        if (
            not self._drag_active
            or self._drag_last_position is None
            or self._overlay_transform is None
        ):
            return

        position = event.position()
        delta_x = float(position.x()) - self._drag_last_position[0]
        delta_y = float(position.y()) - self._drag_last_position[1]
        self._drag_last_position = (
            float(position.x()),
            float(position.y()),
        )
        delta = self._screen_delta_to_plane(
            delta_x,
            delta_y,
            self._drag_plane_normal,
        )
        anchor = self._overlay_world_position + delta
        self._commit_anchor(anchor)

    def _screen_delta_to_world(
        self,
        delta_x: float,
        delta_y: float,
    ) -> QVector3D:
        return self._screen_delta_to_plane(delta_x, delta_y)

    def _screen_delta_to_plane(
        self,
        delta_x: float,
        delta_y: float,
        plane_normal: QVector3D | None = None,
    ) -> QVector3D:
        distance = max(
            (self.camera.position() - self._overlay_world_position).length(),
            0.02,
        )
        viewport_height = max(self.container.height(), 1)
        world_per_pixel = (
            2.0
            * distance
            * math.tan(math.radians(self.camera.fieldOfView()) * 0.5)
            / viewport_height
        )
        forward = (
            self.camera.viewCenter() - self.camera.position()
        ).normalized()
        camera_up = self.camera.upVector().normalized()
        right = QVector3D.crossProduct(forward, camera_up).normalized()
        if right.lengthSquared() < 0.000001:
            right = QVector3D(1.0, 0.0, 0.0)
        up = QVector3D.crossProduct(right, forward).normalized()
        if plane_normal is not None and plane_normal.lengthSquared() > 0.000001:
            normal = plane_normal.normalized()
            right = right - normal * QVector3D.dotProduct(right, normal)
            up = up - normal * QVector3D.dotProduct(up, normal)
            if right.lengthSquared() < 0.000001:
                right = QVector3D(1.0, 0.0, 0.0)
            if up.lengthSquared() < 0.000001:
                up = QVector3D(0.0, 1.0, 0.0)
        return (
            right.normalized() * (delta_x * world_per_pixel)
            - up.normalized() * (delta_y * world_per_pixel)
        )

    def preset_changed_by_handle(self) -> None:
        self._face_camera_tick()

    def _apply_transform_delta(
        self,
        action: str,
        axis: str | None,
        delta_x: float,
        delta_y: float,
        start_position: QVector3D,
        start_rotation: tuple[float, float, float],
        start_scale: float,
    ) -> None:
        if action == "move":
            if axis is None:
                translation = self._screen_delta_to_plane(
                    delta_x,
                    delta_y,
                )
            else:
                translation = self._axis_screen_delta(axis, delta_x, delta_y)
            self._commit_anchor(start_position + translation)
            return
        if action == "rotate":
            self._preset.face_player = False
            if axis is None:
                self._preset.rotation_x = self._wrap_degrees(
                    start_rotation[0] - delta_y * 0.45
                )
                self._preset.rotation_y = self._wrap_degrees(
                    start_rotation[1] + delta_x * 0.45
                )
                self._preset.rotation_z = self._wrap_degrees(
                    start_rotation[2]
                )
            else:
                local_axis = self._local_axis_vector(axis)
                angle = (delta_x - delta_y) * 0.45
                start = QQuaternion.fromEulerAngles(*start_rotation)
                rotated = (
                    QQuaternion.fromAxisAndAngle(local_axis, angle)
                    * start
                )
                euler = rotated.toEulerAngles()
                self._preset.rotation_x = self._wrap_degrees(float(euler.x()))
                self._preset.rotation_y = self._wrap_degrees(float(euler.y()))
                self._preset.rotation_z = self._wrap_degrees(float(euler.z()))
            self._set_overlay_rotation()
            self.overlay_rotated.emit(
                self._preset.rotation_x,
                self._preset.rotation_y,
                self._preset.rotation_z,
            )
            return
        if action == "scale":
            scale = max(
                0.2,
                min(
                    3.0,
                    start_scale * math.exp((delta_x - delta_y) * 0.006),
                ),
            )
            self._preset.panel_scale = scale
            if self._overlay_scale_transform is not None:
                self._overlay_scale_transform.setScale3D(
                    QVector3D(scale, scale, scale)
                )
            self.overlay_scaled.emit(scale)

    def _axis_screen_delta(
        self,
        axis: str,
        delta_x: float,
        delta_y: float,
    ) -> QVector3D:
        world_axis = self._gun_axis_world(axis)
        forward = (
            self.camera.viewCenter() - self.camera.position()
        ).normalized()
        camera_up = self.camera.upVector().normalized()
        right = QVector3D.crossProduct(forward, camera_up).normalized()
        up = QVector3D.crossProduct(right, forward).normalized()
        screen_x = QVector3D.dotProduct(world_axis, right)
        screen_y = -QVector3D.dotProduct(world_axis, up)
        distance = max(
            (
                self.camera.position()
                - self._overlay_world_position
            ).length(),
            0.02,
        )
        world_per_pixel = (
            2.0
            * distance
            * math.tan(math.radians(self.camera.fieldOfView()) * 0.5)
            / max(self.container.height(), 1)
        )
        denominator = screen_x * screen_x + screen_y * screen_y
        if denominator < 0.025:
            amount = delta_y * world_per_pixel
        else:
            amount = (
                delta_x * screen_x + delta_y * screen_y
            ) / denominator * world_per_pixel
        return world_axis * amount

    @staticmethod
    def _local_axis_vector(axis: str) -> QVector3D:
        return {
            "x": QVector3D(1.0, 0.0, 0.0),
            "y": QVector3D(0.0, 1.0, 0.0),
            "z": QVector3D(0.0, 0.0, 1.0),
        }[axis]

    def _gun_axis_world(self, axis: str) -> QVector3D:
        local_axis = self._local_axis_vector(axis)
        pivot_rotation = QQuaternion.fromEulerAngles(
            0.0,
            self._auto_rotate_angle,
            0.0,
        )
        return pivot_rotation.rotatedVector(local_axis).normalized()

    @staticmethod
    def _wrap_degrees(value: float) -> float:
        wrapped = (float(value) + 180.0) % 360.0 - 180.0
        return 180.0 if wrapped == -180.0 else wrapped

    def _place_on_surface(self, part_id: str, event) -> None:
        hit = event.worldIntersection()
        if not isinstance(hit, QVector3D) or hit.isNull():
            self._preset.select_part(part_id, reset_offsets=True)
            self._preset.use_absolute_anchor = False
            self.apply_preset(self._preset)
            return
        normal = self._surface_normal_from_pick(event, hit)
        clearance = max(0.0, float(self._preset.surface_clearance))
        anchor = hit + normal * clearance
        bounds = self.bounds_for_part(part_id)
        if bounds is None:
            return
        low, high = bounds
        self._preset.select_part(part_id, reset_offsets=False)
        self._preset.anchor_local_x = float(anchor.x())
        self._preset.anchor_local_y = float(anchor.y())
        self._preset.anchor_local_z = float(anchor.z())
        self._preset.offset_x = float(anchor.x() - (low[0] + high[0]) * 0.5)
        self._preset.offset_y = float(anchor.y() - high[1])
        self._preset.offset_z = float(anchor.z() - (low[2] + high[2]) * 0.5)
        self._preset.use_absolute_anchor = True
        self._preset.face_player = False
        self._surface_normal = normal
        rotation = QQuaternion.rotationTo(
            QVector3D(1.0, 0.0, 0.0),
            normal,
        )
        euler = rotation.toEulerAngles()
        self._preset.rotation_x = self._wrap_degrees(float(euler.x()))
        self._preset.rotation_y = self._wrap_degrees(float(euler.y()))
        self._preset.rotation_z = self._wrap_degrees(float(euler.z()))
        self.apply_preset(self._preset)
        self.overlay_rotated.emit(
            self._preset.rotation_x,
            self._preset.rotation_y,
            self._preset.rotation_z,
        )

    def _surface_normal_from_pick(
        self,
        event,
        hit: QVector3D,
    ) -> QVector3D:
        normal = None
        try:
            indices = (
                int(event.vertex1Index()),
                int(event.vertex2Index()),
                int(event.vertex3Index()),
            )
            entity = event.entity()
            mesh = next(
                (
                    component
                    for component in entity.components()
                    if isinstance(component, Qt3DRender.QMesh)
                ),
                None,
            )
            if mesh is not None:
                path = mesh.source().toLocalFile()
                vertices: list[QVector3D] = []
                with open(path, "r", encoding="utf-8", errors="ignore") as handle:
                    for line in handle:
                        if not line.startswith("v "):
                            continue
                        values = line.split()
                        if len(values) >= 4:
                            vertices.append(
                                QVector3D(
                                    float(values[1]),
                                    float(values[2]),
                                    float(values[3]),
                                )
                            )
                p1, p2, p3 = (vertices[index] for index in indices)
                candidate = QVector3D.crossProduct(p2 - p1, p3 - p1)
                if candidate.lengthSquared() > 0.00000001:
                    candidate = candidate.normalized()
                    candidate = QQuaternion.fromEulerAngles(
                        0.0,
                        self._auto_rotate_angle,
                        0.0,
                    ).rotatedVector(candidate)
                    normal = candidate
        except (AttributeError, OSError, StopIteration, ValueError):
            normal = None
        if normal is None or normal.lengthSquared() < 0.000001:
            towards_camera = self.camera.position() - hit
            normal = (
                towards_camera.normalized()
                if towards_camera.lengthSquared() > 0.000001
                else QVector3D(0.0, 1.0, 0.0)
            )
        if QVector3D.dotProduct(normal, self.camera.position() - hit) < 0.0:
            normal = -normal
        return normal.normalized()

    def _commit_anchor(self, anchor: QVector3D) -> None:
        bounds = self.bounds_for_part(self.selected_part_id)
        if bounds is None:
            return
        low, high = bounds
        base_x = float((low[0] + high[0]) * 0.5)
        base_y = float(high[1] + self._preset.surface_clearance)
        base_z = float((low[2] + high[2]) * 0.5)
        self._preset.offset_x = float(anchor.x()) - base_x
        self._preset.offset_y = float(anchor.y()) - float(high[1])
        self._preset.offset_z = float(anchor.z()) - base_z
        self._preset.anchor_local_x = float(anchor.x())
        self._preset.anchor_local_y = float(anchor.y())
        self._preset.anchor_local_z = float(anchor.z())
        self._preset.use_absolute_anchor = True
        self._update_normalized_anchor(
            self._preset,
            anchor,
            (low, high),
        )
        self._overlay_world_position = anchor
        self._overlay_transform.setTranslation(anchor)
        if self._gizmo_anchor_transform is not None:
            self._gizmo_anchor_transform.setTranslation(anchor)
        self._face_camera_tick()
        self.overlay_moved.emit(
            self._preset.offset_x,
            self._preset.offset_y,
            self._preset.offset_z,
        )

    @staticmethod
    def _update_normalized_anchor(
        preset: DlyDisplayPreset,
        anchor: QVector3D,
        bounds: tuple[np.ndarray, np.ndarray],
    ) -> None:
        low = np.asarray(bounds[0], dtype=np.float32)
        high = np.asarray(bounds[1], dtype=np.float32)
        size = high - low

        def normalized(value: float, minimum: float, extent: float) -> float:
            if abs(float(extent)) < 0.00000001:
                return 0.5
            return float((value - minimum) / extent)

        preset.normalized_anchor_x = normalized(
            anchor.x(),
            float(low[0]),
            float(size[0]),
        )
        preset.normalized_anchor_y = normalized(
            anchor.y(),
            float(low[1]),
            float(size[1]),
        )
        preset.normalized_anchor_z = normalized(
            anchor.z(),
            float(low[2]),
            float(size[2]),
        )

    def _set_overlay_rotation(self) -> None:
        if self._overlay_transform is None:
            return
        if self._preset.face_player:
            return
        self._overlay_transform.setRotation(
            QQuaternion.fromEulerAngles(
                self._preset.rotation_x,
                self._preset.rotation_y,
                self._preset.rotation_z,
            )
        )

    def _face_camera_tick(self) -> None:
        if self._overlay_transform is None:
            return
        if self._preset.face_player:
            low, high = self._bounds
            center_array = (low + high) * 0.5
            center = QVector3D(
                float(center_array[0]),
                float(center_array[1]),
                float(center_array[2]),
            )
            pivot_rotation = QQuaternion.fromEulerAngles(
                0.0,
                self._auto_rotate_angle,
                0.0,
            )
            world_anchor = center + pivot_rotation.rotatedVector(
                self._overlay_world_position - center
            )
            direction_world = self.camera.position() - world_anchor
            direction = QQuaternion.fromEulerAngles(
                0.0,
                -self._auto_rotate_angle,
                0.0,
            ).rotatedVector(direction_world)
            if direction.lengthSquared() > 0.000001:
                self._overlay_transform.setRotation(
                    QQuaternion.rotationTo(
                        QVector3D(1.0, 0.0, 0.0),
                        direction.normalized(),
                    )
                )
            return
        self._set_overlay_rotation()

    def set_fixed_view(self, view_name: str) -> None:
        allowed = {"front", "back", "left", "right", "top", "bottom", "iso"}
        self._fixed_view = view_name if view_name in allowed else "iso"
        self._preset.fixed_view_preference = self._fixed_view
        self.fixed_view_changed.emit(self._fixed_view)

    def apply_fixed_view(
        self,
        view_name: str | None = None,
        *,
        preserve_distance: bool = False,
    ) -> None:
        self.set_fixed_view(view_name or self._fixed_view)
        low, high = self._bounds
        center_array = (low + high) * 0.5
        center = QVector3D(
            float(center_array[0]),
            float(center_array[1]),
            float(center_array[2]),
        )
        extent = max(float(np.max(high - low)), 0.05)
        if preserve_distance:
            distance = max(
                (self.camera.position() - self.camera.viewCenter()).length(),
                extent * 0.2,
            )
        else:
            distance = extent * 1.9
        view_positions = {
            "front": (QVector3D(0.0, 0.0, -distance), QVector3D(0.0, 1.0, 0.0)),
            "back": (QVector3D(0.0, 0.0, distance), QVector3D(0.0, 1.0, 0.0)),
            "left": (QVector3D(-distance, 0.0, 0.0), QVector3D(0.0, 1.0, 0.0)),
            "right": (QVector3D(distance, 0.0, 0.0), QVector3D(0.0, 1.0, 0.0)),
            "top": (QVector3D(0.0, distance, 0.0), QVector3D(0.0, 0.0, -1.0)),
            "bottom": (QVector3D(0.0, -distance, 0.0), QVector3D(0.0, 0.0, 1.0)),
            "iso": (
                QVector3D(distance * 0.62, distance * 0.82, distance * 0.62),
                QVector3D(0.0, 1.0, 0.0),
            ),
        }
        offset, up = view_positions[self._fixed_view]
        self.auto_rotate_check.setChecked(False)
        self._set_auto_rotate(False, notify=False)
        self._auto_rotate_angle = 0.0
        self.model_pivot_transform.setRotationY(0.0)
        self.camera.setPosition(center + offset)
        self.camera.setUpVector(up)
        self.camera.setViewCenter(center)
        self._face_camera_tick()

    def _view_direction(self, view_name: str) -> QVector3D:
        return {
            "front": QVector3D(0.0, 0.0, -1.0),
            "back": QVector3D(0.0, 0.0, 1.0),
            "left": QVector3D(-1.0, 0.0, 0.0),
            "right": QVector3D(1.0, 0.0, 0.0),
            "top": QVector3D(0.0, 1.0, 0.0),
            "bottom": QVector3D(0.0, -1.0, 0.0),
            "iso": QVector3D(0.62, 0.82, 0.62).normalized(),
        }[view_name]

    def nearest_fixed_view(self) -> str:
        direction = (
            self.camera.position() - self.camera.viewCenter()
        )
        if direction.lengthSquared() < 0.000001:
            return "iso"
        direction = direction.normalized()
        return max(
            ("front", "back", "left", "right", "top", "bottom"),
            key=lambda name: QVector3D.dotProduct(
                direction,
                self._view_direction(name),
            ),
        )

    def focus_overlay(self) -> None:
        if self._overlay_transform is None:
            return
        size = max(
            self._preset.panel_width * self._preset.panel_scale,
            self._preset.panel_height * self._preset.panel_scale,
            0.004,
        )
        distance = max(size * 3.2, 0.055)
        direction = (
            self.camera.position() - self.camera.viewCenter()
        )
        if direction.lengthSquared() < 0.000001:
            direction = QVector3D(0.0, 0.2, 1.0)
        direction = direction.normalized()
        self.camera.setViewCenter(self._overlay_world_position)
        self.camera.setPosition(
            self._overlay_world_position + direction * distance
        )
        self._face_camera_tick()
        self.transform_status.emit(
            "相机已聚焦子弹显示屏幕"
        )

    def toggle_projection(self) -> None:
        aspect = max(
            0.1,
            self.container.width() / max(self.container.height(), 1),
        )
        if self._perspective_projection:
            height = 0.22
            width = height * aspect
            self.camera.lens().setOrthographicProjection(
                -width * 0.5,
                width * 0.5,
                -height * 0.5,
                height * 0.5,
                0.001,
                30.0,
            )
            self._perspective_projection = False
            self.transform_status.emit("视图投影：正交")
        else:
            self.camera.lens().setPerspectiveProjection(
                35.0,
                aspect,
                0.001,
                30.0,
            )
            self._perspective_projection = True
            self.transform_status.emit("视图投影：透视")

    def _start_modal_transform(self, action: str) -> None:
        if (
            action not in {"move", "rotate", "scale"}
            or self._overlay_transform is None
        ):
            return
        self.set_transform_tool(action)
        self._overlay_selected = True
        self._modal_transform = action
        self._modal_axis = None
        self._modal_start_mouse = None
        self._modal_start_position = self._overlay_world_position
        self._modal_start_rotation = (
            self._preset.rotation_x,
            self._preset.rotation_y,
            self._preset.rotation_z,
        )
        self._modal_start_scale = self._preset.panel_scale
        self._modal_start_face_player = self._preset.face_player
        if action == "rotate":
            self._preset.face_player = False
            self._set_overlay_rotation()
            self.overlay_rotated.emit(
                self._preset.rotation_x,
                self._preset.rotation_y,
                self._preset.rotation_z,
            )
        self.controller.setEnabled(False)
        self.container.setFocus(Qt.FocusReason.ShortcutFocusReason)
        self.setFocus(Qt.FocusReason.ShortcutFocusReason)
        labels = {"move": "移动", "rotate": "旋转", "scale": "缩放"}
        self.transform_status.emit(
            f"{labels[action]}模式：移动鼠标预览；X/Y/Z 锁定轴；"
            "左键或 Enter 确认；右键或 Esc 取消"
        )

    def _update_modal_transform(self, event) -> None:
        if self._modal_transform is None or not isinstance(event, QMouseEvent):
            return
        position = event.position()
        current = (float(position.x()), float(position.y()))
        if self._modal_start_mouse is None:
            self._modal_start_mouse = current
            return
        delta_x = current[0] - self._modal_start_mouse[0]
        delta_y = current[1] - self._modal_start_mouse[1]
        self._apply_transform_delta(
            self._modal_transform,
            self._modal_axis,
            delta_x,
            delta_y,
            self._modal_start_position,
            self._modal_start_rotation,
            self._modal_start_scale,
        )

    def _commit_modal_transform(self) -> None:
        if self._modal_transform is None:
            return
        action = self._modal_transform
        self._modal_transform = None
        self._modal_axis = None
        self._modal_start_mouse = None
        self.controller.setEnabled(True)
        labels = {"move": "移动", "rotate": "旋转", "scale": "缩放"}
        self.transform_status.emit(f"{labels[action]}已确认并自动保存")

    def _cancel_modal_transform(self) -> None:
        if self._modal_transform is None:
            return
        self._preset.anchor_local_x = float(
            self._modal_start_position.x()
        )
        self._preset.anchor_local_y = float(
            self._modal_start_position.y()
        )
        self._preset.anchor_local_z = float(
            self._modal_start_position.z()
        )
        self._preset.use_absolute_anchor = True
        (
            self._preset.rotation_x,
            self._preset.rotation_y,
            self._preset.rotation_z,
        ) = self._modal_start_rotation
        self._preset.panel_scale = self._modal_start_scale
        self._preset.face_player = self._modal_start_face_player
        self._commit_anchor(self._modal_start_position)
        if self._overlay_scale_transform is not None:
            self._overlay_scale_transform.setScale3D(
                QVector3D(
                    self._modal_start_scale,
                    self._modal_start_scale,
                    self._modal_start_scale,
                )
            )
        self._set_overlay_rotation()
        self.overlay_rotated.emit(
            self._preset.rotation_x,
            self._preset.rotation_y,
            self._preset.rotation_z,
        )
        self.overlay_scaled.emit(self._preset.panel_scale)
        self._modal_transform = None
        self._modal_axis = None
        self._modal_start_mouse = None
        self.controller.setEnabled(True)
        self.transform_status.emit("已取消变换并恢复原位置")

    def eventFilter(self, watched, event) -> bool:
        event_type = event.type()
        if self._modal_transform is not None:
            if event_type == QEvent.Type.KeyPress:
                key = event.key()
                if key in (Qt.Key.Key_X, Qt.Key.Key_Y, Qt.Key.Key_Z):
                    self._modal_axis = {
                        Qt.Key.Key_X: "x",
                        Qt.Key.Key_Y: "y",
                        Qt.Key.Key_Z: "z",
                    }[key]
                    self._modal_start_mouse = None
                    self.transform_status.emit(
                        f"已锁定枪模局部 {self._modal_axis.upper()} 轴"
                    )
                    event.accept()
                    return True
                if key in (Qt.Key.Key_Return, Qt.Key.Key_Enter):
                    self._commit_modal_transform()
                    event.accept()
                    return True
                if key == Qt.Key.Key_Escape:
                    self._cancel_modal_transform()
                    event.accept()
                    return True
            if event_type == QEvent.Type.MouseMove:
                self._update_modal_transform(event)
                event.accept()
                return True
            if event_type == QEvent.Type.MouseButtonPress:
                if event.button() == Qt.MouseButton.LeftButton:
                    self._commit_modal_transform()
                elif event.button() == Qt.MouseButton.RightButton:
                    self._cancel_modal_transform()
                event.accept()
                return True
        if (
            event_type == QEvent.Type.MouseButtonPress
            and event.button() == Qt.MouseButton.RightButton
            and event.modifiers() & Qt.KeyboardModifier.AltModifier
        ):
            self._alt_view_drag_active = True
            self._alt_view_dragged = False
            position = event.position()
            self._alt_view_last_position = (
                float(position.x()),
                float(position.y()),
            )
            self.controller.setEnabled(False)
            self.container.setFocus(Qt.FocusReason.MouseFocusReason)
            event.accept()
            return True
        if (
            event_type == QEvent.Type.MouseMove
            and self._alt_view_drag_active
            and isinstance(event, QMouseEvent)
        ):
            position = event.position()
            current = (float(position.x()), float(position.y()))
            if self._alt_view_last_position is not None:
                delta_x = (
                    current[0] - self._alt_view_last_position[0]
                )
                delta_y = (
                    current[1] - self._alt_view_last_position[1]
                )
                if abs(delta_x) + abs(delta_y) > 0.05:
                    self._alt_view_dragged = True
                    self._orbit_camera_drag(delta_x, delta_y)
            self._alt_view_last_position = current
            event.accept()
            return True
        if (
            event_type == QEvent.Type.MouseButtonRelease
            and event.button() == Qt.MouseButton.RightButton
            and self._alt_view_drag_active
        ):
            self._alt_view_drag_active = False
            self._alt_view_last_position = None
            self.controller.setEnabled(True)
            nearest = self.nearest_fixed_view()
            self.apply_fixed_view(nearest, preserve_distance=True)
            self.transform_status.emit(
                f"视角已吸附到固定方向：{nearest}"
            )
            event.accept()
            return True
        if event_type == QEvent.Type.KeyPress:
            key_map = {
                Qt.Key.Key_1: "front",
                Qt.Key.Key_3: "right",
                Qt.Key.Key_7: "top",
            }
            selected_view = key_map.get(event.key())
            if selected_view is not None:
                self.apply_fixed_view(selected_view)
                event.accept()
                return True
            if event.key() == Qt.Key.Key_5:
                self.toggle_projection()
                event.accept()
                return True
            transform_keys = {
                Qt.Key.Key_G: "move",
                Qt.Key.Key_R: "rotate",
                Qt.Key.Key_S: "scale",
            }
            action = transform_keys.get(event.key())
            if action is not None and not event.isAutoRepeat():
                self._start_modal_transform(action)
                event.accept()
                return True
        return super().eventFilter(watched, event)

    def _orbit_camera_drag(self, delta_x: float, delta_y: float) -> None:
        center = self.camera.viewCenter()
        camera_up = self.camera.upVector().normalized()
        forward = (center - self.camera.position()).normalized()
        right = QVector3D.crossProduct(forward, camera_up).normalized()
        offset = self.camera.position() - center
        yaw = QQuaternion.fromAxisAndAngle(
            camera_up,
            -delta_x * 0.42,
        )
        pitched_up = yaw.rotatedVector(camera_up)
        pitch = QQuaternion.fromAxisAndAngle(
            right,
            -delta_y * 0.42,
        )
        offset = pitch.rotatedVector(yaw.rotatedVector(offset))
        new_up = pitch.rotatedVector(pitched_up)
        if abs(QVector3D.dotProduct(new_up, offset.normalized())) > 0.985:
            return
        self.camera.setPosition(center + offset)
        self.camera.setUpVector(new_up.normalized())


    def reset_camera(self) -> None:
        super().reset_camera()
        low, high = self._bounds
        center_array = (low + high) * 0.5
        extent = max(float(np.max(high - low)), 0.05)
        center = QVector3D(
            float(center_array[0]),
            float(center_array[1]),
            float(center_array[2]),
        )
        self.camera.setPosition(
            center + QVector3D(extent * 1.20, extent * 1.80, 0.0)
        )
        self.camera.setViewCenter(center)
