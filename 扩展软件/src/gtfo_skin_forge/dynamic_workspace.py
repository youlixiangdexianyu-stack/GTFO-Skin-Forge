from __future__ import annotations

import copy
from pathlib import Path

from PySide6.QtCore import Qt, QTimer, Signal
from PySide6.QtGui import QColor, QIcon, QImage, QPainter, QPen, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView,
    QCheckBox,
    QComboBox,
    QDoubleSpinBox,
    QFileDialog,
    QFrame,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QSpinBox,
    QSplitter,
    QTabWidget,
    QTableWidget,
    QTableWidgetItem,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from .assets import extract_texture
from .config import ensure_app_dirs
from .dly import DLY_HEL_PARTS, find_gtfo_profile_root
from .dynamic_visuals import (
    CHANNELS,
    DYNAMIC_CHANNEL_SLOTS,
    DynamicBuildResult,
    DynamicLayer,
    DynamicVisualProject,
    add_text_layer,
    autosave_dynamic_path,
    build_dynamic_project,
    copy_media,
    duplicate_layer,
    effective_timeline,
    hel_item,
    import_image_layer,
    import_sequence_layer,
    import_video_layer,
    install_dynamic_build,
    load_dynamic_project,
    project_media_dir,
    save_dynamic_project,
    uninstall_dynamic_build,
)
from .dynamic_viewer import DynamicUvCanvas, DynamicVisualPreviewView
from .game_locator import is_gtfo_running
from .models import GameInstall


class DynamicVisualWorkspace(QWidget):
    status_message = Signal(str)

    def __init__(
        self,
        *,
        app_root: Path,
        game: GameInstall | None = None,
        parent=None,
    ):
        super().__init__(parent)
        self.app_root = app_root
        self.game = game
        self.item = hel_item()
        self.project_path = autosave_dynamic_path()
        self.project = self._load_initial_project()
        self.profile_root = find_gtfo_profile_root(start=app_root)
        self._updating = False
        self._play_time = 0.0
        self._last_build: DynamicBuildResult | None = None
        self._base_texture_cache: dict[tuple[str, str], Path] = {}
        self._preview_cache = ensure_app_dirs()["cache"] / "dynamic_visuals"
        self._preview_cache.mkdir(parents=True, exist_ok=True)
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(450)
        self._save_timer.timeout.connect(self._autosave)
        self._preview_timer = QTimer(self)
        self._preview_timer.setSingleShot(True)
        self._preview_timer.setInterval(120)
        self._preview_timer.timeout.connect(self._refresh_all_previews)
        self._play_timer = QTimer(self)
        self._play_timer.setInterval(50)
        self._play_timer.timeout.connect(self._play_tick)
        self._build_ui()
        self._populate_parts()
        self._refresh_layer_table()
        self._sync_controls()
        self.set_game(game)

    def _load_initial_project(self) -> DynamicVisualProject:
        path = autosave_dynamic_path()
        if path.is_file():
            try:
                return load_dynamic_project(path)
            except (OSError, ValueError):
                pass
        return DynamicVisualProject(
            name="HEL 动态视觉",
            item_id="auto_pistol",
            selected_part_id="front",
            selected_channel="Albedo",
        )

    def _build_ui(self) -> None:
        outer = QVBoxLayout(self)
        outer.setContentsMargins(12, 10, 12, 8)
        outer.setSpacing(8)
        title_row = QHBoxLayout()
        title_box = QVBoxLayout()
        title = QLabel("动态视觉贴花")
        title.setStyleSheet("font-size: 20px; font-weight: 700; color: #9ff8ff;")
        subtitle = QLabel("HEL 自动手枪 · 四通道 · 图片 / 序列 / MP4 / 文字")
        subtitle.setStyleSheet("color: #82969b;")
        title_box.addWidget(title)
        title_box.addWidget(subtitle)
        title_row.addLayout(title_box, 1)
        self.timeline_label = QLabel("0.00s")
        self.timeline_label.setStyleSheet("color: #d8ff67;")
        self.loop_check = QCheckBox("循环")
        self.loop_check.setChecked(self.project.loop)
        self.loop_check.toggled.connect(self._loop_changed)
        self.play_button = QPushButton("播放")
        self.play_button.setCheckable(True)
        self.play_button.toggled.connect(self._toggle_play)
        title_row.addWidget(self.timeline_label)
        title_row.addWidget(self.loop_check)
        title_row.addWidget(self.play_button)
        outer.addLayout(title_row)

        self.splitter = QSplitter(Qt.Orientation.Horizontal)
        self.splitter.setChildrenCollapsible(False)
        outer.addWidget(self.splitter, 1)
        self._build_left_panel()
        self._build_center_panel()
        self._build_properties_panel()
        self.splitter.setStretchFactor(0, 0)
        self.splitter.setStretchFactor(1, 1)
        self.splitter.setStretchFactor(2, 0)
        self.splitter.setSizes([310, 780, 360])

        footer = QFrame()
        footer.setStyleSheet(
            "QFrame { border-top: 1px solid #303a3f; background: #151a1d; }"
        )
        footer_layout = QHBoxLayout(footer)
        footer_layout.setContentsMargins(8, 7, 8, 7)
        self.project_name = QLineEdit(self.project.name)
        self.project_name.setMaximumWidth(220)
        self.project_name.editingFinished.connect(self._name_changed)
        self.profile_label = QLineEdit()
        self.profile_label.setReadOnly(True)
        self.profile_label.setPlaceholderText("未找到 BepInEx 测试档")
        self.profile_label.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Fixed,
        )
        choose_profile = QPushButton("选择测试档")
        choose_profile.clicked.connect(self._choose_profile)
        open_button = QPushButton("打开工程")
        open_button.clicked.connect(self._open_project)
        save_button = QPushButton("保存工程")
        save_button.clicked.connect(self._save_project)
        export_button = QPushButton("导出动态包")
        export_button.clicked.connect(self._export_pack)
        install_button = QPushButton("安装到游戏")
        install_button.setObjectName("Primary")
        install_button.clicked.connect(self._install_pack)
        restore_button = QPushButton("恢复上一版")
        restore_button.clicked.connect(self._restore_previous)
        self.status_label = QLabel("可编辑图层后导出或直接安装")
        self.status_label.setStyleSheet("color: #8fa1a6; border: none;")
        self.status_message.connect(self.status_label.setText)
        footer_layout.addWidget(QLabel("工程"))
        footer_layout.addWidget(self.project_name)
        footer_layout.addWidget(QLabel("配置目标"))
        footer_layout.addWidget(self.profile_label, 1)
        footer_layout.addWidget(choose_profile)
        footer_layout.addWidget(self.status_label)
        footer_layout.addWidget(open_button)
        footer_layout.addWidget(save_button)
        footer_layout.addWidget(restore_button)
        footer_layout.addWidget(export_button)
        footer_layout.addWidget(install_button)
        outer.addWidget(footer)

    def _build_left_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(290)
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(0, 0, 8, 0)
        heading = QLabel("HEL 图层")
        heading.setStyleSheet("font-size: 15px; font-weight: 700; color: #d8ff67;")
        layout.addWidget(heading)
        part_row = QHBoxLayout()
        part_row.addWidget(QLabel("部件"))
        self.part_combo = QComboBox()
        self.part_combo.currentIndexChanged.connect(self._part_changed)
        part_row.addWidget(self.part_combo, 1)
        layout.addLayout(part_row)
        channel_row = QHBoxLayout()
        channel_row.addWidget(QLabel("通道"))
        self.channel_combo = QComboBox()
        self.channel_combo.addItems(CHANNELS)
        self.channel_combo.currentTextChanged.connect(self._channel_changed)
        channel_row.addWidget(self.channel_combo, 1)
        layout.addLayout(channel_row)
        self.layer_table = QTableWidget(0, 4)
        self.layer_table.setHorizontalHeaderLabels(["显示", "图层", "类型", "通道"])
        self.layer_table.verticalHeader().setVisible(False)
        self.layer_table.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows
        )
        self.layer_table.setSelectionMode(
            QAbstractItemView.SelectionMode.SingleSelection
        )
        self.layer_table.setEditTriggers(
            QAbstractItemView.EditTrigger.NoEditTriggers
        )
        self.layer_table.setAlternatingRowColors(True)
        self.layer_table.itemSelectionChanged.connect(self._layer_selected)
        self.layer_table.setColumnWidth(0, 48)
        self.layer_table.setColumnWidth(2, 72)
        self.layer_table.setColumnWidth(3, 112)
        layout.addWidget(self.layer_table, 1)
        add_row = QHBoxLayout()
        image_button = QPushButton("图片")
        image_button.clicked.connect(self._add_image)
        sequence_button = QPushButton("轮播")
        sequence_button.clicked.connect(self._add_sequence)
        video_button = QPushButton("MP4")
        video_button.clicked.connect(self._add_video)
        text_button = QPushButton("文字")
        text_button.clicked.connect(self._add_text)
        add_row.addWidget(image_button)
        add_row.addWidget(sequence_button)
        add_row.addWidget(video_button)
        add_row.addWidget(text_button)
        layout.addLayout(add_row)
        action_row = QHBoxLayout()
        duplicate_button = QPushButton("复制图层")
        duplicate_button.clicked.connect(self._duplicate_layer)
        delete_button = QPushButton("删除图层")
        delete_button.clicked.connect(self._delete_layer)
        action_row.addWidget(duplicate_button)
        action_row.addWidget(delete_button)
        layout.addLayout(action_row)
        self.splitter.addWidget(panel)

    def _build_center_panel(self) -> None:
        self.preview_tabs = QTabWidget()
        self.preview_view = DynamicVisualPreviewView()
        self.preview_view.overlay_moved.connect(self._overlay_moved)
        self.preview_view.overlay_rotated.connect(self._overlay_rotated)
        self.preview_view.overlay_scaled.connect(self._overlay_scaled)
        self.uv_canvas = DynamicUvCanvas()
        self.uv_canvas.layer_selected.connect(self._select_layer_by_id)
        self.uv_canvas.layer_rect_changed.connect(self._uv_rect_changed)
        self.transform_tool_combo = QComboBox()
        self.transform_tool_combo.addItem("移动 G", "move")
        self.transform_tool_combo.addItem("旋转 R", "rotate")
        self.transform_tool_combo.addItem("缩放 S", "scale")
        self.transform_tool_combo.currentIndexChanged.connect(
            self._transform_tool_changed
        )
        self.select_overlay_button = QPushButton("选中并聚焦贴花")
        self.select_overlay_button.clicked.connect(self._focus_selected_layer)
        self.preview_view.controls.insertWidget(1, self.transform_tool_combo)
        self.preview_view.controls.insertWidget(2, self.select_overlay_button)
        self.preview_tabs.addTab(self.preview_view, "3D 贴花")
        self.preview_tabs.addTab(self.uv_canvas, "2D UV")
        self.splitter.addWidget(self.preview_tabs)

    def _build_properties_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(340)
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(8, 0, 0, 0)
        heading = QLabel("图层属性")
        heading.setStyleSheet("font-size: 15px; font-weight: 700; color: #d8ff67;")
        layout.addWidget(heading)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        content = QWidget()
        self.props_layout = QVBoxLayout(content)
        self.props_layout.setContentsMargins(0, 0, 0, 0)
        self.layer_name = QLineEdit()
        self.layer_name.editingFinished.connect(self._property_changed)
        self.props_layout.addWidget(QLabel("名称"))
        self.props_layout.addWidget(self.layer_name)

        transform_group = QGroupBox("位置 / 大小 / 变形")
        transform_layout = QGridLayout(transform_group)
        self.opacity_spin = self._double_spin(0.0, 1.0, 0.01)
        self.rotation_spin = self._double_spin(-180.0, 180.0, 1.0)
        self.scale_x_spin = self._double_spin(0.05, 8.0, 0.05)
        self.scale_y_spin = self._double_spin(0.05, 8.0, 0.05)
        self.uv_x_spin = self._double_spin(-1.0, 2.0, 0.005)
        self.uv_y_spin = self._double_spin(-1.0, 2.0, 0.005)
        self.uv_w_spin = self._double_spin(0.001, 2.0, 0.005)
        self.uv_h_spin = self._double_spin(0.001, 2.0, 0.005)
        controls = [
            ("透明度", self.opacity_spin),
            ("旋转", self.rotation_spin),
            ("X 缩放", self.scale_x_spin),
            ("Y 缩放", self.scale_y_spin),
            ("UV X", self.uv_x_spin),
            ("UV Y", self.uv_y_spin),
            ("UV 宽", self.uv_w_spin),
            ("UV 高", self.uv_h_spin),
        ]
        for index, (label, widget) in enumerate(controls):
            transform_layout.addWidget(QLabel(label), index // 2, (index % 2) * 2)
            transform_layout.addWidget(widget, index // 2, (index % 2) * 2 + 1)
        self.props_layout.addWidget(transform_group)

        crop_group = QGroupBox("裁剪")
        crop_layout = QGridLayout(crop_group)
        self.crop_left = self._double_spin(0.0, 1.0, 0.005)
        self.crop_top = self._double_spin(0.0, 1.0, 0.005)
        self.crop_right = self._double_spin(0.0, 1.0, 0.005)
        self.crop_bottom = self._double_spin(0.0, 1.0, 0.005)
        for index, (label, widget) in enumerate(
            [
                ("左", self.crop_left),
                ("上", self.crop_top),
                ("右", self.crop_right),
                ("下", self.crop_bottom),
            ]
        ):
            crop_layout.addWidget(QLabel(label), index // 2, (index % 2) * 2)
            crop_layout.addWidget(widget, index // 2, (index % 2) * 2 + 1)
        self.props_layout.addWidget(crop_group)

        playback_group = QGroupBox("播放")
        playback_layout = QGridLayout(playback_group)
        self.fps_spin = QDoubleSpinBox()
        self.fps_spin.setRange(1.0, 60.0)
        self.fps_spin.setDecimals(1)
        self.fps_spin.setSuffix(" FPS")
        self.loop_layer = QCheckBox("循环本图层")
        self.fps_spin.valueChanged.connect(self._property_changed)
        self.loop_layer.toggled.connect(self._property_changed)
        playback_layout.addWidget(QLabel("源帧率"), 0, 0)
        playback_layout.addWidget(self.fps_spin, 0, 1)
        playback_layout.addWidget(self.loop_layer, 1, 0, 1, 2)
        self.props_layout.addWidget(playback_group)

        self.text_group = QGroupBox("文字")
        text_layout = QGridLayout(self.text_group)
        self.text_edit = QLineEdit()
        self.font_size_spin = QSpinBox()
        self.font_size_spin.setRange(8, 512)
        self.text_color = QLineEdit("#FFFFFF")
        self.stroke_color = QLineEdit("#000000")
        self.stroke_width_spin = QSpinBox()
        self.stroke_width_spin.setRange(0, 24)
        self.wrap_width_spin = QSpinBox()
        self.wrap_width_spin.setRange(64, 4096)
        self.font_path_edit = QLineEdit()
        self.font_path_edit.setReadOnly(True)
        self.font_button = QPushButton("选择字体")
        self.font_button.clicked.connect(self._choose_font)
        for widget in (
            self.text_edit,
            self.font_size_spin,
            self.text_color,
            self.stroke_color,
            self.stroke_width_spin,
            self.wrap_width_spin,
        ):
            widget.editingFinished.connect(self._property_changed) if hasattr(
                widget, "editingFinished"
            ) else widget.valueChanged.connect(self._property_changed)
        text_layout.addWidget(QLabel("文字"), 0, 0)
        text_layout.addWidget(self.text_edit, 0, 1)
        text_layout.addWidget(QLabel("字体"), 1, 0)
        text_layout.addWidget(self.font_path_edit, 1, 1)
        text_layout.addWidget(self.font_button, 2, 1)
        text_layout.addWidget(QLabel("字号"), 3, 0)
        text_layout.addWidget(self.font_size_spin, 3, 1)
        text_layout.addWidget(QLabel("颜色"), 4, 0)
        text_layout.addWidget(self.text_color, 4, 1)
        text_layout.addWidget(QLabel("描边"), 5, 0)
        text_layout.addWidget(self.stroke_color, 5, 1)
        text_layout.addWidget(QLabel("描边宽"), 6, 0)
        text_layout.addWidget(self.stroke_width_spin, 6, 1)
        text_layout.addWidget(QLabel("换行宽"), 7, 0)
        text_layout.addWidget(self.wrap_width_spin, 7, 1)
        self.props_layout.addWidget(self.text_group)
        self.props_layout.addStretch(1)
        scroll.setWidget(content)
        layout.addWidget(scroll, 1)
        self.splitter.addWidget(panel)

    def _double_spin(self, minimum: float, maximum: float, step: float) -> QDoubleSpinBox:
        spin = QDoubleSpinBox()
        spin.setRange(minimum, maximum)
        spin.setSingleStep(step)
        spin.setDecimals(3)
        spin.valueChanged.connect(self._property_changed)
        return spin

    def _populate_parts(self) -> None:
        self._updating = True
        self.part_combo.clear()
        for part in DLY_HEL_PARTS:
            self.part_combo.addItem(part.label, part.id)
        index = self.part_combo.findData(self.project.selected_part_id)
        self.part_combo.setCurrentIndex(max(0, index))
        channel_index = self.channel_combo.findText(self.project.selected_channel)
        self.channel_combo.setCurrentIndex(max(0, channel_index))
        self._updating = False

    def set_game(self, game: GameInstall | None) -> None:
        self.game = game
        if game is not None:
            self.project.game_root = str(game.root)
        if self.profile_root is None:
            self.profile_root = find_gtfo_profile_root(
                start=self.app_root,
                selected_root=None,
            )
        self.profile_label.setText(
            str(self.profile_root) if self.profile_root else ""
        )
        self._schedule_preview()

    def activate(self) -> None:
        self._refresh_layer_table()
        self._refresh_all_previews()

    def _current_layer(self) -> DynamicLayer | None:
        return self.project.selected_layer()

    def _refresh_layer_table(self) -> None:
        self._updating = True
        try:
            self.layer_table.setRowCount(len(self.project.layers))
            selected_row = -1
            for row, layer in enumerate(self.project.layers):
                visible = QTableWidgetItem("✓" if layer.visible else "")
                visible.setData(Qt.ItemDataRole.UserRole, layer.id)
                visible.setTextAlignment(Qt.AlignmentFlag.AlignCenter)
                self.layer_table.setItem(row, 0, visible)
                self.layer_table.setItem(row, 1, QTableWidgetItem(layer.name))
                kind = {
                    "image": "图片",
                    "sequence": "轮播",
                    "video": "MP4",
                    "text": "文字",
                }.get(layer.kind, layer.kind)
                self.layer_table.setItem(row, 2, QTableWidgetItem(kind))
                self.layer_table.setItem(row, 3, QTableWidgetItem(layer.channel))
                if layer.id == self.project.selected_layer_id:
                    selected_row = row
            if selected_row >= 0:
                self.layer_table.selectRow(selected_row)
        finally:
            self._updating = False

    def _sync_controls(self) -> None:
        layer = self._current_layer()
        self._updating = True
        try:
            enabled = layer is not None
            for widget in (
                self.layer_name,
                self.opacity_spin,
                self.rotation_spin,
                self.scale_x_spin,
                self.scale_y_spin,
                self.uv_x_spin,
                self.uv_y_spin,
                self.uv_w_spin,
                self.uv_h_spin,
                self.crop_left,
                self.crop_top,
                self.crop_right,
                self.crop_bottom,
                self.fps_spin,
                self.loop_layer,
                self.text_group,
            ):
                widget.setEnabled(enabled)
            if layer is None:
                self.text_group.setVisible(False)
                return
            self.layer_name.setText(layer.name)
            self.opacity_spin.setValue(layer.opacity)
            self.rotation_spin.setValue(layer.uv_rotation)
            self.scale_x_spin.setValue(layer.scale_x)
            self.scale_y_spin.setValue(layer.scale_y)
            self.uv_x_spin.setValue(layer.uv_x)
            self.uv_y_spin.setValue(layer.uv_y)
            self.uv_w_spin.setValue(layer.uv_width)
            self.uv_h_spin.setValue(layer.uv_height)
            self.crop_left.setValue(layer.crop_left)
            self.crop_top.setValue(layer.crop_top)
            self.crop_right.setValue(layer.crop_right)
            self.crop_bottom.setValue(layer.crop_bottom)
            self.fps_spin.setValue(layer.fps)
            self.loop_layer.setChecked(layer.loop)
            self.text_group.setVisible(layer.kind == "text")
            self.text_edit.setText(layer.text)
            self.font_path_edit.setText(layer.font_path)
            self.font_size_spin.setValue(layer.font_size)
            self.text_color.setText(layer.text_color)
            self.stroke_color.setText(layer.stroke_color)
            self.stroke_width_spin.setValue(layer.stroke_width)
            self.wrap_width_spin.setValue(layer.wrap_width)
        finally:
            self._updating = False

    def _refresh_all_previews(self) -> None:
        layer = self._current_layer()
        self._refresh_uv_canvas()
        if self.game is None:
            self.preview_view.status.setText("未检测到 GTFO，无法加载 3D 预览")
            return
        try:
            self.preview_view.load_dynamic(
                self.game,
                self.project,
                self.project_path,
                layer,
                time_seconds=self._play_time,
                cache_dir=self._preview_cache,
            )
        except Exception as exc:
            self.preview_view.status.setText(f"3D 预览加载失败：{exc}")

    def _refresh_uv_canvas(self) -> None:
        part_id = str(self.part_combo.currentData() or self.project.selected_part_id)
        channel = self.channel_combo.currentText() or self.project.selected_channel
        image_path = self._base_texture_path(part_id, channel)
        layers = [
            layer
            for layer in self.project.layers
            if layer.part_id == part_id and layer.channel == channel
        ]
        self.uv_canvas.set_context(
            image_path,
            layers,
            self.project.selected_layer_id,
        )

    def _base_texture_path(self, part_id: str, channel: str) -> Path | None:
        key = (part_id, channel)
        cached = self._base_texture_cache.get(key)
        if cached is not None and cached.is_file():
            return cached
        if self.game is None:
            return None
        part = next((value for value in self.item.parts if value.id == part_id), None)
        if part is None:
            return None
        image = extract_texture(self.game, part, channel)
        if image is None:
            return None
        target = self._preview_cache / f"base_{part_id}_{channel}.png"
        target.parent.mkdir(parents=True, exist_ok=True)
        image.save(target)
        self._base_texture_cache[key] = target
        return target

    def _schedule_preview(self) -> None:
        self._preview_timer.start()

    def _schedule_save(self) -> None:
        self._save_timer.start()

    def _autosave(self) -> None:
        try:
            self.project_path = save_dynamic_project(self.project, self.project_path)
        except OSError as exc:
            self.status_message.emit(f"自动保存失败：{exc}")

    def _part_changed(self) -> None:
        if self._updating:
            return
        self.project.selected_part_id = str(self.part_combo.currentData())
        self.project.selected_channel = self.channel_combo.currentText()
        self._schedule_preview()
        self._schedule_save()

    def _channel_changed(self) -> None:
        if self._updating:
            return
        self.project.selected_channel = self.channel_combo.currentText()
        self._schedule_preview()
        self._schedule_save()

    def _layer_selected(self) -> None:
        if self._updating:
            return
        row = self.layer_table.currentRow()
        if row < 0 or row >= len(self.project.layers):
            return
        layer = self.project.layers[row]
        self.project.selected_layer_id = layer.id
        self.project.selected_part_id = layer.part_id
        self.project.selected_channel = layer.channel
        self._select_combos(layer.part_id, layer.channel)
        self._sync_controls()
        self._schedule_preview()
        self._schedule_save()

    def _select_layer_by_id(self, layer_id: str) -> None:
        layer = next((value for value in self.project.layers if value.id == layer_id), None)
        if layer is None:
            return
        self.project.selected_layer_id = layer.id
        self.project.selected_part_id = layer.part_id
        self.project.selected_channel = layer.channel
        self._select_combos(layer.part_id, layer.channel)
        self._refresh_layer_table()
        self._sync_controls()
        self._schedule_preview()

    def _select_combos(self, part_id: str, channel: str) -> None:
        self._updating = True
        try:
            part_index = self.part_combo.findData(part_id)
            if part_index >= 0:
                self.part_combo.setCurrentIndex(part_index)
            channel_index = self.channel_combo.findText(channel)
            if channel_index >= 0:
                self.channel_combo.setCurrentIndex(channel_index)
        finally:
            self._updating = False

    def _property_changed(self, *_args) -> None:
        if self._updating:
            return
        layer = self._current_layer()
        if layer is None:
            return
        layer.name = self.layer_name.text().strip() or layer.name
        layer.opacity = self.opacity_spin.value()
        layer.uv_rotation = self.rotation_spin.value()
        layer.scale_x = self.scale_x_spin.value()
        layer.scale_y = self.scale_y_spin.value()
        layer.uv_x = self.uv_x_spin.value()
        layer.uv_y = self.uv_y_spin.value()
        layer.uv_width = self.uv_w_spin.value()
        layer.uv_height = self.uv_h_spin.value()
        layer.crop_left = self.crop_left.value()
        layer.crop_top = self.crop_top.value()
        layer.crop_right = self.crop_right.value()
        layer.crop_bottom = self.crop_bottom.value()
        layer.fps = self.fps_spin.value()
        layer.loop = self.loop_layer.isChecked()
        layer.text = self.text_edit.text()
        layer.font_path = self.font_path_edit.text()
        layer.font_size = self.font_size_spin.value()
        layer.text_color = self.text_color.text() or "#FFFFFF"
        layer.stroke_color = self.stroke_color.text() or "#00000000"
        layer.stroke_width = self.stroke_width_spin.value()
        layer.wrap_width = self.wrap_width_spin.value()
        self._refresh_layer_table()
        self._sync_controls()
        self._schedule_preview()
        self._schedule_save()

    def _choose_font(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "选择字体",
            str(Path(self.font_path_edit.text()).parent)
            if self.font_path_edit.text()
            else "",
            "字体 (*.ttf *.otf *.ttc)",
        )
        if path:
            copied = copy_media(
                [Path(path)],
                project_path=self.project_path,
                prefix="fonts",
            )
            self.font_path_edit.setText(copied[0] if copied else path)
            self._property_changed()

    def _uv_rect_changed(
        self,
        layer_id: str,
        x: float,
        y: float,
        width: float,
        height: float,
    ) -> None:
        layer = next((value for value in self.project.layers if value.id == layer_id), None)
        if layer is None or layer.locked:
            return
        layer.uv_x = x
        layer.uv_y = y
        layer.uv_width = width
        layer.uv_height = height
        self.project.selected_layer_id = layer.id
        self._sync_controls()
        self._schedule_preview()
        self._schedule_save()

    def _overlay_moved(self, x: float, y: float, z: float) -> None:
        layer = self._current_layer()
        if layer is None:
            return
        preset = self.preview_view._preset
        layer.plane_part_id = str(
            preset.anchor_part_id
            or self.part_combo.currentData()
            or layer.part_id
        )
        layer.use_absolute_anchor = True
        layer.plane_x = float(preset.anchor_local_x)
        layer.plane_y = float(preset.anchor_local_y)
        layer.plane_z = float(preset.anchor_local_z)
        bounds = self.preview_view.selected_part_bounds()
        if bounds is not None:
            low, high = bounds
            span_x = max(1e-6, float(high[0] - low[0]))
            span_y = max(1e-6, float(high[1] - low[1]))
            span_z = max(1e-6, float(high[2] - low[2]))
            norm_x = (layer.plane_x - float(low[0])) / span_x
            norm_y = (layer.plane_y - float(low[1])) / span_y
            norm_z = (layer.plane_z - float(low[2])) / span_z
            u = norm_x if span_x >= span_z else norm_z
            v = 1.0 - norm_y
            layer.uv_x = min(1.0 - layer.uv_width, max(0.0, u - layer.uv_width * 0.5))
            layer.uv_y = min(1.0 - layer.uv_height, max(0.0, v - layer.uv_height * 0.5))
        self._refresh_uv_canvas()
        self._sync_controls()
        self._schedule_save()

    def _overlay_rotated(self, x: float, y: float, z: float) -> None:
        layer = self._current_layer()
        if layer is None:
            return
        layer.plane_rx = float(x)
        layer.plane_ry = float(y)
        layer.plane_rz = float(z)
        layer.uv_rotation = float(z)
        self._refresh_uv_canvas()
        self._schedule_save()

    def _overlay_scaled(self, value: float) -> None:
        layer = self._current_layer()
        if layer is None:
            return
        value = max(0.01, float(value))
        previous = max(0.01, float(layer.plane_scale))
        ratio = value / previous
        layer.uv_width = min(2.0, max(0.001, layer.uv_width * ratio))
        layer.uv_height = min(2.0, max(0.001, layer.uv_height * ratio))
        layer.plane_scale = value
        self._refresh_uv_canvas()
        self._sync_controls()
        self._schedule_save()

    def _add_image(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "选择贴花图片",
            "",
            "图片 (*.png *.jpg *.jpeg *.tga *.webp *.bmp)",
        )
        if not path:
            return
        try:
            import_image_layer(
                self.project,
                self.project_path,
                Path(path),
                channel=self.channel_combo.currentText(),
                part_id=str(self.part_combo.currentData()),
            )
            self._after_layer_added()
        except (OSError, ValueError) as exc:
            QMessageBox.warning(self, "添加失败", str(exc))

    def _add_sequence(self) -> None:
        paths, _ = QFileDialog.getOpenFileNames(
            self,
            "选择图片序列",
            "",
            "图片 (*.png *.jpg *.jpeg *.tga *.webp *.bmp)",
        )
        if not paths:
            return
        try:
            import_sequence_layer(
                self.project,
                self.project_path,
                [Path(path) for path in paths],
                channel=self.channel_combo.currentText(),
                part_id=str(self.part_combo.currentData()),
                fps=10.0,
            )
            self._after_layer_added()
        except (OSError, ValueError) as exc:
            QMessageBox.warning(self, "添加失败", str(exc))

    def _add_video(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "选择 MP4 视频",
            "",
            "视频 (*.mp4 *.mov *.webm);;所有文件 (*.*)",
        )
        if not path:
            return
        try:
            import_video_layer(
                self.project,
                self.project_path,
                Path(path),
                channel=self.channel_combo.currentText(),
                part_id=str(self.part_combo.currentData()),
                fps=15.0,
            )
            self._after_layer_added()
        except Exception as exc:
            QMessageBox.warning(self, "视频导入失败", str(exc))

    def _add_text(self) -> None:
        add_text_layer(
            self.project,
            channel=self.channel_combo.currentText(),
            part_id=str(self.part_combo.currentData()),
        )
        self._after_layer_added()

    def _after_layer_added(self) -> None:
        self._refresh_layer_table()
        self._sync_controls()
        self._schedule_preview()
        self._schedule_save()
        self._play_timer.singleShot(180, self._focus_selected_layer)

    def _transform_tool_changed(self) -> None:
        tool = str(self.transform_tool_combo.currentData() or "move")
        self.preview_view.set_transform_tool(tool)

    def _focus_selected_layer(self) -> None:
        self.preview_tabs.setCurrentIndex(0)
        self.preview_view.select_overlay()
        self.preview_view.focus_overlay()

    def _duplicate_layer(self) -> None:
        layer = self._current_layer()
        if layer is None:
            return
        duplicate_layer(self.project, layer)
        self._after_layer_added()

    def _delete_layer(self) -> None:
        layer = self._current_layer()
        if layer is None:
            return
        self.project.layers.remove(layer)
        self.project.selected_layer_id = (
            self.project.layers[-1].id if self.project.layers else ""
        )
        self._after_layer_added()

    def _toggle_play(self, playing: bool) -> None:
        self.play_button.setText("暂停" if playing else "播放")
        if playing:
            self._play_timer.start()
        else:
            self._play_timer.stop()

    def _play_tick(self) -> None:
        fps, frame_count = effective_timeline(self.project)
        duration = frame_count / max(1.0, fps)
        self._play_time += 0.05
        if self._play_time >= duration:
            if self.project.loop:
                self._play_time = 0.0
            else:
                self._play_time = duration
                self.play_button.setChecked(False)
        self.timeline_label.setText(f"{self._play_time:.2f}s")
        self._preview_timer.start()

    def _loop_changed(self, checked: bool) -> None:
        if not self._updating:
            self.project.loop = bool(checked)
            self._schedule_save()

    def _name_changed(self) -> None:
        self.project.name = self.project_name.text().strip() or "未命名动态视觉"
        self._schedule_save()

    def _choose_profile(self) -> None:
        path = QFileDialog.getExistingDirectory(
            self,
            "选择包含 BepInEx 的测试档",
            str(self.profile_root or self.app_root),
        )
        if not path:
            return
        self.profile_root = Path(path)
        self.profile_label.setText(path)

    def _open_project(self) -> None:
        path, _ = QFileDialog.getOpenFileName(
            self,
            "打开动态视觉工程",
            str(self.project_path.parent),
            "动态视觉工程 (*.gtfdyn)",
        )
        if not path:
            return
        try:
            self.project = load_dynamic_project(Path(path))
            self.project_path = Path(path)
            self.project_name.setText(self.project.name)
            self.loop_check.setChecked(self.project.loop)
            self._populate_parts()
            self._refresh_layer_table()
            self._sync_controls()
            self._schedule_preview()
            self.status_message.emit(f"已打开：{path}")
        except (OSError, ValueError) as exc:
            QMessageBox.warning(self, "打开失败", str(exc))

    def _save_project(self) -> None:
        path, _ = QFileDialog.getSaveFileName(
            self,
            "保存动态视觉工程",
            str(self.project_path),
            "动态视觉工程 (*.gtfdyn)",
        )
        if not path:
            return
        try:
            self.project_path = save_dynamic_project(self.project, Path(path))
            self.status_message.emit(f"已保存：{self.project_path}")
        except OSError as exc:
            QMessageBox.warning(self, "保存失败", str(exc))

    def _require_game(self) -> GameInstall | None:
        if self.game is None:
            QMessageBox.warning(self, "未检测到 GTFO", "请先在枪皮工坊检测游戏目录。")
            return None
        return self.game

    def _build(self) -> DynamicBuildResult | None:
        game = self._require_game()
        if game is None:
            return None
        output = ensure_app_dirs()["cache"] / "dynamic_builds" / "active_pack"
        try:
            self._autosave()
            result = build_dynamic_project(
                self.project,
                self.project_path,
                game,
                output,
            )
            self._last_build = result
            self.status_message.emit(
                f"构建完成：{result.frame_count} 帧 @ {result.timeline_fps:g} FPS"
            )
            return result
        except Exception as exc:
            QMessageBox.warning(self, "构建失败", str(exc))
            return None

    def _export_pack(self) -> None:
        result = self._build()
        if result is None:
            return
        target, _ = QFileDialog.getSaveFileName(
            self,
            "导出动态视觉包",
            str(self.project_path.with_suffix(".gtfdynpack.zip")),
            "动态视觉包 (*.gtfdynpack.zip)",
        )
        if target:
            try:
                target_path = Path(target)
                target_path.parent.mkdir(parents=True, exist_ok=True)
                import shutil

                shutil.copy2(result.export_path, target_path)
                self.status_message.emit(f"已导出：{target_path}")
            except OSError as exc:
                QMessageBox.warning(self, "导出失败", str(exc))

    def _install_pack(self) -> None:
        if self.profile_root is None:
            self._choose_profile()
            if self.profile_root is None:
                return
        result = self._build()
        if result is None:
            return
        try:
            manifest = install_dynamic_build(self.profile_root, result)
            self.status_message.emit(f"已安装：{manifest}")
        except OSError as exc:
            QMessageBox.warning(self, "安装失败", str(exc))

    def _restore_previous(self) -> None:
        if self.profile_root is None:
            return
        try:
            uninstall_dynamic_build(self.profile_root)
            self.status_message.emit("已恢复上一版动态视觉插件和动态包")
        except OSError as exc:
            QMessageBox.warning(self, "恢复失败", str(exc))

    def save_splitter_sizes(self) -> list[int]:
        return self.splitter.sizes()

    def restore_splitter_sizes(self, sizes: list[int]) -> None:
        if len(sizes) == self.splitter.count() and all(value > 0 for value in sizes):
            self.splitter.setSizes(sizes)
