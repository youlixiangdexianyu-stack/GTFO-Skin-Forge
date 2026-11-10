"""DLY display preset storage and BepInEx configuration bridging."""

from __future__ import annotations

import json
import os
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

from .assembly import FORGE_SCENE_TO_GAME_SCALE

# v2 stores display anchors in GTFO weapon space (metres) instead of the 0.8x
# GTFO Forge scene space used by v1 presets.
DLY_SCHEMA_VERSION = 2
DLY_PRESET_FILENAME = "hel_ammo_display.gtfdly"
DLY_PLUGIN_FOLDER = Path("BepInEx") / "plugins" / "Yuyu-HelAmmoDisplay"
DLY_CONFIG_PATH = Path("BepInEx") / "config" / "Yuyu.HelAmmoDisplay.cfg"
DLY_TRANSFORM_TOOLS = ("move", "rotate", "scale")
DLY_FIXED_VIEWS = ("front", "back", "left", "right", "top", "bottom", "iso")


@dataclass(frozen=True, slots=True)
class DlyPart:
    id: str
    label: str
    anchor_name: str
    optional: bool = False


DLY_HEL_PARTS: tuple[DlyPart, ...] = (
    DlyPart("front", "前部 / 枪管", "Front_UZI_2"),
    DlyPart("receiver", "机匣 / 后部", "Receiver_UZI_1"),
    DlyPart("stock", "枪托 / 握把", "Stock_Pistol_1"),
    DlyPart("mag", "弹匣", "Mag_9mm_50rdns_A"),
    DlyPart("sight", "瞄具", "Front_Pistol_Sight_2_E"),
    DlyPart("flashlight", "枪灯", "Flashlight_B", optional=True),
    DlyPart("ammo_bullet", "整颗 9mm 子弹", "Casing_9mm", optional=True),
    DlyPart("ammo_shell", "9mm 空弹壳", "Casing_9mm", optional=True),
)


def part_by_id(part_id: str) -> DlyPart:
    return next(
        (part for part in DLY_HEL_PARTS if part.id == part_id),
        DLY_HEL_PARTS[1],
    )


@dataclass(slots=True)
class DlyDisplayPreset:
    schema_version: int = DLY_SCHEMA_VERSION
    target_item_id: str = "auto_pistol"
    anchor_part_id: str = "receiver"
    anchor_name: str = "Receiver_UZI_1"
    anchor_local_x: float = 0.0
    anchor_local_y: float = 0.0
    anchor_local_z: float = 0.0
    use_absolute_anchor: bool = False
    use_part_normalized_anchor: bool = False
    normalized_anchor_x: float = 0.5
    normalized_anchor_y: float = 1.0
    normalized_anchor_z: float = 0.5
    offset_x: float = 0.0
    offset_y: float = 0.0
    offset_z: float = 0.0
    rotation_x: float = 0.0
    rotation_y: float = 0.0
    rotation_z: float = 0.0
    panel_width: float = 0.03
    panel_height: float = 0.0125
    panel_scale: float = 1.0
    surface_clearance: float = 0.0015
    text_character_size: float = 0.00155
    text_color: str = "#B9FBFF"
    panel_color: str = "#05090AF2"
    border_color: str = "#3EDAF5"
    panel_opacity: float = 0.95
    border_opacity: float = 1.0
    face_player: bool = True
    preview_clip: int = 60
    hidden_parts: list[str] = field(default_factory=list)
    last_transform_tool: str = "move"
    fixed_view_preference: str = "iso"
    surface_snap_enabled: bool = False
    notes: str = ""

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "DlyDisplayPreset":
        defaults = cls()
        try:
            source_version = int(value.get("schema_version", 1) or 1)
        except (TypeError, ValueError):
            source_version = 1
        known = {
            field_name: value[field_name]
            for field_name in cls.__dataclass_fields__
            if field_name in value
        }
        preset = cls(**known)
        if source_version < 2:
            # v1 presets were authored against the 0.8x Forge preview model;
            # v2 uses game weapon space, so scale position-space values once.
            scale = FORGE_SCENE_TO_GAME_SCALE
            preset.anchor_local_x = float(preset.anchor_local_x) * scale
            preset.anchor_local_y = float(preset.anchor_local_y) * scale
            preset.anchor_local_z = float(preset.anchor_local_z) * scale
            preset.offset_x = float(preset.offset_x) * scale
            preset.offset_y = float(preset.offset_y) * scale
            preset.offset_z = float(preset.offset_z) * scale
            preset.surface_clearance = float(preset.surface_clearance) * scale
        preset.schema_version = DLY_SCHEMA_VERSION
        preset.target_item_id = str(preset.target_item_id or defaults.target_item_id)
        part = part_by_id(str(preset.anchor_part_id))
        preset.anchor_part_id = part.id
        if not preset.anchor_name:
            preset.anchor_name = part.anchor_name
        preset.preview_clip = max(0, min(999, int(preset.preview_clip)))
        valid_part_ids = {part.id for part in DLY_HEL_PARTS}
        preset.hidden_parts = [
            part_id
            for part_id in dict.fromkeys(str(value) for value in preset.hidden_parts)
            if part_id in valid_part_ids
        ]
        preset.panel_opacity = max(0.0, min(1.0, float(preset.panel_opacity)))
        preset.border_opacity = max(0.0, min(1.0, float(preset.border_opacity)))
        if preset.last_transform_tool not in DLY_TRANSFORM_TOOLS:
            preset.last_transform_tool = defaults.last_transform_tool
        if preset.fixed_view_preference not in DLY_FIXED_VIEWS:
            preset.fixed_view_preference = defaults.fixed_view_preference
        preset.surface_snap_enabled = bool(preset.surface_snap_enabled)
        preset.use_part_normalized_anchor = bool(
            preset.use_part_normalized_anchor
        )
        return preset

    def select_part(self, part_id: str, reset_offsets: bool = True) -> None:
        part = part_by_id(part_id)
        self.anchor_part_id = part.id
        self.anchor_name = part.anchor_name
        if reset_offsets:
            self.offset_x = 0.0
            self.offset_y = 0.0
            self.offset_z = 0.0


def default_preset_path(data_root: Path) -> Path:
    return data_root / "projects" / DLY_PRESET_FILENAME


def load_dly_preset(path: Path) -> DlyDisplayPreset:
    if not path.is_file():
        return DlyDisplayPreset()
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            return DlyDisplayPreset()
        return DlyDisplayPreset.from_dict(value)
    except (OSError, ValueError, TypeError):
        return DlyDisplayPreset()


def save_dly_preset(path: Path, preset: DlyDisplayPreset) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(preset.to_dict(), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    os.replace(temporary, path)
    return path


def find_gtfo_profile_root(
    *,
    selected_root: Path | str | None = None,
    start: Path | str | None = None,
) -> Path | None:
    candidates: list[Path] = []
    if selected_root:
        candidates.append(Path(selected_root))
    if start:
        candidates.append(Path(start))
    candidates.extend((Path.cwd(), Path(__file__).resolve().parent))

    seen: set[str] = set()
    for candidate in candidates:
        try:
            resolved = candidate.resolve()
        except OSError:
            continue
        for parent in (resolved, *resolved.parents):
            key = str(parent).casefold()
            if key in seen:
                continue
            seen.add(key)
            is_profile = (
                (parent / DLY_PLUGIN_FOLDER).is_dir()
                and (
                    (parent / "BepInEx" / "core").is_dir()
                    or (parent / "winhttp.dll").is_file()
                )
            )
            if is_profile:
                return parent
    return None


def plugin_config_path(profile_root: Path) -> Path:
    return profile_root / DLY_CONFIG_PATH


def write_plugin_config(profile_root: Path, preset: DlyDisplayPreset) -> Path:
    config_path = plugin_config_path(profile_root)
    config_path.parent.mkdir(parents=True, exist_ok=True)
    section = "HEL Ammo Display"
    values = {
        "Enabled": "true",
        "AutoPlaceOnReceiverTop": _bool_text(
            not preset.use_absolute_anchor
        ),
        "FollowItemCamera": _bool_text(preset.face_player),
        "AnchorObjectName": preset.anchor_name,
        "UseAbsoluteAnchor": _bool_text(preset.use_absolute_anchor),
        "AnchorLocalX": _float_text(preset.anchor_local_x),
        "AnchorLocalY": _float_text(preset.anchor_local_y),
        "AnchorLocalZ": _float_text(preset.anchor_local_z),
        "UsePartNormalizedAnchor": _bool_text(
            preset.use_part_normalized_anchor
            and not preset.use_absolute_anchor
        ),
        "NormalizedAnchorX": _float_text(preset.normalized_anchor_x),
        "NormalizedAnchorY": _float_text(preset.normalized_anchor_y),
        "NormalizedAnchorZ": _float_text(preset.normalized_anchor_z),
        "OffsetX": _float_text(
            0.0
            if (
                preset.use_absolute_anchor
                or preset.use_part_normalized_anchor
            )
            else preset.offset_x
        ),
        "OffsetY": _float_text(
            0.0
            if (
                preset.use_absolute_anchor
                or preset.use_part_normalized_anchor
            )
            else preset.offset_y
        ),
        "OffsetZ": _float_text(
            0.0
            if (
                preset.use_absolute_anchor
                or preset.use_part_normalized_anchor
            )
            else preset.offset_z
        ),
        "RotationX": _float_text(preset.rotation_x),
        "RotationY": _float_text(preset.rotation_y),
        "RotationZ": _float_text(preset.rotation_z),
        "PanelWidth": _float_text(preset.panel_width),
        "PanelHeight": _float_text(preset.panel_height),
        "PanelScale": _float_text(preset.panel_scale),
        "SurfaceClearance": _float_text(preset.surface_clearance),
        "TextCharacterSize": _float_text(preset.text_character_size),
        "TextColor": preset.text_color,
        "PanelColor": preset.panel_color,
        "PanelOpacity": _float_text(preset.panel_opacity),
        "BorderColor": preset.border_color,
        "BorderOpacity": _float_text(preset.border_opacity),
    }

    existing = (
        config_path.read_text(encoding="utf-8-sig").splitlines()
        if config_path.is_file()
        else []
    )
    lines = _upsert_section_values(existing, section, values)
    temporary = config_path.with_suffix(config_path.suffix + ".tmp")
    temporary.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
    os.replace(temporary, config_path)
    return config_path


def _upsert_section_values(
    source_lines: list[str],
    section: str,
    values: dict[str, str],
) -> list[str]:
    lines = list(source_lines)
    header = f"[{section}]"
    try:
        section_index = next(
            index for index, line in enumerate(lines)
            if line.strip().casefold() == header.casefold()
        )
    except StopIteration:
        if lines and lines[-1].strip():
            lines.append("")
        lines.append(header)
        lines.extend(f"{key} = {value}" for key, value in values.items())
        return lines

    section_end = len(lines)
    for index in range(section_index + 1, len(lines)):
        stripped = lines[index].strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            section_end = index
            break

    remaining = dict(values)
    for index in range(section_index + 1, section_end):
        stripped = lines[index].lstrip()
        if not stripped or stripped.startswith(("#", ";")):
            continue
        if "=" not in stripped:
            continue
        key = stripped.split("=", 1)[0].strip()
        if key in remaining:
            lines[index] = f"{key} = {remaining.pop(key)}"

    if remaining:
        insertion = section_end
        while insertion > section_index + 1 and not lines[insertion - 1].strip():
            insertion -= 1
        additions = [f"{key} = {value}" for key, value in remaining.items()]
        lines[insertion:insertion] = additions
    return lines


def _bool_text(value: bool) -> str:
    return "true" if value else "false"


def _float_text(value: float) -> str:
    rounded = round(float(value), 8)
    return f"{rounded:.8f}".rstrip("0").rstrip(".") or "0"
