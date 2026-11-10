from __future__ import annotations

from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


CHANNELS = ("Albedo", "Normal", "MetallicSmoothness", "Emissive")
CHANNEL_SLOTS = {
    "Albedo": "_MainTex",
    "Normal": "_BumpMap",
    "MetallicSmoothness": "_MetallicGlossMap",
    "Emissive": "_EmissiveMap",
}
CHANNEL_LABELS_ZH = {
    "Albedo": "基础色",
    "Normal": "法线",
    "MetallicSmoothness": "金属/光滑",
    "Emissive": "自发光",
}
ROLE_LABELS_ZH = {
    "front": "枪身 / 前部",
    "receiver": "机匣 / 后部",
    "stock": "枪托 / 框架",
    "mag": "弹匣",
    "sight": "瞄具",
    "glass": "镜片",
    "flashlight": "枪灯",
    "screen": "屏幕",
    "main": "主体",
    "ammo": "整颗子弹 / 弹壳",
}


@dataclass(slots=True)
class GameInstall:
    root: Path
    data_dir: Path
    source: str
    valid: bool = True
    issues: list[str] = field(default_factory=list)

    @property
    def exe_path(self) -> Path:
        return self.root / "GTFO.exe"

    def to_dict(self) -> dict[str, Any]:
        return {
            "root": str(self.root),
            "data_dir": str(self.data_dir),
            "source": self.source,
            "valid": self.valid,
            "issues": self.issues,
        }

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "GameInstall":
        return cls(
            root=Path(value["root"]),
            data_dir=Path(value["data_dir"]),
            source=str(value.get("source", "manual")),
            valid=bool(value.get("valid", True)),
            issues=list(value.get("issues", [])),
        )


@dataclass(slots=True)
class PartSpec:
    id: str
    role: str
    asset: int
    material: str
    name_zh: str = ""
    meshes: list[str] = field(default_factory=list)
    channels: list[str] = field(default_factory=lambda: list(CHANNELS))
    glass_material: str | None = None
    optional: bool = False

    @property
    def display_name(self) -> str:
        return self.name_zh or ROLE_LABELS_ZH.get(self.role, self.role)


@dataclass(slots=True)
class CatalogItem:
    id: str
    name: str
    category: str
    aliases: list[str]
    parts: list[PartSpec]
    description: str = ""
    kind: str = "weapon"

    def active_parts(self, enabled_optional: set[str] | None = None) -> list[PartSpec]:
        enabled = enabled_optional or set()
        return [
            part
            for part in self.parts
            if not part.optional or part.id in enabled
        ]


@dataclass(slots=True)
class AssetMaterialInfo:
    asset: int
    material: str
    slots: dict[str, int | None]
    floats: dict[str, float]
    colors: dict[str, tuple[float, float, float, float]]
    keywords: str
    mesh_names: list[str] = field(default_factory=list)
    compatible_emissive: bool = True
    warnings: list[str] = field(default_factory=list)


@dataclass(slots=True)
class PartValidation:
    part_id: str
    supported: bool
    available_channels: list[str]
    material_info: AssetMaterialInfo | None = None
    issues: list[str] = field(default_factory=list)


@dataclass(slots=True)
class CatalogValidation:
    item_id: str
    supported: bool
    parts: dict[str, PartValidation] = field(default_factory=dict)
    affected_items: list[str] = field(default_factory=list)
    issues: list[str] = field(default_factory=list)


@dataclass(slots=True)
class TextureCandidate:
    path: Path
    width: int
    height: int
    has_alpha: bool
    channel_guess: str | None
    channel_confidence: float
    warnings: list[str] = field(default_factory=list)


@dataclass(slots=True)
class SlotMatch:
    part_id: str
    channel: str
    path: Path | None = None
    part_confidence: float = 0.0
    channel_confidence: float = 0.0
    source: str = "automatic"
    warnings: list[str] = field(default_factory=list)

    @property
    def confidence(self) -> float:
        return min(self.part_confidence, self.channel_confidence or 0.0)

    @property
    def needs_confirmation(self) -> bool:
        return bool(self.path) and self.source != "manual" and self.confidence < 0.82


@dataclass(slots=True)
class MatchAnalysis:
    item_id: str
    folder: Path
    candidates: list[TextureCandidate] = field(default_factory=list)
    slots: dict[str, dict[str, SlotMatch]] = field(default_factory=dict)
    missing_parts: list[str] = field(default_factory=list)
    missing_channels: dict[str, list[str]] = field(default_factory=dict)
    unknown_files: list[Path] = field(default_factory=list)
    conflicts: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)


@dataclass(slots=True)
class EmissiveSettings:
    enabled: bool = False
    color: tuple[float, float, float, float] = (0.3, 0.3, 0.3, 1.0)
    intensity: float = 1.0
    mask_path: Path | None = None

    def effective_color(self) -> tuple[float, float, float, float]:
        return (
            max(0.0, self.color[0] * self.intensity),
            max(0.0, self.color[1] * self.intensity),
            max(0.0, self.color[2] * self.intensity),
            self.color[3],
        )

    def to_dict(self) -> dict[str, Any]:
        return {
            "enabled": self.enabled,
            "color": list(self.color),
            "intensity": self.intensity,
            "mask_path": str(self.mask_path) if self.mask_path else None,
        }

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "EmissiveSettings":
        color = tuple(float(x) for x in value.get("color", (0.3, 0.3, 0.3, 1.0)))
        mask = value.get("mask_path")
        return cls(
            enabled=bool(value.get("enabled", False)),
            color=(color + (1.0,))[:4],
            intensity=float(value.get("intensity", 1.0)),
            mask_path=Path(mask) if mask else None,
        )


@dataclass(slots=True)
class SkinProject:
    schema_version: int = 3
    name: str = "未命名枪皮"
    game_root: str = ""
    item_id: str = ""
    source_folder: str = ""
    resolution_mode: str = "user"
    assignments: dict[str, dict[str, str]] = field(default_factory=dict)
    confirmations: dict[str, bool] = field(default_factory=dict)
    disabled_parts: list[str] = field(default_factory=list)
    emissive: dict[str, EmissiveSettings] = field(default_factory=dict)
    enabled_parts: list[str] = field(default_factory=list)
    notes: str = ""

    def to_dict(self) -> dict[str, Any]:
        return {
            "schema_version": self.schema_version,
            "name": self.name,
            "game_root": self.game_root,
            "item_id": self.item_id,
            "source_folder": self.source_folder,
            "resolution_mode": self.resolution_mode,
            "assignments": self.assignments,
            "confirmations": self.confirmations,
            "disabled_parts": self.disabled_parts,
            "emissive": {key: value.to_dict() for key, value in self.emissive.items()},
            "enabled_parts": self.enabled_parts,
            "notes": self.notes,
        }

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "SkinProject":
        version = int(value.get("schema_version", 1))
        resolution_mode = str(value.get("resolution_mode", "user"))
        if version < 2 and resolution_mode == "source":
            resolution_mode = "user"
        return cls(
            schema_version=3,
            name=str(value.get("name", "未命名枪皮")),
            game_root=str(value.get("game_root", "")),
            item_id=str(value.get("item_id", "")),
            source_folder=str(value.get("source_folder", "")),
            resolution_mode=resolution_mode,
            assignments={
                str(part): {str(channel): str(path) for channel, path in slots.items()}
                for part, slots in value.get("assignments", {}).items()
            },
            confirmations={str(k): bool(v) for k, v in value.get("confirmations", {}).items()},
            disabled_parts=[
                str(part_id)
                for part_id in value.get("disabled_parts", [])
            ],
            emissive={
                str(part): EmissiveSettings.from_dict(settings)
                for part, settings in value.get("emissive", {}).items()
            },
            enabled_parts=[
                str(part_id) for part_id in value.get("enabled_parts", [])
            ],
            notes=str(value.get("notes", "")),
        )

    @classmethod
    def from_analysis(
        cls,
        *,
        name: str,
        game_root: Path | str,
        item_id: str,
        folder: Path,
        analysis: MatchAnalysis,
        resolution_mode: str = "user",
    ) -> "SkinProject":
        assignments: dict[str, dict[str, str]] = {}
        for part_id, channels in analysis.slots.items():
            files = {channel: str(match.path) for channel, match in channels.items() if match.path}
            if files:
                assignments[part_id] = files
        return cls(
            name=name,
            game_root=str(game_root),
            item_id=item_id,
            source_folder=str(folder),
            resolution_mode=resolution_mode,
            assignments=assignments,
        )


@dataclass(slots=True)
class BuildFileResult:
    asset: int
    output_path: Path
    source_path: Path
    sha256: str
    materials: list[str]
    texture_path_ids: dict[str, dict[str, int]]
    emissive: dict[str, dict[str, Any]]


@dataclass(slots=True)
class BuildResult:
    item_id: str
    output_dir: Path
    manifest_path: Path
    files: list[BuildFileResult]
    warnings: list[str]
    affected_items: list[str]
    item_name: str = ""


@dataclass(slots=True)
class InstallRecord:
    id: str
    created_at: str
    game_root: str
    backup_dir: str
    files: list[dict[str, str]]
    manifest_path: str
    retained: bool = True

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, value: dict[str, Any]) -> "InstallRecord":
        return cls(**value)
