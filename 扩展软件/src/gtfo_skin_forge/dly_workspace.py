from __future__ import annotations

import hashlib
import shutil
from pathlib import Path

from PySide6.QtCore import QSize, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QIcon, QPainter, QPen, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView,
    QColorDialog,
    QComboBox,
    QDoubleSpinBox,
    QFileDialog,
    QFrame,
    QGridLayout,
    QGroupBox,
    QHeaderView,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QListWidget,
    QListWidgetItem,
    QMessageBox,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    QSpinBox,
    QSplitter,
    QTableWidget,
    QTableWidgetItem,
    QToolButton,
    QVBoxLayout,
    QWidget,
)

from .catalog import load_catalog
from .config import app_data_dir, ensure_app_dirs
from .dly import (
    DLY_HEL_PARTS,
    DLY_PLUGIN_FOLDER,
    DlyDisplayPreset,
    default_preset_path,
    find_gtfo_profile_root,
    load_dly_preset,
    part_by_id,
    plugin_config_path,
    save_dly_preset,
    write_plugin_config,
)
from .game_locator import is_gtfo_running
from .models import CatalogItem, GameInstall


class DlyWorkspace(QWidget):
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
        self.catalog = load_catalog()
        self.item: CatalogItem | None = self.catalog.get("auto_pistol")
        self.preset_path = default_preset_path(ensure_app_dirs()["root"])
        self.preset = load_dly_preset(self.preset_path)
        self.profile_root = find_gtfo_profile_root(start=app_root)
        self.preview_view = None
        self._updating_controls = False
        self._display_object_selected = True
        self.screen_object_buttons: dict[str, QToolButton] = {}
        self.screen_object_cells: dict[str, QWidget] = {}
        self._preview_timer = QTimer(self)
        self._preview_timer.setSingleShot(True)
        self._preview_timer.setInterval(35)
        self._preview_timer.timeout.connect(self._refresh_preview)
        self._autosave_timer = QTimer(self)
        self._autosave_timer.setSingleShot(True)
        self._autosave_timer.setInterval(450)
        self._autosave_timer.timeout.connect(self._autosave)
        self._build_ui()
        self._sync_controls_from_preset()
        self._refresh_profile_status()

    def _build_ui(self) -> None:
        outer = QVBoxLayout(self)
        outer.setContentsMargins(12, 10, 12, 8)
        outer.setSpacing(8)

        title_row = QHBoxLayout()
        title_box = QVBoxLayout()
        title = QLabel("DLY 弹夹显示")
        title.setStyleSheet(
            "font-size: 20px; font-weight: 700; color: #9ff8ff;"
        )
        subtitle = QLabel(
            "HEL 自动手枪 · 可视化部件附着与实时弹夹屏幕预览"
        )
        subtitle.setStyleSheet("color: #82969b;")
        title_box.addWidget(title)
        title_box.addWidget(subtitle)
        title_row.addLayout(title_box, 1)
        self.preview_value = QSpinBox()
        self.preview_value.setRange(0, 999)
        self.preview_value.setValue(self.preset.preview_clip)
        self.preview_value.setPrefix("预览弹夹  ")
        self.preview_value.valueChanged.connect(self._controls_changed)
        title_row.addWidget(self.preview_value)
        outer.addLayout(title_row)

        self.splitter = QSplitter(Qt.Orientation.Horizontal)
        self.splitter.setChildrenCollapsible(False)
        outer.addWidget(self.splitter, 1)
        self._build_parts_panel()
        self._build_preview_panel()
        self._build_controls_panel()
        self.splitter.setStretchFactor(0, 0)
        self.splitter.setStretchFactor(1, 1)
        self.splitter.setStretchFactor(2, 0)
        self.splitter.setSizes([245, 760, 375])

        footer = QFrame()
        footer.setStyleSheet(
            "QFrame { border-top: 1px solid #303a3f; background: #151a1d; }"
        )
        footer_layout = QHBoxLayout(footer)
        footer_layout.setContentsMargins(8, 7, 8, 7)
        self.profile_label = QLineEdit()
        self.profile_label.setReadOnly(True)
        self.profile_label.setPlaceholderText("未找到 BepInEx 测试档")
        self.profile_label.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Fixed,
        )
        choose_profile = QPushButton("选择测试档")
        choose_profile.clicked.connect(self._choose_profile)
        self.apply_button = QPushButton("应用到当前测试档")
        self.apply_button.setObjectName("Primary")
        self.apply_button.clicked.connect(self._apply_to_profile)
        self.status_label = QLabel("编辑位置后应用；重新装备 HEL 生效")
        self.status_label.setStyleSheet("color: #8fa1a6; border: none;")
        self.status_message.connect(self.status_label.setText)
        footer_layout.addWidget(QLabel("配置目标"))
        footer_layout.addWidget(self.profile_label, 1)
        footer_layout.addWidget(choose_profile)
        footer_layout.addWidget(self.status_label)
        footer_layout.addWidget(self.apply_button)
        outer.addWidget(footer)

    def _build_parts_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(255)
        panel.setSizePolicy(
            QSizePolicy.Policy.Preferred,
            QSizePolicy.Policy.Expanding,
        )
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(0, 0, 8, 0)
        heading = QLabel("HEL 可附着部件")
        heading.setStyleSheet(
            "font-size: 15px; font-weight: 700; color: #d8ff67;"
        )
        layout.addWidget(heading)
        hint = QLabel(
            "点击枪模或列表选择部件；拖动显示框可沿表面移动，"
            "拖动红/绿/蓝轴可单轴移动。"
        )
        hint.setWordWrap(True)
        hint.setStyleSheet("color: #7f9297;")
        layout.addWidget(hint)
        self.display_object_button = QPushButton(
            "●  子弹显示屏幕\n独立编辑对象"
        )
        self.display_object_button.setObjectName("DlyDisplayObject")
        self.display_object_button.setCheckable(True)
        self.display_object_button.setChecked(True)
        self.display_object_button.setMinimumHeight(44)
        self.display_object_button.setToolTip(
            "选择子弹显示屏幕；即使背板和边框完全透明也可编辑"
        )
        self.display_object_button.setStyleSheet(
            "QPushButton {"
            "text-align: left; padding: 5px 10px; color: #ffd18b;"
            "background: #2b2115; border: 1px solid #b97929;"
            "border-radius: 5px; font-weight: 600;"
            "}"
            "QPushButton:checked {"
            "background: #332617; border: 2px solid #ffad32;"
            "color: #fff1d8;"
            "}"
            "QPushButton:hover { border-color: #ffc45b; }"
        )
        self.display_object_button.clicked.connect(
            self._select_display_overlay
        )
        layout.addWidget(self.display_object_button)
        self.parts_table = QTableWidget(len(DLY_HEL_PARTS), 3)
        self.parts_table.setObjectName("DlyPartsTable")
        self.parts_table.setHorizontalHeaderLabels(["", "部件", "对象"])
        self.parts_table.verticalHeader().setVisible(False)
        self.parts_table.setShowGrid(False)
        self.parts_table.setAlternatingRowColors(True)
        self.parts_table.setSelectionBehavior(
            QAbstractItemView.SelectionBehavior.SelectRows
        )
        self.parts_table.setSelectionMode(
            QAbstractItemView.SelectionMode.SingleSelection
        )
        self.parts_table.setEditTriggers(
            QAbstractItemView.EditTrigger.NoEditTriggers
        )
        self.parts_table.horizontalHeader().setSectionResizeMode(
            0,
            QHeaderView.ResizeMode.Fixed,
        )
        self.parts_table.horizontalHeader().setSectionResizeMode(
            1,
            QHeaderView.ResizeMode.Stretch,
        )
        self.parts_table.horizontalHeader().setSectionResizeMode(
            2,
            QHeaderView.ResizeMode.Fixed,
        )
        self.parts_table.setColumnWidth(0, 36)
        self.parts_table.setColumnWidth(2, 62)
        self.parts_table.setWordWrap(True)
        for row, part in enumerate(DLY_HEL_PARTS):
            suffix = " · 可选" if part.optional else ""
            item = QTableWidgetItem(
                f"{part.label}{suffix}\n{part.anchor_name}"
            )
            item.setData(Qt.ItemDataRole.UserRole, part.id)
            self.parts_table.setItem(row, 1, item)
            button = QToolButton()
            button.setCheckable(True)
            button.setText("")
            button.setToolButtonStyle(
                Qt.ToolButtonStyle.ToolButtonIconOnly
            )
            button.setIconSize(QSize(17, 17))
            button.setFixedSize(26, 30)
            button.setToolTip(f"隐藏 {part.label}")
            button.blockSignals(True)
            button.setChecked(part.id not in self.preset.hidden_parts)
            button.blockSignals(False)
            self._update_part_visibility_button(button, button.isChecked())
            button.toggled.connect(
                lambda checked, part_id=part.id:
                self._part_visibility_changed(part_id, checked)
            )
            self.parts_table.setCellWidget(row, 0, button)

            screen_cell = QWidget()
            screen_layout = QHBoxLayout(screen_cell)
            screen_layout.setContentsMargins(2, 0, 2, 0)
            screen_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
            screen_button = QToolButton()
            screen_button.setObjectName(
                f"DlyScreenObject_{part.id}"
            )
            screen_button.setText("屏幕")
            screen_button.setToolTip(
                "单独选中子弹显示屏幕；G 后按 X/Y/Z 只移动屏幕"
            )
            screen_button.setCheckable(True)
            screen_button.setFixedSize(54, 28)
            screen_button.setToolButtonStyle(
                Qt.ToolButtonStyle.ToolButtonTextOnly
            )
            screen_button.setStyleSheet(
                "QToolButton {"
                "padding: 2px 5px; color: #ffd18b;"
                "background: #221b12; border: 1px solid #8c632e;"
                "border-radius: 4px; font-weight: 600;"
                "}"
                "QToolButton:checked {"
                "background: #3b2915; color: #fff3dc;"
                "border: 2px solid #ffad32;"
                "}"
                "QToolButton:hover { border-color: #ffc45b; }"
            )
            screen_button.clicked.connect(
                self._select_display_overlay
            )
            screen_layout.addWidget(screen_button)
            screen_button.setVisible(
                part.id == self.preset.anchor_part_id
            )
            screen_button.setChecked(
                part.id == self.preset.anchor_part_id
            )
            self.screen_object_buttons[part.id] = screen_button
            self.screen_object_cells[part.id] = screen_cell
            self.parts_table.setCellWidget(row, 2, screen_cell)
            self.parts_table.setRowHeight(row, 54)
            item.setForeground(
                QColor(
                    "#59666a"
                    if part.id in self.preset.hidden_parts
                    else "#f1f4f3"
                )
            )
        self.parts_table.currentCellChanged.connect(
            lambda row, _column, _previous_row, _previous_column:
            self._part_row_changed(row)
        )
        layout.addWidget(self.parts_table, 1)
        reset_receiver = QPushButton("返回机匣默认位置")
        reset_receiver.clicked.connect(self._reset_to_receiver)
        layout.addWidget(reset_receiver)
        self.splitter.addWidget(panel)

    def _build_preview_panel(self) -> None:
        panel = QWidget()
        panel.setMinimumWidth(420)
        panel.setSizePolicy(
            QSizePolicy.Policy.Ignored,
            QSizePolicy.Policy.Expanding,
        )
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(5, 0, 5, 0)
        view_row = QHBoxLayout()
        view_row.addWidget(QLabel("固定视角"))
        self.fixed_view_combo = QComboBox()
        for label, view_id in (
            ("正面", "front"),
            ("背面", "back"),
            ("左侧", "left"),
            ("右侧", "right"),
            ("顶部", "top"),
            ("底部", "bottom"),
            ("等轴测", "iso"),
        ):
            self.fixed_view_combo.addItem(label, view_id)
        self.fixed_view_combo.setCurrentIndex(6)
        self.fixed_view_combo.currentIndexChanged.connect(
            self._fixed_view_changed
        )
        apply_view = QPushButton("跳到固定面")
        apply_view.clicked.connect(self._apply_fixed_view)
        self.surface_snap_button = QPushButton("吸附到对应面")
        self.surface_snap_button.setCheckable(True)
        self.surface_snap_button.setChecked(
            self.preset.surface_snap_enabled
        )
        self.surface_snap_button.setToolTip(
            "点击枪模时将屏幕放到命中点，并朝向命中的三角面"
        )
        self.surface_snap_button.clicked.connect(
            self._surface_snap_changed
        )
        view_row.addWidget(self.fixed_view_combo)
        view_row.addWidget(apply_view)
        view_row.addWidget(self.surface_snap_button)
        view_hint = QLabel(
            "Alt+右键拖动后吸附最近面 · 1/3/7 视角 · 5 透视/正交"
        )
        view_hint.setStyleSheet("color: #7f9297;")
        view_row.addWidget(view_hint, 1)
        layout.addLayout(view_row)
        tool_row = QHBoxLayout()
        self.transform_tool_buttons: dict[str, QPushButton] = {}
        for tool, text, color in (
            ("move", "移动  G", "#ff625f"),
            ("rotate", "旋转  R", "#74ee69"),
            ("scale", "缩放  S", "#5aa9ff"),
        ):
            button = QPushButton(text)
            button.setCheckable(True)
            button.setMinimumWidth(86)
            button.clicked.connect(
                lambda _checked=False, selected=tool:
                self._transform_tool_clicked(selected)
            )
            button.setStyleSheet(
                "QPushButton {"
                "padding: 5px 11px; border-radius: 4px;"
                "background: #171e21; border: 1px solid #3d4a4f;"
                f"color: {color}; font-weight: 600;"
                "}"
                "QPushButton:checked {"
                f"background: #252f33; border: 2px solid {color};"
                "color: #ffffff;"
                "}"
            )
            self.transform_tool_buttons[tool] = button
            tool_row.addWidget(button)
        focus_button = QPushButton("聚焦显示框")
        focus_button.clicked.connect(self._focus_overlay)
        tool_row.addWidget(focus_button)
        self.transform_state_label = QLabel(
            "G/R/S 模态变换 · 左键/Enter 确认 · 右键/Esc 取消"
        )
        self.transform_state_label.setStyleSheet("color: #8fa1a6;")
        tool_row.addWidget(self.transform_state_label, 1)
        layout.addLayout(tool_row)
        self.preview_host = QVBoxLayout()
        self.preview_message = QLabel("正在准备 HEL 3D 预览…")
        self.preview_message.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview_message.setStyleSheet(
            "color: #8ca0a5; background: #0b0e10;"
            " border: 1px solid #303a3f; padding: 20px;"
        )
        self.preview_host.addWidget(self.preview_message, 1)
        layout.addLayout(self.preview_host, 1)
        self.splitter.addWidget(panel)

    def _fixed_view_changed(self, _index: int) -> None:
        if self.preview_view is not None:
            self.preview_view.set_fixed_view(
                str(self.fixed_view_combo.currentData())
            )

    def _apply_fixed_view(self) -> None:
        if self.preview_view is not None:
            self.preview_view.apply_fixed_view(
                str(self.fixed_view_combo.currentData())
            )

    def _select_display_overlay(self) -> None:
        self._display_object_selected = True
        self._sync_display_object_selection_ui()
        self.parts_table.clearSelection()
        if self.preview_view is not None:
            self.preview_view.select_overlay()
        else:
            self.status_message.emit(
                "已选中子弹显示屏幕；3D 预览加载后可直接编辑"
            )

    def _surface_snap_changed(self, checked: bool) -> None:
        self.preset.surface_snap_enabled = bool(checked)
        if self.preview_view is not None:
            self.preview_view.set_surface_snap_enabled(bool(checked))
        self.status_message.emit(
            "表面吸附已开启：点击枪模的实际命中点定位"
            if checked
            else "表面吸附已关闭：使用部件顶面中央 + 偏移"
        )
        self._autosave_timer.start()

    def _transform_tool_clicked(self, tool: str) -> None:
        self.preset.last_transform_tool = tool
        for name, button in self.transform_tool_buttons.items():
            button.blockSignals(True)
            button.setChecked(name == tool)
            button.blockSignals(False)
        if self.preview_view is not None:
            self.preview_view.select_overlay()
            self.preview_view.set_transform_tool(tool)
        self._autosave_timer.start()

    def _focus_overlay(self) -> None:
        self._display_object_selected = True
        self._sync_display_object_selection_ui()
        self.parts_table.clearSelection()
        if self.preview_view is not None:
            self.preview_view.select_overlay()
            self.preview_view.focus_overlay()
        else:
            self.status_message.emit("3D 预览尚未加载，暂时无法聚焦显示框")

    def _build_controls_panel(self) -> None:
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setMinimumWidth(315)
        content = QWidget()
        layout = QVBoxLayout(content)
        layout.setContentsMargins(0, 0, 7, 0)
        layout.setSpacing(8)

        position_group = QGroupBox("位置偏移")
        position_grid = QGridLayout(position_group)
        self.offset_spins = self._vector_row(
            position_grid,
            "X 左右",
            "Y 上下",
            "Z 前后",
            suffix=" mm",
            minimum=-200.0,
            maximum=200.0,
            step=1.0,
        )
        for spin in self.offset_spins.values():
            spin.valueChanged.disconnect(self._controls_changed)
            spin.valueChanged.connect(self._offset_control_changed)
        layout.addWidget(position_group)

        rotation_group = QGroupBox("贴面旋转")
        rotation_grid = QGridLayout(rotation_group)
        self.rotation_spins = self._vector_row(
            rotation_grid,
            "X",
            "Y",
            "Z",
            suffix="°",
            minimum=-180.0,
            maximum=180.0,
            step=1.0,
        )
        self.face_player_check = QPushButton("始终面向玩家：开")
        self.face_player_check.setCheckable(True)
        self.face_player_check.clicked.connect(self._controls_changed)
        rotation_grid.addWidget(self.face_player_check, 2, 0, 1, 3)
        layout.addWidget(rotation_group)

        size_group = QGroupBox("屏幕尺寸")
        size_grid = QGridLayout(size_group)
        self.width_spin = self._spin(
            size_grid,
            0,
            "宽度",
            " mm",
            5.0,
            100.0,
            30.0,
            0.5,
        )
        self.height_spin = self._spin(
            size_grid,
            1,
            "高度",
            " mm",
            3.0,
            50.0,
            12.5,
            0.5,
        )
        self.scale_spin = self._spin(
            size_grid,
            2,
            "整体缩放",
            "×",
            0.2,
            3.0,
            1.0,
            0.05,
        )
        self.clearance_spin = self._spin(
            size_grid,
            3,
            "表面间隙",
            " mm",
            0.0,
            20.0,
            1.5,
            0.1,
        )
        self.digit_scale_spin = self._spin(
            size_grid,
            4,
            "数字大小",
            "×",
            0.25,
            3.0,
            1.0,
            0.05,
        )
        self.panel_opacity_spin = self._spin(
            size_grid,
            5,
            "背板不透明度",
            "%",
            0.0,
            100.0,
            95.0,
            1.0,
        )
        self.border_opacity_spin = self._spin(
            size_grid,
            6,
            "边框不透明度",
            "%",
            0.0,
            100.0,
            100.0,
            1.0,
        )
        layout.addWidget(size_group)

        color_group = QGroupBox("颜色")
        color_grid = QGridLayout(color_group)
        self.digit_color_button = self._color_button(color_grid, 0, "数字颜色")
        self.panel_color_button = self._color_button(color_grid, 1, "背板颜色")
        self.border_color_button = self._color_button(color_grid, 2, "边框颜色")
        layout.addWidget(color_group)

        actions = QGroupBox("预设与应用")
        action_layout = QVBoxLayout(actions)
        row_one = QHBoxLayout()
        reset_button = QPushButton("重置当前部件")
        reset_button.clicked.connect(self._reset_current_part)
        save_button = QPushButton("保存预设")
        save_button.clicked.connect(self._save_preset)
        reload_button = QPushButton("重新载入")
        reload_button.clicked.connect(self._reload_preset)
        row_one.addWidget(reset_button)
        row_one.addWidget(save_button)
        row_one.addWidget(reload_button)
        action_layout.addLayout(row_one)
        save_as_button = QPushButton("预设另存为…")
        save_as_button.clicked.connect(self._save_preset_as)
        action_layout.addWidget(save_as_button)
        self.plugin_status = QLabel()
        self.plugin_status.setWordWrap(True)
        self.plugin_status.setStyleSheet("color: #82969b;")
        action_layout.addWidget(self.plugin_status)
        layout.addWidget(actions)
        layout.addStretch(1)
        scroll.setWidget(content)
        self.controls_scroll = scroll
        self.splitter.addWidget(scroll)

    def _vector_row(
        self,
        grid: QGridLayout,
        x_label: str,
        y_label: str,
        z_label: str,
        *,
        suffix: str,
        minimum: float,
        maximum: float,
        step: float,
    ) -> dict[str, QDoubleSpinBox]:
        result: dict[str, QDoubleSpinBox] = {}
        for column, (axis, label) in enumerate(
            (("x", x_label), ("y", y_label), ("z", z_label))
        ):
            caption = QLabel(label)
            spin = QDoubleSpinBox()
            spin.setRange(minimum, maximum)
            spin.setDecimals(1)
            spin.setSingleStep(step)
            spin.setSuffix(suffix)
            spin.setMinimumWidth(86)
            spin.valueChanged.connect(self._controls_changed)
            grid.addWidget(caption, 0, column)
            grid.addWidget(spin, 1, column)
            result[axis] = spin
        return result

    def _spin(
        self,
        grid: QGridLayout,
        row: int,
        label: str,
        suffix: str,
        minimum: float,
        maximum: float,
        value: float,
        step: float,
    ) -> QDoubleSpinBox:
        caption = QLabel(label)
        spin = QDoubleSpinBox()
        spin.setRange(minimum, maximum)
        spin.setDecimals(2)
        spin.setSingleStep(step)
        spin.setSuffix(suffix)
        spin.setValue(value)
        spin.valueChanged.connect(self._controls_changed)
        grid.addWidget(caption, row, 0)
        grid.addWidget(spin, row, 1)
        return spin

    def _color_button(
        self,
        grid: QGridLayout,
        row: int,
        label: str,
    ) -> QPushButton:
        button = QPushButton("#000000")
        button.clicked.connect(
            lambda _checked=False, target=button: self._choose_color(target)
        )
        grid.addWidget(QLabel(label), row, 0)
        grid.addWidget(button, row, 1)
        return button

    def set_game(self, game: GameInstall | None) -> None:
        changed = (
            game is None
            or self.game is None
            or game.root != self.game.root
        )
        self.game = game
        if changed and self.preview_view is not None:
            self._load_preview()

    def activate(self) -> None:
        self.ensure_preview()

    def ensure_preview(self) -> None:
        if self.preview_view is not None:
            return
        if self.game is None or self.item is None:
            self.preview_message.setText(
                "未检测到 GTFO，DLY 设置仍可保存，但无法显示 3D 预览。"
            )
            return
        try:
            from .dly_viewer import DlyPreviewView

            self.preview_view = DlyPreviewView(self)
            self.preview_view.set_fixed_view(
                str(self.fixed_view_combo.currentData())
            )
            self.preview_view.part_picked.connect(
                self._on_view_part_picked
            )
            self.preview_view.overlay_moved.connect(
                self._on_overlay_moved
            )
            self.preview_view.overlay_rotated.connect(
                self._on_overlay_rotated
            )
            self.preview_view.overlay_scaled.connect(
                self._on_overlay_scaled
            )
            self.preview_view.transform_status.connect(
                self._on_transform_status
            )
            self.preview_view.fixed_view_changed.connect(
                self._on_fixed_view_changed
            )
            self.preview_view.overlay_selected.connect(
                self._on_overlay_selected
            )
            self.preview_host.removeWidget(self.preview_message)
            self.preview_message.hide()
            self.preview_host.addWidget(self.preview_view, 1)
            self._load_preview()
            self._apply_part_visibility()
        except Exception as exc:
            self.preview_message.setText(
                f"3D 预览不可用：{type(exc).__name__}: {exc}"
            )
            self.preview_message.setStyleSheet(
                "color: #ff9b93; background: #0b0e10;"
                " border: 1px solid #633b3b; padding: 20px;"
            )

    def _load_preview(self) -> None:
        if (
            self.preview_view is None
            or self.game is None
            or self.item is None
        ):
            return
        try:
            self.preview_view.load_hel(self.game, self.item, self.preset)
            self._sync_controls_from_preset()
            self._refresh_preview_message()
        except Exception as exc:
            self.status_message.emit(
                f"3D 预览加载失败：{type(exc).__name__}: {exc}"
            )

    def _refresh_preview_message(self) -> None:
        part = part_by_id(self.preset.anchor_part_id)
        face = "始终面向玩家" if self.preset.face_player else "固定贴面朝向"
        self.status_message.emit(
            f"已选择 {part.label} · {part.anchor_name} · {face}"
        )

    def _part_row_changed(self, row: int) -> None:
        if row < 0:
            return
        self._display_object_selected = False
        self._sync_display_object_selection_ui()
        item = self.parts_table.item(row, 1)
        if item is None:
            return
        part_id = str(item.data(Qt.ItemDataRole.UserRole))
        self.preset.select_part(part_id, reset_offsets=True)
        self._sync_controls_from_preset()
        if self.preview_view is not None:
            self.preview_view.apply_preset(self.preset)
        self._queue_updates()

    def _on_view_part_picked(self, part_id: str) -> None:
        self._display_object_selected = False
        self._sync_display_object_selection_ui()
        self._select_table_part(part_id)
        self._sync_controls_from_preset()
        self._autosave_timer.start()

    def _part_visibility_changed(self, part_id: str, visible: bool) -> None:
        hidden = set(self.preset.hidden_parts)
        if visible:
            hidden.discard(part_id)
        else:
            hidden.add(part_id)
        self.preset.hidden_parts = [
            part.id for part in DLY_HEL_PARTS if part.id in hidden
        ]
        row = self._part_row(part_id)
        if row >= 0:
            button = self.parts_table.cellWidget(row, 0)
            if isinstance(button, QToolButton):
                self._update_part_visibility_button(button, visible)
            item = self.parts_table.item(row, 1)
            if item is not None:
                item.setForeground(
                    QColor("#f1f4f3" if visible else "#59666a")
                )
        if self.preview_view is not None:
            self.preview_view.set_part_visibility(part_id, visible)
        self._autosave_timer.start()
        self.status_message.emit(
            f"{'显示' if visible else '隐藏'}部件："
            f"{part_by_id(part_id).label}"
        )

    def _apply_part_visibility(self) -> None:
        if self.preview_view is not None:
            self.preview_view.set_hidden_parts(
                set(self.preset.hidden_parts)
            )

    def _select_table_part(self, part_id: str) -> None:
        row = self._part_row(part_id)
        if row >= 0:
            self.parts_table.blockSignals(True)
            self.parts_table.selectRow(row)
            self.parts_table.blockSignals(False)
            return

    def _part_row(self, part_id: str) -> int:
        for row in range(self.parts_table.rowCount()):
            item = self.parts_table.item(row, 1)
            if (
                item is not None
                and str(item.data(Qt.ItemDataRole.UserRole)) == part_id
            ):
                return row
        return -1

    def _sync_part_visibility_buttons(self) -> None:
        hidden = set(self.preset.hidden_parts)
        for row, part in enumerate(DLY_HEL_PARTS):
            button = self.parts_table.cellWidget(row, 0)
            if isinstance(button, QToolButton):
                button.blockSignals(True)
                button.setChecked(part.id not in hidden)
                button.blockSignals(False)
                self._update_part_visibility_button(
                    button,
                    part.id not in hidden,
                )
            item = self.parts_table.item(row, 1)
            if item is not None:
                item.setForeground(
                    QColor(
                        "#59666a"
                        if part.id in hidden
                        else "#f1f4f3"
                    )
                )

    @staticmethod
    def _update_part_visibility_button(
        button: QToolButton,
        visible: bool,
    ) -> None:
        button.setText("")
        button.setIcon(_visibility_icon(visible))
        button.setToolTip("隐藏部件" if visible else "显示部件")
        button.setStyleSheet(
            "QToolButton {"
            "background: transparent; border: 1px solid transparent;"
            "border-radius: 3px; padding: 2px;"
            "}"
            "QToolButton:hover { background: #344047;"
            "border-color: #70e7f3; }"
            if visible
            else "QToolButton {"
            "background: transparent; border: 1px solid transparent;"
            "border-radius: 3px; padding: 2px;"
            "}"
            "QToolButton:hover { background: #44282a;"
            "border-color: #b76569; }"
        )

    def _offset_control_changed(self, *_args) -> None:
        if self._updating_controls:
            return
        self.preset.use_absolute_anchor = False
        self._controls_changed()

    def _controls_changed(self, *_args) -> None:
        if self._updating_controls:
            return
        self.preset.preview_clip = self.preview_value.value()
        self.preset.offset_x = self.offset_spins["x"].value() / 1000.0
        self.preset.offset_y = self.offset_spins["y"].value() / 1000.0
        self.preset.offset_z = self.offset_spins["z"].value() / 1000.0
        self.preset.rotation_x = self.rotation_spins["x"].value()
        self.preset.rotation_y = self.rotation_spins["y"].value()
        self.preset.rotation_z = self.rotation_spins["z"].value()
        self.preset.panel_width = self.width_spin.value() / 1000.0
        self.preset.panel_height = self.height_spin.value() / 1000.0
        self.preset.panel_scale = self.scale_spin.value()
        self.preset.surface_clearance = self.clearance_spin.value() / 1000.0
        self.preset.text_character_size = (
            self.digit_scale_spin.value() * 0.00155
        )
        self.preset.panel_opacity = self.panel_opacity_spin.value() / 100.0
        self.preset.border_opacity = self.border_opacity_spin.value() / 100.0
        self.preset.face_player = self.face_player_check.isChecked()
        self.face_player_check.setText(
            "始终面向玩家：" + (
                "开" if self.preset.face_player else "关"
            )
        )
        for spin in self.rotation_spins.values():
            spin.setEnabled(not self.preset.face_player)
        self._update_color_button(
            self.digit_color_button,
            self.preset.text_color,
            "数字颜色",
        )
        self._update_color_button(
            self.panel_color_button,
            self.preset.panel_color,
            "背板颜色",
        )
        self._update_color_button(
            self.border_color_button,
            self.preset.border_color,
            "边框颜色",
        )
        self._queue_updates()

    def _sync_controls_from_preset(self) -> None:
        self._updating_controls = True
        try:
            self.preview_value.setValue(self.preset.preview_clip)
            self.offset_spins["x"].setValue(self.preset.offset_x * 1000.0)
            self.offset_spins["y"].setValue(self.preset.offset_y * 1000.0)
            self.offset_spins["z"].setValue(self.preset.offset_z * 1000.0)
            self.rotation_spins["x"].setValue(self.preset.rotation_x)
            self.rotation_spins["y"].setValue(self.preset.rotation_y)
            self.rotation_spins["z"].setValue(self.preset.rotation_z)
            self.width_spin.setValue(self.preset.panel_width * 1000.0)
            self.height_spin.setValue(self.preset.panel_height * 1000.0)
            self.scale_spin.setValue(self.preset.panel_scale)
            self.clearance_spin.setValue(
                self.preset.surface_clearance * 1000.0
            )
            self.digit_scale_spin.setValue(
                max(0.25, min(3.0, self.preset.text_character_size / 0.00155))
            )
            self.panel_opacity_spin.setValue(
                self.preset.panel_opacity * 100.0
            )
            self.border_opacity_spin.setValue(
                self.preset.border_opacity * 100.0
            )
            view_index = self.fixed_view_combo.findData(
                self.preset.fixed_view_preference
            )
            if view_index >= 0:
                self.fixed_view_combo.blockSignals(True)
                self.fixed_view_combo.setCurrentIndex(view_index)
                self.fixed_view_combo.blockSignals(False)
            self.surface_snap_button.blockSignals(True)
            self.surface_snap_button.setChecked(
                self.preset.surface_snap_enabled
            )
            self.surface_snap_button.blockSignals(False)
            for tool, button in self.transform_tool_buttons.items():
                button.blockSignals(True)
                button.setChecked(
                    tool == self.preset.last_transform_tool
                )
                button.blockSignals(False)
            self.face_player_check.setChecked(self.preset.face_player)
            self.face_player_check.setText(
                "始终面向玩家：" + (
                    "开" if self.preset.face_player else "关"
                )
            )
            for spin in self.rotation_spins.values():
                spin.setEnabled(not self.preset.face_player)
            self._update_color_button(
                self.digit_color_button,
                self.preset.text_color,
                "数字颜色",
            )
            self._update_color_button(
                self.panel_color_button,
                self.preset.panel_color,
                "背板颜色",
            )
            self._update_color_button(
                self.border_color_button,
                self.preset.border_color,
                "边框颜色",
            )
            self._select_table_part(self.preset.anchor_part_id)
            self._sync_part_visibility_buttons()
            self._sync_display_object_selection_ui()
        finally:
            self._updating_controls = False

    def _choose_color(self, target: QPushButton) -> None:
        if target is self.digit_color_button:
            current = self.preset.text_color
        elif target is self.panel_color_button:
            current = self.preset.panel_color
        else:
            current = self.preset.border_color
        color = QColorDialog.getColor(QColor(current), self, "选择颜色")
        if not color.isValid():
            return
        value = _qt_color_to_plugin_text(
            color,
            include_alpha=target is self.panel_color_button,
        )
        if target is self.digit_color_button:
            self.preset.text_color = value
        elif target is self.panel_color_button:
            self.preset.panel_color = value
        else:
            self.preset.border_color = value
        self._sync_controls_from_preset()
        self._queue_updates()

    @staticmethod
    def _update_color_button(
        button: QPushButton,
        color_text: str,
        label: str,
    ) -> None:
        color = QColor(color_text)
        if not color.isValid():
            color = QColor("#000000")
        text_color = "#0d1112" if color.lightness() > 145 else "#f4f7f5"
        button.setText(f"{label}  {color_text.upper()}")
        button.setStyleSheet(
            "QPushButton {"
            f"background: {color.name(QColor.NameFormat.HexArgb)};"
            f"color: {text_color}; border: 1px solid #5f7076;"
            "border-radius: 4px; padding: 6px;"
            "}"
        )

    def _queue_updates(self) -> None:
        self._preview_timer.start()
        self._autosave_timer.start()

    def _on_overlay_moved(
        self,
        offset_x: float,
        offset_y: float,
        offset_z: float,
    ) -> None:
        self.preset.offset_x = float(offset_x)
        self.preset.offset_y = float(offset_y)
        self.preset.offset_z = float(offset_z)
        self._updating_controls = True
        try:
            self.offset_spins["x"].setValue(offset_x * 1000.0)
            self.offset_spins["y"].setValue(offset_y * 1000.0)
            self.offset_spins["z"].setValue(offset_z * 1000.0)
        finally:
            self._updating_controls = False
        self._autosave_timer.start()
        self.status_message.emit(
            "显示框位置已更新："
            f"X {offset_x * 1000:.1f} mm · "
            f"Y {offset_y * 1000:.1f} mm · "
            f"Z {offset_z * 1000:.1f} mm"
        )

    def _on_overlay_rotated(
        self,
        rotation_x: float,
        rotation_y: float,
        rotation_z: float,
    ) -> None:
        self._updating_controls = True
        try:
            self.rotation_spins["x"].setValue(rotation_x)
            self.rotation_spins["y"].setValue(rotation_y)
            self.rotation_spins["z"].setValue(rotation_z)
            self.face_player_check.setChecked(
                self.preset.face_player
            )
            self.face_player_check.setText(
                "始终面向玩家："
                + ("开" if self.preset.face_player else "关")
            )
            for spin in self.rotation_spins.values():
                spin.setEnabled(not self.preset.face_player)
        finally:
            self._updating_controls = False
        self._autosave_timer.start()

    def _on_overlay_scaled(self, scale: float) -> None:
        self._updating_controls = True
        try:
            self.scale_spin.setValue(scale)
        finally:
            self._updating_controls = False
        self.status_message.emit(f"显示框缩放：{scale:.2f}×")
        self._autosave_timer.start()

    def _on_transform_status(self, text: str) -> None:
        self._select_display_overlay_without_signal()
        self.transform_state_label.setText(text)
        self.status_message.emit(text)
        self._autosave_timer.start()

    def _on_overlay_selected(self) -> None:
        self._select_display_overlay_without_signal()
        self.status_message.emit(
            "已选中子弹显示屏幕；G 移动，R 旋转，S 缩放"
        )

    def _select_display_overlay_without_signal(self) -> None:
        self._display_object_selected = True
        self._sync_display_object_selection_ui()
        self.parts_table.clearSelection()

    def _sync_display_object_selection_ui(self) -> None:
        self.display_object_button.blockSignals(True)
        self.display_object_button.setChecked(
            self._display_object_selected
        )
        self.display_object_button.blockSignals(False)
        for part in DLY_HEL_PARTS:
            is_anchor = part.id == self.preset.anchor_part_id
            cell = self.screen_object_cells.get(part.id)
            button = self.screen_object_buttons.get(part.id)
            if cell is not None:
                cell.setVisible(True)
            if button is not None:
                button.blockSignals(True)
                button.setVisible(is_anchor)
                button.setChecked(
                    is_anchor and self._display_object_selected
                )
                button.blockSignals(False)

    def _on_fixed_view_changed(self, view_id: str) -> None:
        self.preset.fixed_view_preference = view_id
        index = self.fixed_view_combo.findData(view_id)
        if index >= 0:
            self.fixed_view_combo.blockSignals(True)
            self.fixed_view_combo.setCurrentIndex(index)
            self.fixed_view_combo.blockSignals(False)
        self._autosave_timer.start()

    def _refresh_preview(self) -> None:
        if self.preview_view is not None:
            self.preview_view.apply_preset(self.preset)
        self._refresh_preview_message()

    def _autosave(self) -> None:
        try:
            save_dly_preset(self.preset_path, self.preset)
        except OSError as exc:
            self.status_message.emit(f"DLY 自动保存失败：{exc}")

    def _save_preset(self) -> None:
        try:
            self._refresh_preview()
            if self.preview_view is not None:
                self.preview_view.sync_absolute_anchor()
                self.preset.use_absolute_anchor = True
                self.preset.use_part_normalized_anchor = False
            save_dly_preset(self.preset_path, self.preset)
            self.status_message.emit(f"DLY 预设已保存：{self.preset_path}")
        except OSError as exc:
            QMessageBox.warning(self, "保存失败", str(exc))

    def _save_preset_as(self) -> None:
        path, _ = QFileDialog.getSaveFileName(
            self,
            "保存 DLY 预设",
            str(self.preset_path),
            "DLY 预设 (*.gtfdly);;JSON (*.json)",
        )
        if not path:
            return
        target = Path(path)
        try:
            self._refresh_preview()
            save_dly_preset(target, self.preset)
            self.status_message.emit(f"DLY 预设已另存：{target}")
        except OSError as exc:
            QMessageBox.warning(self, "保存失败", str(exc))

    def _reload_preset(self) -> None:
        self.preset = load_dly_preset(self.preset_path)
        self._sync_controls_from_preset()
        if self.preview_view is not None:
            self.preview_view.apply_preset(self.preset)
        self.status_message.emit("已重新载入 DLY 预设")

    def _reset_current_part(self) -> None:
        self.preset.select_part(
            self.preset.anchor_part_id,
            reset_offsets=True,
        )
        self.preset.rotation_x = 0.0
        self.preset.rotation_y = 0.0
        self.preset.rotation_z = 0.0
        self.preset.use_absolute_anchor = False
        self._sync_controls_from_preset()
        self._refresh_preview()
        self._autosave()

    def _reset_to_receiver(self) -> None:
        self.preset.select_part("receiver", reset_offsets=True)
        self.preset.rotation_x = 0.0
        self.preset.rotation_y = 0.0
        self.preset.rotation_z = 0.0
        self.preset.use_absolute_anchor = False
        self._sync_controls_from_preset()
        self._refresh_preview()
        self._autosave()

    def _choose_profile(self) -> None:
        path = QFileDialog.getExistingDirectory(
            self,
            "选择包含 BepInEx 的 GTFO 测试档目录",
            str(self.profile_root or self.app_root),
        )
        if not path:
            return
        selected = Path(path)
        profile = find_gtfo_profile_root(selected_root=selected)
        if profile is None:
            QMessageBox.warning(
                self,
                "未找到测试档",
                "所选目录及其上级目录中没有 "
                "BepInEx\\plugins\\Yuyu-HelAmmoDisplay。",
            )
            return
        self.profile_root = profile
        self._refresh_profile_status()
        self.status_message.emit(f"配置目标已设为：{profile}")

    def _refresh_profile_status(self) -> None:
        if self.profile_root is None:
            self.profile_root = find_gtfo_profile_root(start=self.app_root)
        if self.profile_root is None:
            self.profile_label.setText("未检测到测试档")
            self.apply_button.setEnabled(False)
            self.plugin_status.setText("需要先选择包含已安装 DLY 插件的测试档。")
            return
        config_path = plugin_config_path(self.profile_root)
        plugin_dir = self.profile_root / DLY_PLUGIN_FOLDER
        self.profile_label.setText(str(self.profile_root))
        self.apply_button.setEnabled(True)
        self.plugin_status.setText(
            "插件已安装\n"
            f"插件目录：{plugin_dir}\n"
            f"配置文件：{config_path}"
        )

    def _apply_to_profile(self) -> None:
        profile = find_gtfo_profile_root(
            selected_root=self.profile_root,
            start=self.app_root,
        )
        if profile is None:
            QMessageBox.warning(
                self,
                "未找到测试档",
                "请先选择包含 BepInEx/plugins/Yuyu-HelAmmoDisplay 的测试档。",
            )
            return
        self.profile_root = profile
        try:
            self._refresh_preview()
            if self.preview_view is not None:
                self.preview_view.sync_absolute_anchor()
                self.preset.use_absolute_anchor = True
                self.preset.use_part_normalized_anchor = False
            save_dly_preset(self.preset_path, self.preset)
            self._update_bundled_plugin(profile)
            config_path = write_plugin_config(profile, self.preset)
        except OSError as exc:
            QMessageBox.warning(self, "应用失败", str(exc))
            return
        self._refresh_profile_status()
        self.status_message.emit(
            f"已应用 DLY 配置：{config_path}；重新装备 HEL 生效"
        )

    def _update_bundled_plugin(self, profile_root: Path) -> None:
        bundled_dir = Path(__file__).with_name("data") / "dly_plugin"
        bundled_dll = bundled_dir / "Yuyu.HelAmmoDisplay.dll"
        if not bundled_dll.is_file():
            return
        destination_dir = profile_root / DLY_PLUGIN_FOLDER
        destination_dll = destination_dir / "Yuyu.HelAmmoDisplay.dll"
        if (
            destination_dll.is_file()
            and _sha256(destination_dll) == _sha256(bundled_dll)
        ):
            return
        if is_gtfo_running():
            raise OSError(
                "需要更新 DLY 插件，但 GTFO 正在运行。请先完全退出游戏，"
                "再重新点击“应用到当前测试档”。"
            )
        destination_dir.mkdir(parents=True, exist_ok=True)
        if destination_dll.is_file():
            backup = destination_dll.with_suffix(".dll.bak")
            shutil.copy2(destination_dll, backup)
        shutil.copy2(bundled_dll, destination_dll)
        bundled_manifest = bundled_dir / "manifest.json"
        if bundled_manifest.is_file():
            shutil.copy2(
                bundled_manifest,
                destination_dir / "manifest.json",
            )

    def save_splitter_sizes(self) -> list[int]:
        return self.splitter.sizes()

    def restore_splitter_sizes(self, sizes: list[int]) -> None:
        if len(sizes) == self.splitter.count() and all(
            value > 0 for value in sizes
        ):
            self.splitter.setSizes(sizes)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


_VISIBILITY_ICON_CACHE: dict[bool, QIcon] = {}


def _visibility_icon(visible: bool) -> QIcon:
    cached = _VISIBILITY_ICON_CACHE.get(visible)
    if cached is not None:
        return cached
    pixmap = QPixmap(20, 20)
    pixmap.fill(Qt.GlobalColor.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)
    color = QColor("#d9e6e3" if visible else "#8b6265")
    painter.setPen(QPen(color, 1.5))
    painter.setBrush(Qt.BrushStyle.NoBrush)
    painter.drawEllipse(2, 5, 16, 10)
    painter.setBrush(color)
    painter.drawEllipse(7, 7, 6, 6)
    if not visible:
        painter.setPen(QPen(QColor("#ff8d91"), 1.8))
        painter.drawLine(3, 17, 17, 3)
    painter.end()
    icon = QIcon(pixmap)
    _VISIBILITY_ICON_CACHE[visible] = icon
    return icon


def _qt_color_to_plugin_text(
    color: QColor,
    *,
    include_alpha: bool,
) -> str:
    if not include_alpha or color.alpha() >= 255:
        return color.name(QColor.NameFormat.HexRgb).upper()
    return (
        f"#{color.red():02X}{color.green():02X}"
        f"{color.blue():02X}{color.alpha():02X}"
    )
