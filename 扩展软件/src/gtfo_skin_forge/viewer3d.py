from __future__ import annotations

import math
from pathlib import Path

import numpy as np
from PySide6.Qt3DCore import Qt3DCore
from PySide6.Qt3DExtras import Qt3DExtras
from PySide6.Qt3DRender import Qt3DRender
from PySide6.QtCore import QEvent, QTimer, Qt, QUrl, Signal
from PySide6.QtGui import QColor, QVector3D, QWheelEvent
from PySide6.QtWidgets import (
    QCheckBox,
    QColorDialog,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QSizePolicy,
    QSlider,
    QVBoxLayout,
    QWidget,
)

from .mesh_preview import extract_weapon_meshes, prepare_preview_materials
from .models import CatalogItem, GameInstall, SkinProject


_EMISSIVE_OVERLAY_FRAGMENT_SHADER = b"""
#version 150 core

uniform sampler2D diffuseTexture;

in vec3 position;
in vec2 texCoord;
out vec4 fragColor;

void main()
{
    vec4 glow = texture(diffuseTexture, texCoord);
    float strength = max(max(glow.r, glow.g), glow.b);
    if (strength < 0.01 || glow.a < 0.01)
        discard;
    fragColor = glow;
}
"""


DEFAULT_LIGHT_COLOR = QColor("#fff4e8")


class Weapon3DView(QWidget):
    ambient_intensity_setting_changed = Signal(float)
    ambient_color_setting_changed = Signal(str)
    emissive_visibility_setting_changed = Signal(bool)
    auto_rotate_setting_changed = Signal(bool)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        self.controls = QHBoxLayout()
        self.status = QLabel("导入贴图后将在这里显示整枪 3D 预览")
        self.status.setStyleSheet("color: #9dabb0;")
        self.emissive_check = QCheckBox("原生发光预览")
        self.emissive_check.setChecked(True)
        self.emissive_check.setToolTip(
            "枪体保持正常亮度，仅 Emissive 贴图亮部额外发光"
        )
        self.emissive_check.toggled.connect(self._set_emissive_visible)
        self.auto_rotate_check = QCheckBox("自动旋转")
        self.auto_rotate_check.setChecked(True)
        self.auto_rotate_check.setToolTip("缓慢旋转当前 3D 模型用于展示")
        reset_button = QPushButton("重置视角")
        reset_button.clicked.connect(self.reset_camera)
        self.controls.addWidget(self.status, 1)
        self.controls.addWidget(self.auto_rotate_check)
        self.controls.addWidget(self.emissive_check)
        self.controls.addWidget(reset_button)
        layout.addLayout(self.controls)

        lighting_controls = QHBoxLayout()
        lighting_controls.setSpacing(8)
        ambient_label = QLabel("环境光")
        ambient_label.setStyleSheet("color: #aab8bc;")
        self.ambient_intensity_slider = QSlider(Qt.Orientation.Horizontal)
        self.ambient_intensity_slider.setRange(0, 200)
        self.ambient_intensity_slider.setValue(100)
        self.ambient_intensity_slider.setFixedWidth(150)
        self.ambient_intensity_slider.setToolTip("调节 3D 环境补光强度")
        self.ambient_intensity_value = QLabel("1.00")
        self.ambient_intensity_value.setStyleSheet("color: #d8ff67;")
        self.ambient_intensity_value.setFixedWidth(34)
        self.ambient_color_button = QPushButton()
        self.ambient_color_button.setFixedWidth(82)
        self.ambient_color_button.setToolTip("选择环境光颜色")
        lighting_controls.addStretch(1)
        lighting_controls.addWidget(ambient_label)
        lighting_controls.addWidget(self.ambient_intensity_slider)
        lighting_controls.addWidget(self.ambient_intensity_value)
        lighting_controls.addWidget(self.ambient_color_button)
        layout.addLayout(lighting_controls)

        self.view = Qt3DExtras.Qt3DWindow()
        self.view.defaultFrameGraph().setClearColor(QColor("#0b0e10"))
        self.container = QWidget.createWindowContainer(self.view, self)
        if self.container is None:
            self.status.setText("当前显卡环境不支持 Qt3D 3D 预览")
            self.container = QLabel("3D 预览不可用")
            self.container.setStyleSheet("color: #ff8b83;")
        layout.addWidget(self.container, 1)

        self.root = Qt3DCore.QEntity()
        self.view.setRootEntity(self.root)
        self.model_pivot = Qt3DCore.QEntity(self.root)
        self.model_pivot_transform = Qt3DCore.QTransform(self.model_pivot)
        self.model_pivot.addComponent(self.model_pivot_transform)
        self.model_root = Qt3DCore.QEntity(self.model_pivot)
        self.model_offset_transform = Qt3DCore.QTransform(self.model_root)
        self.model_root.addComponent(self.model_offset_transform)
        self.camera = self.view.camera()
        self.camera.lens().setPerspectiveProjection(35.0, 1.0, 0.001, 30.0)
        self.controller = self._new_orbit_controller()
        self._scene_lights: list[
            tuple[Qt3DRender.QDirectionalLight, float]
        ] = []
        for direction, intensity in (
            (QVector3D(-0.7, -1.0, 0.4), 1.15),
            (QVector3D(0.8, -0.5, -0.6), 0.55),
            (QVector3D(-0.30, 0.65, 0.75), 0.35),
        ):
            light = self._add_light(direction, intensity, DEFAULT_LIGHT_COLOR)
            self._scene_lights.append((light, intensity))
        self._apply_ambient_color(DEFAULT_LIGHT_COLOR)
        self.ambient_intensity_slider.valueChanged.connect(
            self._ambient_intensity_changed
        )
        self.ambient_color_button.clicked.connect(
            self._choose_ambient_color
        )
        self.auto_rotate_check.toggled.connect(self._set_auto_rotate)
        self._auto_rotate_angle = 0.0
        self._auto_rotate_timer = QTimer(self)
        self._auto_rotate_timer.setInterval(33)
        self._auto_rotate_timer.timeout.connect(self._auto_rotate_tick)
        self._auto_rotate_timer.start()
        self.view.installEventFilter(self)
        self.container.installEventFilter(self)
        self._entities: list[Qt3DCore.QEntity] = []
        self._emissive_entities: list[Qt3DCore.QEntity] = []
        self._materials: list[Qt3DCore.QNode] = []
        self._bounds = (np.zeros(3, dtype=np.float32), np.ones(3, dtype=np.float32))
        self._loaded_key: tuple | None = None
        self._mesh_entities_by_part: dict[str, list[Qt3DCore.QEntity]] = {}
        self._part_bounds: dict[
            str, tuple[np.ndarray, np.ndarray]
        ] = {}

    def _new_orbit_controller(self) -> Qt3DExtras.QOrbitCameraController:
        controller = Qt3DExtras.QOrbitCameraController(self.root)
        controller.setCamera(self.camera)
        controller.setLinearSpeed(0.08)
        controller.setLookSpeed(320.0)
        return controller

    def _add_light(
        self,
        direction: QVector3D,
        intensity: float,
        color: QColor | str = "#fff4e8",
    ) -> Qt3DRender.QDirectionalLight:
        entity = Qt3DCore.QEntity(self.root)
        light = Qt3DRender.QDirectionalLight(entity)
        light.setWorldDirection(direction.normalized())
        light.setIntensity(intensity)
        light.setColor(QColor(color))
        entity.addComponent(light)
        return light

    def _ambient_intensity_changed(self, value: int, notify: bool = True) -> None:
        intensity = value / 100.0
        for light, base_intensity in self._scene_lights:
            light.setIntensity(base_intensity * intensity)
        self.ambient_intensity_value.setText(f"{intensity:.2f}")
        if notify:
            self.ambient_intensity_setting_changed.emit(intensity)

    def _choose_ambient_color(self) -> None:
        color = QColorDialog.getColor(
            self._scene_lights[0][0].color(),
            self,
            "选择环境光颜色",
        )
        if color.isValid():
            self._apply_ambient_color(color)
            self.ambient_color_setting_changed.emit(color.name())

    def _apply_ambient_color(self, color: QColor) -> None:
        for light, _ in self._scene_lights:
            light.setColor(color)
        self.ambient_color_button.setText(color.name().upper())
        luminance = (
            0.299 * color.red()
            + 0.587 * color.green()
            + 0.114 * color.blue()
        )
        text_color = "#10130f" if luminance > 145 else "#f5f7f5"
        self.ambient_color_button.setStyleSheet(
            "QPushButton {"
            f"background: {color.name()}; color: {text_color};"
            "border: 1px solid #65757a; border-radius: 4px;"
            "padding: 3px 6px; font-weight: 600;"
            "}"
        )

    def _auto_rotate_tick(self) -> None:
        if not self.auto_rotate_check.isChecked() or not self._entities:
            return
        self._auto_rotate_angle = (self._auto_rotate_angle + 1.00) % 360.0
        self.model_pivot_transform.setRotationY(self._auto_rotate_angle)

    def _set_auto_rotate(self, visible: bool, notify: bool = True) -> None:
        if visible:
            self._auto_rotate_timer.start()
        else:
            self._auto_rotate_timer.stop()
        if notify:
            self.auto_rotate_setting_changed.emit(visible)

    def eventFilter(self, watched, event) -> bool:
        if event.type() == QEvent.Type.Wheel and isinstance(event, QWheelEvent):
            steps = event.angleDelta().y() / 120.0
            if steps:
                center = self.camera.viewCenter()
                offset = self.camera.position() - center
                factor = 0.78 ** steps if steps > 0 else 0.78 ** steps
                self.camera.setPosition(center + offset * factor)
                event.accept()
                return True
        return super().eventFilter(watched, event)

    def apply_preview_settings(
        self,
        intensity: float,
        color: str,
        emissive_visible: bool,
        auto_rotate: bool = True,
    ) -> None:
        value = max(
            self.ambient_intensity_slider.minimum(),
            min(
                self.ambient_intensity_slider.maximum(),
                int(round(float(intensity) * 100.0)),
            ),
        )
        self.ambient_intensity_slider.blockSignals(True)
        self.ambient_intensity_slider.setValue(value)
        self.ambient_intensity_slider.blockSignals(False)
        self._ambient_intensity_changed(value, notify=False)
        parsed = QColor(color)
        if parsed.isValid():
            self._apply_ambient_color(parsed)
        self.emissive_check.blockSignals(True)
        self.emissive_check.setChecked(bool(emissive_visible))
        self.emissive_check.blockSignals(False)
        self._set_emissive_visible(bool(emissive_visible), notify=False)
        self.auto_rotate_check.blockSignals(True)
        self.auto_rotate_check.setChecked(bool(auto_rotate))
        self.auto_rotate_check.blockSignals(False)
        self._set_auto_rotate(bool(auto_rotate), notify=False)

    def _clear_entities(self) -> None:
        for entity in self._entities:
            entity.setParent(None)
            entity.deleteLater()
        self._entities.clear()
        for entity in self._emissive_entities:
            entity.setParent(None)
            entity.deleteLater()
        self._emissive_entities.clear()
        for material in self._materials:
            material.setParent(None)
            material.deleteLater()
        self._materials.clear()
        self._mesh_entities_by_part.clear()
        self._part_bounds.clear()

    def _texture(self, entity: Qt3DCore.QEntity, path: Path | None):
        if path is None or not path.is_file():
            return None
        texture = Qt3DRender.QTexture2D(entity)
        texture.setGenerateMipMaps(True)
        texture.setMagnificationFilter(Qt3DRender.QAbstractTexture.Filter.Linear)
        texture.setMinificationFilter(Qt3DRender.QAbstractTexture.Filter.LinearMipMapLinear)
        image = Qt3DRender.QTextureImage(texture)
        image.setSource(QUrl.fromLocalFile(str(path)))
        texture.addTextureImage(image)
        return texture

    def _emissive_overlay_material(self, path: Path) -> Qt3DExtras.QTextureMaterial:
        material = Qt3DExtras.QTextureMaterial(self.root)
        material.setShareable(True)
        material.setAlphaBlendingEnabled(True)
        texture = self._texture(material, path)
        if texture is not None:
            material.setTexture(texture)
        for technique in material.effect().techniques():
            for render_pass in technique.renderPasses():
                shader = render_pass.shaderProgram()
                if (
                    technique.graphicsApiFilter().api()
                    == Qt3DRender.QGraphicsApiFilter.Api.OpenGL
                    and technique.graphicsApiFilter().majorVersion() >= 3
                    and shader.format() == Qt3DRender.QShaderProgram.Format.GLSL
                ):
                    shader.setFragmentShaderCode(
                        _EMISSIVE_OVERLAY_FRAGMENT_SHADER
                    )
                for state in list(render_pass.renderStates()):
                    if isinstance(state, Qt3DRender.QNoDepthMask):
                        render_pass.removeRenderState(state)
                    elif isinstance(state, Qt3DRender.QBlendEquationArguments):
                        state.setSourceRgb(
                            Qt3DRender.QBlendEquationArguments.Blending.One
                        )
                        state.setDestinationRgb(
                            Qt3DRender.QBlendEquationArguments.Blending.One
                        )
                        state.setSourceAlpha(
                            Qt3DRender.QBlendEquationArguments.Blending.One
                        )
                        state.setDestinationAlpha(
                            Qt3DRender.QBlendEquationArguments.Blending.One
                        )
                depth_test = Qt3DRender.QDepthTest(render_pass)
                depth_test.setDepthFunction(
                    Qt3DRender.QDepthTest.DepthFunction.Always
                )
                render_pass.addRenderState(depth_test)
        return material

    def _set_emissive_visible(self, visible: bool, notify: bool = True) -> None:
        for entity in self._emissive_entities:
            entity.setEnabled(visible)
        if notify:
            self.emissive_visibility_setting_changed.emit(visible)

    def load_weapon(
        self,
        game: GameInstall,
        item: CatalogItem,
        project: SkinProject | None = None,
    ) -> None:
        project = project or SkinProject(name=item.name, item_id=item.id)
        assignment_key = tuple(
            sorted(
                (
                    part_id,
                    tuple(sorted(files.items())),
                )
                for part_id, files in project.assignments.items()
            )
        )
        lighting_key = tuple(
            (
                part_id,
                settings.enabled,
                settings.effective_color(),
            )
            for part_id, settings in sorted(project.emissive.items())
        )
        key = (
            item.id,
            assignment_key,
            lighting_key,
            tuple(project.disabled_parts),
        )
        if key == self._loaded_key:
            return
        self.status.setText("正在加载整枪网格…")
        self.setCursor(Qt.CursorShape.WaitCursor)
        try:
            enabled_parts = set(project.enabled_parts)
            meshes, bounds = extract_weapon_meshes(
                game,
                item,
                enabled_parts=enabled_parts,
                disabled_parts=set(project.disabled_parts),
            )
            materials = prepare_preview_materials(game, item, project)
            self._clear_entities()
            self._materials.clear()
            center_array = (bounds[0] + bounds[1]) / 2.0
            center_vector = QVector3D(
                float(center_array[0]),
                float(center_array[1]),
                float(center_array[2]),
            )
            self.model_pivot_transform.setTranslation(center_vector)
            self.model_offset_transform.setTranslation(-center_vector)
            self._auto_rotate_angle = 0.0
            self.model_pivot_transform.setRotationY(0.0)
            materials_by_part: dict[str, Qt3DExtras.QMetalRoughMaterial] = {}
            emissive_materials_by_part: dict[str, Qt3DExtras.QTextureMaterial] = {}
            emissive_parts: set[str] = set()
            for part_id, material_info in materials.items():
                material = Qt3DExtras.QMetalRoughMaterial(self.root)
                material.setShareable(True)
                albedo_texture = self._texture(material, material_info.albedo)
                normal_texture = self._texture(material, material_info.normal)
                if albedo_texture is not None:
                    material.setBaseColor(albedo_texture)
                else:
                    material.setBaseColor(QColor("#697176"))
                if normal_texture is not None:
                    material.setNormal(normal_texture)
                material.setMetalness(0.12)
                material.setRoughness(0.58)
                self._materials.append(material)
                if material_info.emissive:
                    emissive_material = self._emissive_overlay_material(
                        material_info.emissive
                    )
                    self._materials.append(emissive_material)
                    emissive_materials_by_part[part_id] = emissive_material
                    emissive_parts.add(part_id)
                materials_by_part[part_id] = material
            for mesh_info in meshes:
                entity = Qt3DCore.QEntity(self.model_root)
                mesh = Qt3DRender.QMesh(entity)
                mesh.setSource(QUrl.fromLocalFile(str(mesh_info.obj_path)))
                entity.addComponent(mesh)
                transform = Qt3DCore.QTransform(entity)
                transform.setTranslation(QVector3D(*mesh_info.position))
                entity.addComponent(transform)
                material = materials_by_part.get(mesh_info.part_id)
                if material is not None:
                    entity.addComponent(material)
                self._entities.append(entity)
                self._mesh_entities_by_part.setdefault(
                    mesh_info.part_id, []
                ).append(entity)
                mesh_low = np.asarray(
                    mesh_info.bounds_min, dtype=np.float32
                ) + np.asarray(mesh_info.position, dtype=np.float32)
                mesh_high = np.asarray(
                    mesh_info.bounds_max, dtype=np.float32
                ) + np.asarray(mesh_info.position, dtype=np.float32)
                if mesh_info.part_id in self._part_bounds:
                    part_low, part_high = self._part_bounds[mesh_info.part_id]
                    self._part_bounds[mesh_info.part_id] = (
                        np.minimum(part_low, mesh_low),
                        np.maximum(part_high, mesh_high),
                    )
                else:
                    self._part_bounds[mesh_info.part_id] = (
                        mesh_low,
                        mesh_high,
                    )
                emissive_material = emissive_materials_by_part.get(mesh_info.part_id)
                if emissive_material is not None:
                    glow_entity = Qt3DCore.QEntity(self.model_root)
                    glow_mesh = Qt3DRender.QMesh(glow_entity)
                    glow_mesh.setSource(QUrl.fromLocalFile(str(mesh_info.obj_path)))
                    glow_entity.addComponent(glow_mesh)
                    glow_transform = Qt3DCore.QTransform(glow_entity)
                    glow_transform.setTranslation(QVector3D(*mesh_info.position))
                    glow_entity.addComponent(glow_transform)
                    glow_entity.addComponent(emissive_material)
                    self._emissive_entities.append(glow_entity)
            self._set_emissive_visible(self.emissive_check.isChecked())
            self._bounds = bounds
            self.reset_camera()
            self._set_auto_rotate(
                self.auto_rotate_check.isChecked(),
                notify=False,
            )
            self._loaded_key = key
            glow_status = (
                f" · 原生发光 {len(emissive_parts)} 个部件"
                if emissive_parts
                else " · 未检测到已启用发光"
            )
            self.status.setText(
                f"{len(meshes)} 个网格 · 本地高模 + Forge 装配"
                f"{glow_status} · 左键平移 · 右键旋转 · 滚轮缩放"
            )
        except Exception as exc:
            self._loaded_key = None
            self.status.setText(f"3D 预览加载失败：{type(exc).__name__}: {exc}")
        finally:
            self.unsetCursor()

    def reset_camera(self) -> None:
        self._auto_rotate_angle = 0.0
        self.model_pivot_transform.setRotationY(0.0)
        self.controller.setEnabled(False)
        self.controller.setParent(None)
        self.controller.deleteLater()
        low, high = self._bounds
        center_array = (low + high) / 2.0
        extent = max(float(np.max(high - low)), 0.05)
        center = QVector3D(float(center_array[0]), float(center_array[1]), float(center_array[2]))
        vertical_span = float(high[1] - low[1])
        distance = max(
            extent * 1.45,
            vertical_span / (2.0 * math.tan(math.radians(17.5))) * 1.30,
        )
        self.camera.setPosition(
            QVector3D(
                float(center_array[0]) + distance,
                float(center_array[1]),
                float(center_array[2]),
            )
        )
        self.camera.setUpVector(QVector3D(0.0, 1.0, 0.0))
        self.camera.setViewCenter(center)
        self.camera.lens().setPerspectiveProjection(
            35.0,
            max(self.width(), 1) / max(self.height(), 1),
            max(extent / 500.0, 0.0001),
            max(extent * 30.0, 1.0),
        )
        self.controller = self._new_orbit_controller()
        self.controller.setLinearSpeed(distance * 0.80)
        self.controller.setLookSpeed(320.0)

    def mesh_entities_for_part(
        self,
        part_id: str,
    ) -> tuple[Qt3DCore.QEntity, ...]:
        return tuple(self._mesh_entities_by_part.get(part_id, ()))

    def bounds_for_part(
        self,
        part_id: str,
    ) -> tuple[np.ndarray, np.ndarray] | None:
        return self._part_bounds.get(part_id)
