from __future__ import annotations

import math
from pathlib import Path

from PySide6.QtCore import QPointF, QRectF, Qt, Signal
from PySide6.QtGui import QColor, QImage, QMouseEvent, QPainter, QPen, QPixmap
from PySide6.QtWidgets import QWidget

from .assembly import FORGE_SCENE_TO_GAME_SCALE
from .dynamic_visuals import (
    DynamicLayer,
    DynamicVisualProject,
    compose_preview_skin,
    frame_for_layer,
    hel_item,
    layer_frames,
)
from .dly import DlyDisplayPreset
from .dly_viewer import DlyPreviewView
from .models import GameInstall


class DynamicUvCanvas(QWidget):
    layer_selected = Signal(str)
    layer_rect_changed = Signal(str, float, float, float, float)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setMinimumSize(420, 360)
        self.setMouseTracking(True)
        self._pixmap = QPixmap()
        self._image_size = (1024, 1024)
        self._layers: list[DynamicLayer] = []
        self._selected_id = ""
        self._drag_mode: str | None = None
        self._drag_origin = QPointF()
        self._start_rect = (0.0, 0.0, 0.0, 0.0)
        self._viewport = QRectF()

    def set_context(
        self,
        image_path: Path | None,
        layers: list[DynamicLayer],
        selected_id: str,
    ) -> None:
        if image_path and image_path.is_file():
            pixmap = QPixmap(str(image_path))
            if not pixmap.isNull():
                self._pixmap = pixmap
                self._image_size = (pixmap.width(), pixmap.height())
        else:
            self._pixmap = QPixmap()
        self._layers = list(layers)
        self._selected_id = selected_id
        self.update()

    def _update_viewport(self) -> None:
        width = max(1, self.width() - 24)
        height = max(1, self.height() - 24)
        image_ratio = self._image_size[0] / max(1, self._image_size[1])
        widget_ratio = width / height
        if image_ratio >= widget_ratio:
            draw_width = float(width)
            draw_height = draw_width / image_ratio
        else:
            draw_height = float(height)
            draw_width = draw_height * image_ratio
        self._viewport = QRectF(
            (self.width() - draw_width) / 2.0,
            (self.height() - draw_height) / 2.0,
            draw_width,
            draw_height,
        )

    def _layer_rect(self, layer: DynamicLayer) -> QRectF:
        return QRectF(
            self._viewport.x() + layer.uv_x * self._viewport.width(),
            self._viewport.y() + layer.uv_y * self._viewport.height(),
            layer.uv_width * self._viewport.width(),
            layer.uv_height * self._viewport.height(),
        )

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor("#0b0f12"))
        self._update_viewport()
        if not self._pixmap.isNull():
            painter.drawPixmap(self._viewport, self._pixmap, QRectF(self._pixmap.rect()))
        painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)
        for layer in reversed(self._layers):
            if not layer.visible:
                continue
            rect = self._layer_rect(layer)
            painter.save()
            painter.translate(rect.center())
            painter.rotate(layer.uv_rotation)
            painter.translate(-rect.center())
            selected = layer.id == self._selected_id
            color = QColor("#ffd45c" if selected else "#56d9e8")
            painter.setPen(QPen(color, 2.0 if selected else 1.2))
            painter.setBrush(QColor(255, 212, 92, 24) if selected else Qt.BrushStyle.NoBrush)
            painter.drawRect(rect)
            if selected:
                painter.setBrush(QColor("#fff4cf"))
                size = 7.0
                painter.drawRect(
                    QRectF(rect.right() - size / 2, rect.bottom() - size / 2, size, size)
                )
            painter.restore()
        painter.setPen(QPen(QColor("#617177"), 1))
        painter.drawRect(self._viewport)
        painter.end()

    def _to_uv(self, point: QPointF) -> tuple[float, float]:
        return (
            (point.x() - self._viewport.x()) / max(1.0, self._viewport.width()),
            (point.y() - self._viewport.y()) / max(1.0, self._viewport.height()),
        )

    def mousePressEvent(self, event: QMouseEvent) -> None:
        if event.button() != Qt.MouseButton.LeftButton:
            return
        point = event.position()
        selected = next(
            (layer for layer in self._layers if layer.id == self._selected_id),
            None,
        )
        if selected is not None:
            rect = self._layer_rect(selected)
            corner = QRectF(rect.right() - 12, rect.bottom() - 12, 24, 24)
            if corner.contains(point):
                self._drag_mode = "resize"
                self._drag_origin = point
                self._start_rect = (
                    selected.uv_x,
                    selected.uv_y,
                    selected.uv_width,
                    selected.uv_height,
                )
                return
        for layer in self._layers:
            if layer.visible and self._layer_rect(layer).contains(point):
                self._selected_id = layer.id
                self.layer_selected.emit(layer.id)
                self._drag_mode = "move"
                self._drag_origin = point
                self._start_rect = (
                    layer.uv_x,
                    layer.uv_y,
                    layer.uv_width,
                    layer.uv_height,
                )
                self.update()
                return
        self._drag_mode = None

    def mouseMoveEvent(self, event: QMouseEvent) -> None:
        if self._drag_mode is None:
            return
        layer = next(
            (item for item in self._layers if item.id == self._selected_id),
            None,
        )
        if layer is None or layer.locked:
            return
        delta_u = (event.position().x() - self._drag_origin.x()) / max(
            1.0, self._viewport.width()
        )
        delta_v = (event.position().y() - self._drag_origin.y()) / max(
            1.0, self._viewport.height()
        )
        x, y, width, height = self._start_rect
        if self._drag_mode == "move":
            x = min(1.0 - width, max(0.0, x + delta_u))
            y = min(1.0 - height, max(0.0, y + delta_v))
        else:
            width = min(1.0 - x, max(0.005, width + delta_u))
            height = min(1.0 - y, max(0.005, height + delta_v))
        self.layer_rect_changed.emit(layer.id, x, y, width, height)

    def mouseReleaseEvent(self, event: QMouseEvent) -> None:
        self._drag_mode = None


class DynamicVisualPreviewView(DlyPreviewView):
    def __init__(self, parent=None):
        super().__init__(parent)
        self._dynamic_project: DynamicVisualProject | None = None
        self._dynamic_project_path: Path | None = None
        self._dynamic_game: GameInstall | None = None
        self._dynamic_layer: DynamicLayer | None = None
        self._dynamic_time = 0.0
        self._dynamic_preview_cache: Path | None = None
        self._preview_serial = 0

    def load_dynamic(
        self,
        game: GameInstall,
        project: DynamicVisualProject,
        project_path: Path,
        layer: DynamicLayer | None,
        *,
        time_seconds: float,
        cache_dir: Path,
    ) -> None:
        self._dynamic_game = game
        self._dynamic_project = project
        self._dynamic_project_path = project_path
        self._dynamic_layer = layer
        self._dynamic_time = max(0.0, float(time_seconds))
        self._dynamic_preview_cache = cache_dir
        skin_project = compose_preview_skin(
            project,
            project_path,
            game,
            time_seconds=self._dynamic_time,
            cache_dir=cache_dir / "compose",
        )
        preset = DlyDisplayPreset(
            target_item_id="auto_pistol",
            anchor_part_id=project.selected_part_id,
            use_absolute_anchor=False,
            panel_width=0.08,
            panel_height=0.05,
            panel_color="#00000000",
            border_color="#55dbe8",
            panel_opacity=0.0,
            border_opacity=0.8,
            face_player=True,
        )
        if layer is not None:
            preset.anchor_part_id = layer.plane_part_id or layer.part_id
            preset.use_absolute_anchor = bool(layer.use_absolute_anchor)
            preset.anchor_local_x = layer.plane_x
            preset.anchor_local_y = layer.plane_y
            preset.anchor_local_z = layer.plane_z
            preset.rotation_x = layer.plane_rx
            preset.rotation_y = layer.plane_ry
            preset.rotation_z = layer.plane_rz
            preset.panel_scale = layer.plane_scale
            preset.panel_width = max(0.01, layer.uv_width * 0.18)
            preset.panel_height = max(0.006, layer.uv_height * 0.18)
        self._preset = preset
        self._pickers.clear()
        self._loaded_key = None
        self.load_weapon(
            game,
            hel_item(),
            skin_project,
            unit_scale=FORGE_SCENE_TO_GAME_SCALE,
        )
        self._install_part_pickers()
        if layer is not None:
            frames = layer_frames(project_path, layer)
            frame = frame_for_layer(layer, frames, self._dynamic_time)
            self._preview_serial += 1
            preview = cache_dir / f"layer_preview_{self._preview_serial:08d}.png"
            preview.parent.mkdir(parents=True, exist_ok=True)
            frame.save(preview)
            self.set_custom_overlay_image(preview)
        self.apply_preset(preset)
