"""Dynamic visual sticker project, composition, pack build, and installation."""

from __future__ import annotations

import hashlib
import json
import math
import os
import re
import shutil
import uuid
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Iterable

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps

from .assets import extract_texture
from .catalog import load_catalog
from .config import ensure_app_dirs
from .game_locator import is_gtfo_running
from .models import CHANNELS, CatalogItem, GameInstall, SkinProject


DYNAMIC_PROJECT_VERSION = 1
DYNAMIC_PACK_VERSION = 1
DYNAMIC_PROJECT_SUFFIX = ".gtfdyn"
DYNAMIC_WORKSPACE = "dynamic"
DYNAMIC_PLUGIN_FOLDER = Path("BepInEx") / "plugins" / "HEL-DynamicChannels"
DYNAMIC_CONFIG_FOLDER = Path("BepInEx") / "config" / "HEL.DynamicChannels"
DYNAMIC_ACTIVE_PACK = DYNAMIC_CONFIG_FOLDER / "active_pack"
DYNAMIC_PLUGIN_NAME = "HEL.DynamicChannels.dll"
DYNAMIC_MANIFEST_NAME = "manifest.json"
DYNAMIC_LAYER_KINDS = ("image", "sequence", "video", "text")
DYNAMIC_CHANNEL_SLOTS = {
    "Albedo": "_MainTex",
    "Normal": "_BumpMap",
    "MetallicSmoothness": "_MetallicGlossMap",
    "Emissive": "_EmissiveMap",
}
MAX_TIMELINE_FRAMES = 600
MAX_ATLAS_SIZE = 4096


def _new_id(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex[:12]}"


def _safe_name(value: str) -> str:
    cleaned = re.sub(r"[^0-9A-Za-z._-]+", "_", value.strip())
    return cleaned.strip("._") or "media"


def _natural_key(path: Path) -> list[Any]:
    return [
        int(part) if part.isdigit() else part.casefold()
        for part in re.split(r"(\d+)", path.name)
    ]


@dataclass(slots=True)
class DynamicLayer:
    id: str = field(default_factory=lambda: _new_id("layer"))
    name: str = "新图层"
    kind: str = "image"
    part_id: str = "front"
    channel: str = "Albedo"
    visible: bool = True
    locked: bool = False
    opacity: float = 1.0
    media_paths: list[str] = field(default_factory=list)
    source_video: str = ""
    fps: float = 10.0
    loop: bool = True
    crop_left: float = 0.0
    crop_top: float = 0.0
    crop_right: float = 1.0
    crop_bottom: float = 1.0
    uv_x: float = 0.38
    uv_y: float = 0.18
    uv_width: float = 0.24
    uv_height: float = 0.18
    uv_rotation: float = 0.0
    scale_x: float = 1.0
    scale_y: float = 1.0
    offset_x: float = 0.0
    offset_y: float = 0.0
    quad: list[float] = field(
        default_factory=lambda: [0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0]
    )
    plane_part_id: str = "front"
    use_absolute_anchor: bool = False
    plane_x: float = 0.0
    plane_y: float = 0.0
    plane_z: float = 0.0
    plane_rx: float = 0.0
    plane_ry: float = 0.0
    plane_rz: float = 0.0
    plane_scale: float = 1.0
    text: str = "动态文字"
    font_path: str = ""
    font_size: int = 96
    text_color: str = "#FFFFFF"
    stroke_color: str = "#00000000"
    stroke_width: int = 0
    text_align: str = "center"
    wrap_width: int = 900

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "DynamicLayer":
        known = {
            key: value[key]
            for key in cls.__dataclass_fields__
            if key in value
        }
        layer = cls(**known)
        if layer.kind not in DYNAMIC_LAYER_KINDS:
            layer.kind = "image"
        if layer.channel not in CHANNELS:
            layer.channel = "Albedo"
        layer.opacity = min(1.0, max(0.0, float(layer.opacity)))
        layer.fps = min(60.0, max(1.0, float(layer.fps)))
        layer.crop_left = min(1.0, max(0.0, float(layer.crop_left)))
        layer.crop_top = min(1.0, max(0.0, float(layer.crop_top)))
        layer.crop_right = min(1.0, max(layer.crop_left + 0.001, float(layer.crop_right)))
        layer.crop_bottom = min(1.0, max(layer.crop_top + 0.001, float(layer.crop_bottom)))
        layer.uv_width = min(2.0, max(0.001, float(layer.uv_width)))
        layer.uv_height = min(2.0, max(0.001, float(layer.uv_height)))
        if len(layer.quad) != 8:
            layer.quad = [0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0]
        return layer


@dataclass(slots=True)
class DynamicVisualProject:
    schema_version: int = DYNAMIC_PROJECT_VERSION
    name: str = "未命名动态视觉"
    item_id: str = "auto_pistol"
    game_root: str = ""
    output_fps: float = 0.0
    loop: bool = True
    selected_part_id: str = "front"
    selected_layer_id: str = ""
    selected_channel: str = "Albedo"
    hidden_parts: list[str] = field(default_factory=list)
    layers: list[DynamicLayer] = field(default_factory=list)
    notes: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": DYNAMIC_PROJECT_VERSION,
            "name": self.name,
            "item_id": self.item_id,
            "game_root": self.game_root,
            "output_fps": self.output_fps,
            "loop": self.loop,
            "selected_part_id": self.selected_part_id,
            "selected_layer_id": self.selected_layer_id,
            "selected_channel": self.selected_channel,
            "hidden_parts": self.hidden_parts,
            "layers": [layer.to_dict() for layer in self.layers],
            "notes": self.notes,
        }

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "DynamicVisualProject":
        return cls(
            schema_version=DYNAMIC_PROJECT_VERSION,
            name=str(value.get("name", "未命名动态视觉")),
            item_id=str(value.get("item_id", "auto_pistol")),
            game_root=str(value.get("game_root", "")),
            output_fps=float(value.get("output_fps", 0.0)),
            loop=bool(value.get("loop", True)),
            selected_part_id=str(value.get("selected_part_id", "front")),
            selected_layer_id=str(value.get("selected_layer_id", "")),
            selected_channel=str(value.get("selected_channel", "Albedo")),
            hidden_parts=[
                str(part_id)
                for part_id in value.get("hidden_parts", [])
            ],
            layers=[
                DynamicLayer.from_dict(layer)
                for layer in value.get("layers", [])
                if isinstance(layer, dict)
            ],
            notes=str(value.get("notes", "")),
        )

    def selected_layer(self) -> DynamicLayer | None:
        return next(
            (layer for layer in self.layers if layer.id == self.selected_layer_id),
            None,
        )

    def active_layers(self) -> list[DynamicLayer]:
        return [
            layer
            for layer in self.layers
            if layer.visible and layer.part_id not in self.hidden_parts
        ]


@dataclass(slots=True)
class DynamicBuildResult:
    project_path: Path
    pack_dir: Path
    export_path: Path
    frame_count: int
    timeline_fps: float
    materials: list[dict[str, Any]]


def hel_item() -> CatalogItem:
    catalog = load_catalog()
    item = catalog.get("auto_pistol")
    if item is None:
        raise ValueError("catalog.json 缺少 auto_pistol")
    return item


def project_media_dir(project_path: Path) -> Path:
    return project_path.with_name(project_path.stem + ".gtfdyn_media")


def autosave_dynamic_path() -> Path:
    return ensure_app_dirs()["projects"] / "dynamic_visuals.gtfdyn"


def save_dynamic_project(project: DynamicVisualProject, path: Path) -> Path:
    path = path.with_suffix(DYNAMIC_PROJECT_SUFFIX)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(project.to_dict(), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    os.replace(temporary, path)
    return path


def load_dynamic_project(path: Path) -> DynamicVisualProject:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("动态工程根节点必须是对象")
    return DynamicVisualProject.from_dict(value)


def copy_media(
    files: Iterable[Path],
    *,
    project_path: Path,
    prefix: str,
) -> list[str]:
    directory = project_media_dir(project_path) / _safe_name(prefix)
    directory.mkdir(parents=True, exist_ok=True)
    result: list[str] = []
    for source in files:
        source = Path(source)
        if not source.is_file():
            continue
        target = directory / source.name
        if target.exists() and _sha256(target) != _sha256(source):
            target = directory / f"{source.stem}-{_sha256(source)[:8]}{source.suffix}"
        if not target.exists():
            shutil.copy2(source, target)
        result.append(str(target.relative_to(project_path.parent)))
    return result


def import_image_layer(
    project: DynamicVisualProject,
    project_path: Path,
    source: Path,
    *,
    channel: str,
    part_id: str,
) -> DynamicLayer:
    media = copy_media([source], project_path=project_path, prefix="images")
    layer = DynamicLayer(
        name=source.stem,
        kind="image",
        part_id=part_id,
        channel=channel,
        media_paths=media,
        plane_part_id=part_id,
    )
    project.layers.append(layer)
    project.selected_layer_id = layer.id
    return layer


def import_sequence_layer(
    project: DynamicVisualProject,
    project_path: Path,
    sources: Iterable[Path],
    *,
    channel: str,
    part_id: str,
    fps: float,
) -> DynamicLayer:
    ordered = sorted((Path(path) for path in sources), key=_natural_key)
    media = copy_media(ordered, project_path=project_path, prefix="sequence")
    layer = DynamicLayer(
        name=ordered[0].stem if ordered else "图片序列",
        kind="sequence",
        part_id=part_id,
        channel=channel,
        media_paths=media,
        fps=max(1.0, min(60.0, float(fps))),
        plane_part_id=part_id,
    )
    project.layers.append(layer)
    project.selected_layer_id = layer.id
    return layer


def import_video_layer(
    project: DynamicVisualProject,
    project_path: Path,
    source: Path,
    *,
    channel: str,
    part_id: str,
    fps: float,
) -> DynamicLayer:
    try:
        import cv2
    except ImportError as exc:
        raise RuntimeError("当前环境缺少 OpenCV，无法导入 MP4") from exc

    source_copy = copy_media([source], project_path=project_path, prefix="video_source")
    source_path = project_path.parent / source_copy[0]
    frame_dir = project_media_dir(project_path) / _safe_name(f"video_{source.stem}")
    frame_dir.mkdir(parents=True, exist_ok=True)
    capture = cv2.VideoCapture(str(source_path))
    if not capture.isOpened():
        raise ValueError(f"无法解码视频: {source}")
    native_fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0)
    if native_fps <= 0.0:
        native_fps = 30.0
    sample_fps = max(1.0, min(60.0, float(fps)))
    duration = float(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0) / native_fps
    wanted = max(1, min(MAX_TIMELINE_FRAMES, int(math.ceil(duration * sample_fps))))
    media: list[str] = []
    for index in range(wanted):
        timestamp = index / sample_fps
        capture.set(cv2.CAP_PROP_POS_MSEC, timestamp * 1000.0)
        ok, frame = capture.read()
        if not ok or frame is None:
            break
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        target = frame_dir / f"frame_{index:04d}.png"
        Image.fromarray(rgb, "RGB").save(target)
        media.append(str(target.relative_to(project_path.parent)))
    capture.release()
    if not media:
        raise ValueError(f"视频没有可读取的画面: {source}")
    layer = DynamicLayer(
        name=source.stem,
        kind="video",
        part_id=part_id,
        channel=channel,
        media_paths=media,
        source_video=source_copy[0],
        fps=sample_fps,
        plane_part_id=part_id,
    )
    project.layers.append(layer)
    project.selected_layer_id = layer.id
    return layer


def add_text_layer(
    project: DynamicVisualProject,
    *,
    channel: str,
    part_id: str,
) -> DynamicLayer:
    layer = DynamicLayer(
        name="文字图层",
        kind="text",
        part_id=part_id,
        channel=channel,
        text="动态文字",
        plane_part_id=part_id,
    )
    project.layers.append(layer)
    project.selected_layer_id = layer.id
    return layer


def duplicate_layer(project: DynamicVisualProject, layer: DynamicLayer) -> DynamicLayer:
    value = layer.to_dict()
    value["id"] = _new_id("layer")
    value["name"] = f"{layer.name} 副本"
    duplicate = DynamicLayer.from_dict(value)
    index = project.layers.index(layer)
    project.layers.insert(index + 1, duplicate)
    project.selected_layer_id = duplicate.id
    return duplicate


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _resolve_media(project_path: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else project_path.parent / path


def _font_for_layer(
    layer: DynamicLayer,
    project_path: Path | None = None,
) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates: list[Path] = []
    if layer.font_path:
        font_path = Path(layer.font_path)
        if not font_path.is_absolute() and project_path is not None:
            font_path = project_path.parent / font_path
        candidates.append(font_path)
    windows = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts"
    candidates.extend(
        [
            windows / "msyh.ttc",
            windows / "msyhbd.ttc",
            windows / "simhei.ttf",
        ]
    )
    for candidate in candidates:
        if candidate.is_file():
            try:
                return ImageFont.truetype(str(candidate), max(8, int(layer.font_size)))
            except OSError:
                continue
    return ImageFont.load_default()


def render_text_layer(
    layer: DynamicLayer,
    project_path: Path | None = None,
) -> Image.Image:
    font = _font_for_layer(layer, project_path)
    width = max(64, int(layer.wrap_width))
    line_height = max(16, int(layer.font_size * 1.25))
    lines = layer.text.splitlines() or [""]
    wrapped: list[str] = []
    max_width = width
    for line in lines:
        words = line.split(" ")
        current = ""
        for word in words:
            candidate = word if not current else f"{current} {word}"
            if font.getlength(candidate) <= width:
                current = candidate
            else:
                if current:
                    wrapped.append(current)
                current = word
        wrapped.append(current)
    max_width = max(
        max((int(font.getlength(line)) for line in wrapped), default=16) + 32,
        32,
    )
    height = max(32, line_height * len(wrapped) + 32)
    image = Image.new("RGBA", (max_width, height), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    y = 16
    for line in wrapped:
        line_width = font.getlength(line)
        if layer.text_align == "left":
            x = 16
        elif layer.text_align == "right":
            x = max_width - 16 - line_width
        else:
            x = (max_width - line_width) / 2.0
        draw.text(
            (x, y),
            line,
            font=font,
            fill=layer.text_color,
            stroke_width=max(0, int(layer.stroke_width)),
            stroke_fill=layer.stroke_color,
        )
        y += line_height
    return image


def layer_frames(project_path: Path, layer: DynamicLayer) -> list[Image.Image]:
    if layer.kind == "text":
        return [render_text_layer(layer, project_path)]
    frames: list[Image.Image] = []
    for value in layer.media_paths:
        path = _resolve_media(project_path, value)
        if not path.is_file():
            continue
        try:
            frames.append(Image.open(path).convert("RGBA"))
        except OSError:
            continue
    return frames or [Image.new("RGBA", (1, 1), (0, 0, 0, 0))]


def effective_timeline(project: DynamicVisualProject) -> tuple[float, int]:
    dynamic = [layer for layer in project.active_layers() if len(layer.media_paths) > 1 or layer.kind == "video"]
    fps = max((float(layer.fps) for layer in dynamic), default=1.0)
    if project.output_fps > 0.0:
        fps = max(fps, float(project.output_fps))
    fps = min(60.0, max(1.0, fps))
    max_duration = 0.0
    for layer in dynamic:
        count = max(1, len(layer.media_paths))
        duration = count / max(1.0, layer.fps)
        if not layer.loop:
            max_duration = max(max_duration, duration)
        else:
            max_duration = max(max_duration, duration)
    frame_count = 1 if max_duration <= 0.0 else int(math.ceil(max_duration * fps))
    frame_count = min(MAX_TIMELINE_FRAMES, max(1, frame_count))
    return fps, frame_count


def frame_for_layer(layer: DynamicLayer, frames: list[Image.Image], time_seconds: float) -> Image.Image:
    if len(frames) <= 1:
        return frames[0]
    index = int(math.floor(time_seconds * max(1.0, layer.fps)))
    if layer.loop:
        index %= len(frames)
    else:
        index = min(len(frames) - 1, index)
    return frames[index]


def _layer_destination(
    layer: DynamicLayer,
    frame: Image.Image,
    texture_size: tuple[int, int],
) -> tuple[Image.Image, tuple[int, int], tuple[int, int, int, int]]:
    width, height = texture_size
    crop_box = (
        int(layer.crop_left * frame.width),
        int(layer.crop_top * frame.height),
        int(layer.crop_right * frame.width),
        int(layer.crop_bottom * frame.height),
    )
    crop_box = (
        max(0, min(frame.width - 1, crop_box[0])),
        max(0, min(frame.height - 1, crop_box[1])),
        max(1, min(frame.width, crop_box[2])),
        max(1, min(frame.height, crop_box[3])),
    )
    source = frame.crop(crop_box)
    destination_width = max(
        1,
        int(round(layer.uv_width * width * max(0.01, layer.scale_x))),
    )
    destination_height = max(
        1,
        int(round(layer.uv_height * height * max(0.01, layer.scale_y))),
    )
    image = source.resize(
        (destination_width, destination_height),
        Image.Resampling.LANCZOS,
    )
    if abs(layer.uv_rotation) > 0.001:
        image = image.rotate(
            layer.uv_rotation,
            resample=Image.Resampling.BICUBIC,
            expand=True,
        )
    center_x = (layer.uv_x + layer.uv_width * 0.5 + layer.offset_x) * width
    center_y = (layer.uv_y + layer.uv_height * 0.5 + layer.offset_y) * height
    left = int(round(center_x - image.width * 0.5))
    top = int(round(center_y - image.height * 0.5))
    alpha = image.getchannel("A")
    alpha = alpha.point(lambda value: int(value * layer.opacity))
    image.putalpha(alpha)
    bounds = (left, top, left + image.width, top + image.height)
    return image, (left, top), bounds


def _normalize_rgb(image: Image.Image) -> Image.Image:
    array = np.asarray(image.convert("RGBA"), dtype=np.float32) / 255.0
    vector = array[..., :3] * 2.0 - 1.0
    length = np.linalg.norm(vector, axis=2, keepdims=True)
    vector /= np.maximum(length, 1e-6)
    array[..., :3] = vector * 0.5 + 0.5
    return Image.fromarray(np.uint8(np.clip(array * 255.0, 0, 255)), "RGBA")


def _compose_group_frame(
    project_path: Path,
    base: Image.Image,
    layers: list[DynamicLayer],
    frames_by_layer: dict[str, list[Image.Image]],
    time_seconds: float,
    union: tuple[int, int, int, int],
) -> Image.Image:
    x0, y0, x1, y1 = union
    canvas = base.crop(union).convert("RGBA")
    for layer in layers:
        frame = frame_for_layer(
            layer,
            frames_by_layer[layer.id],
            time_seconds,
        )
        image, position, _ = _layer_destination(
            layer,
            frame,
            base.size,
        )
        canvas.alpha_composite(image, (position[0] - x0, position[1] - y0))
    if layers and layers[0].channel == "Normal":
        canvas = _normalize_rgb(canvas)
    return canvas


def _union_bounds(
    layers: list[DynamicLayer],
    frames_by_layer: dict[str, list[Image.Image]],
    texture_size: tuple[int, int],
) -> tuple[int, int, int, int]:
    bounds: list[tuple[int, int, int, int]] = []
    for layer in layers:
        frame = frames_by_layer[layer.id][0]
        _, _, layer_bounds = _layer_destination(layer, frame, texture_size)
        bounds.append(layer_bounds)
    width, height = texture_size
    x0 = max(0, min(value[0] for value in bounds))
    y0 = max(0, min(value[1] for value in bounds))
    x1 = min(width, max(value[2] for value in bounds))
    y1 = min(height, max(value[3] for value in bounds))
    if x1 <= x0 or y1 <= y0:
        return (0, 0, min(width, 1), min(height, 1))
    return x0, y0, x1, y1


def _atlas_metadata(width: int, height: int, frame_count: int) -> tuple[int, int, int, int]:
    columns = min(10, max(1, frame_count))
    rows = max(1, int(math.ceil(frame_count / columns)))
    atlas_width = columns * width
    atlas_height = rows * height
    if atlas_width > MAX_ATLAS_SIZE or atlas_height > MAX_ATLAS_SIZE:
        raise ValueError(
            f"补丁图集过大 {atlas_width}x{atlas_height}，"
            "请降低贴花尺寸、动画时长或 FPS。"
        )
    return columns, rows, atlas_width, atlas_height


def build_dynamic_project(
    project: DynamicVisualProject,
    project_path: Path,
    game: GameInstall,
    output_dir: Path,
) -> DynamicBuildResult:
    item = hel_item()
    if project.item_id != item.id:
        raise ValueError("第一版只支持 HEL 自动手枪")
    project_path = project_path.with_suffix(DYNAMIC_PROJECT_SUFFIX)
    save_dynamic_project(project, project_path)
    if output_dir.exists():
        shutil.rmtree(output_dir)
    (output_dir / "base").mkdir(parents=True, exist_ok=True)
    (output_dir / "patches").mkdir(parents=True, exist_ok=True)

    layers = project.active_layers()
    if not layers:
        raise ValueError("工程中没有可见图层")
    timeline_fps, frame_count = effective_timeline(project)
    frames_cache = {
        layer.id: layer_frames(project_path, layer)
        for layer in layers
    }
    groups: dict[tuple[str, str], list[DynamicLayer]] = {}
    for layer in layers:
        groups.setdefault((layer.part_id, layer.channel), []).append(layer)

    manifest: dict[str, Any] = {
        "schema_version": DYNAMIC_PACK_VERSION,
        "project_name": project.name,
        "weapon_id": "auto_pistol",
        "timeline_fps": timeline_fps,
        "frame_count": frame_count,
        "loop": bool(project.loop),
        "materials": [],
    }
    part_map = {part.id: part for part in item.parts}
    for (part_id, channel), group in groups.items():
        part = part_map.get(part_id)
        if part is None or channel not in CHANNELS:
            continue
        base = extract_texture(game, part, channel)
        if base is None:
            raise ValueError(f"{part.material} 缺少 {channel} 通道")
        base = base.convert("RGBA")
        max_size = max(base.size)
        if max_size > 2048:
            scale = 2048.0 / max_size
            base = base.resize(
                (max(1, int(base.width * scale)), max(1, int(base.height * scale))),
                Image.Resampling.LANCZOS,
            )
        union = _union_bounds(group, frames_cache, base.size)
        x0, y0, x1, y1 = union
        patch_width = x1 - x0
        patch_height = y1 - y0
        entry_frame_count = (
            frame_count
            if any(
                layer.kind in {"sequence", "video"}
                and len(frames_cache[layer.id]) > 1
                for layer in group
            )
            else 1
        )
        columns, rows, atlas_width, atlas_height = _atlas_metadata(
            patch_width,
            patch_height,
            entry_frame_count,
        )
        atlas = Image.new("RGBA", (atlas_width, atlas_height), (0, 0, 0, 0))
        for frame_index in range(entry_frame_count):
            time_seconds = frame_index / timeline_fps
            patch = _compose_group_frame(
                project_path,
                base,
                group,
                frames_cache,
                time_seconds,
                union,
            )
            column = frame_index % columns
            row = frame_index // columns
            atlas.alpha_composite(
                patch,
                (column * patch_width, row * patch_height),
            )

        base_rel = Path("base") / part.id / f"{channel}.png"
        patch_rel = Path("patches") / part.id / f"{channel}.png"
        (output_dir / base_rel).parent.mkdir(parents=True, exist_ok=True)
        (output_dir / patch_rel).parent.mkdir(parents=True, exist_ok=True)
        base.save(output_dir / base_rel, optimize=True)
        atlas.save(output_dir / patch_rel, optimize=True)
        manifest["materials"].append(
            {
                "part_id": part.id,
                "material": part.material,
                "channel": channel,
                "texture_property": DYNAMIC_CHANNEL_SLOTS[channel],
                "base_file": base_rel.as_posix(),
                "patch_file": patch_rel.as_posix(),
                "texture_width": base.width,
                "texture_height": base.height,
                "patch_x": x0,
                "patch_y": y0,
                "patch_width": patch_width,
                "patch_height": patch_height,
                "columns": columns,
                "rows": rows,
                "frame_count": entry_frame_count,
            }
        )

    (output_dir / DYNAMIC_MANIFEST_NAME).write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    export_path = output_dir.with_suffix(".gtfdynpack.zip")
    if export_path.exists():
        export_path.unlink()
    shutil.make_archive(
        str(export_path.with_suffix("")),
        "zip",
        root_dir=output_dir,
    )
    return DynamicBuildResult(
        project_path=project_path,
        pack_dir=output_dir,
        export_path=export_path,
        frame_count=frame_count,
        timeline_fps=timeline_fps,
        materials=list(manifest["materials"]),
    )


def compose_preview_skin(
    project: DynamicVisualProject,
    project_path: Path,
    game: GameInstall,
    *,
    time_seconds: float,
    cache_dir: Path,
) -> SkinProject:
    """Compose current frame into preview textures consumable by Weapon3DView."""

    item = hel_item()
    part_map = {part.id: part for part in item.parts}
    cache_dir.mkdir(parents=True, exist_ok=True)
    assignments: dict[str, dict[str, str]] = {}
    groups: dict[tuple[str, str], list[DynamicLayer]] = {}
    for layer in project.active_layers():
        groups.setdefault((layer.part_id, layer.channel), []).append(layer)
    frames_cache = {
        layer.id: layer_frames(project_path, layer)
        for group in groups.values()
        for layer in group
    }
    for (part_id, channel), group in groups.items():
        part = part_map.get(part_id)
        if part is None:
            continue
        base = extract_texture(game, part, channel)
        if base is None:
            continue
        base = base.convert("RGBA")
        full = base.copy()
        for layer in group:
            frame = frame_for_layer(
                layer,
                frames_cache[layer.id],
                time_seconds,
            )
            image, position, _ = _layer_destination(layer, frame, base.size)
            full.alpha_composite(image, position)
        if channel == "Normal":
            full = _normalize_rgb(full)
        target = cache_dir / f"{_safe_name(project.name)}_{part.id}_{channel}.png"
        full.save(target, optimize=True)
        assignments.setdefault(part.id, {})[channel] = str(target)
    return SkinProject(
        name=project.name,
        game_root=str(game.root),
        item_id=item.id,
        assignments=assignments,
        enabled_parts=sorted(
            part.id for part in item.parts if part.optional
        ),
    )


def bundled_dynamic_plugin_dir() -> Path:
    return Path(__file__).with_name("data") / "dynamic_plugin"


def install_dynamic_build(
    profile_root: Path,
    build: DynamicBuildResult,
) -> Path:
    if is_gtfo_running():
        raise OSError("GTFO 正在运行，请完全退出后再安装动态视觉包。")
    plugin_source = bundled_dynamic_plugin_dir()
    plugin_dll = plugin_source / DYNAMIC_PLUGIN_NAME
    if not plugin_dll.is_file():
        raise FileNotFoundError(f"缺少运行时插件: {plugin_dll}")
    destination_plugin_dir = profile_root / DYNAMIC_PLUGIN_FOLDER
    destination_plugin_dir.mkdir(parents=True, exist_ok=True)
    destination_dll = destination_plugin_dir / DYNAMIC_PLUGIN_NAME
    if destination_dll.is_file():
        shutil.copy2(destination_dll, destination_dll.with_suffix(".dll.bak"))
    shutil.copy2(plugin_dll, destination_dll)
    for filename in ("manifest.json",):
        source = plugin_source / filename
        if source.is_file():
            shutil.copy2(source, destination_plugin_dir / filename)
    pack_dir = profile_root / DYNAMIC_ACTIVE_PACK
    if pack_dir.exists():
        backup = pack_dir.with_name("active_pack.bak")
        if backup.exists():
            shutil.rmtree(backup)
        shutil.copytree(pack_dir, backup)
        shutil.rmtree(pack_dir)
    shutil.copytree(build.pack_dir, pack_dir)
    return profile_root / DYNAMIC_ACTIVE_PACK / DYNAMIC_MANIFEST_NAME


def uninstall_dynamic_build(profile_root: Path) -> None:
    if is_gtfo_running():
        raise OSError("GTFO 正在运行，请完全退出后再恢复上一版动态视觉。")
    plugin_dir = profile_root / DYNAMIC_PLUGIN_FOLDER
    backup_dll = plugin_dir / f"{Path(DYNAMIC_PLUGIN_NAME).stem}.dll.bak"
    current_dll = plugin_dir / DYNAMIC_PLUGIN_NAME
    if backup_dll.is_file():
        shutil.copy2(backup_dll, current_dll)
    pack_dir = profile_root / DYNAMIC_ACTIVE_PACK
    backup_pack = pack_dir.with_name("active_pack.bak")
    if backup_pack.is_dir():
        if pack_dir.exists():
            shutil.rmtree(pack_dir)
        shutil.copytree(backup_pack, pack_dir)
    elif pack_dir.exists():
        shutil.rmtree(pack_dir)
