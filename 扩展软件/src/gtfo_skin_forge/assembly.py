from __future__ import annotations

import json
import re
from difflib import SequenceMatcher
from functools import lru_cache
from pathlib import Path
from typing import Any

from .models import CatalogItem, PartSpec


Position = tuple[float, float, float]


def assembly_path() -> Path:
    return Path(__file__).with_name("data") / "assembly.json"


@lru_cache(maxsize=1)
def load_assembly_catalog(path: str | None = None) -> dict[str, Any]:
    source = Path(path) if path else assembly_path()
    if not source.is_file():
        return {"schema_version": 1, "templates": {}, "items": {}}
    value = json.loads(source.read_text(encoding="utf-8"))
    if value.get("schema_version") != 1:
        raise ValueError("Unsupported 3D assembly catalog version")
    return value


def _normalized_name(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.casefold())


def _as_position(value: object) -> Position | None:
    if not isinstance(value, list) or len(value) != 3:
        return None
    try:
        x, y, z = (float(component) for component in value)
    except (TypeError, ValueError):
        return None
    if not all(abs(component) < 1000.0 for component in (x, y, z)):
        return None
    return (x, y, z)


def resolve_mesh_offset(
    item: CatalogItem,
    part: PartSpec,
    mesh_name: str,
    assembly: dict[str, Any] | None = None,
) -> Position:
    """Return the official GTFO Forge node translation for a Unity mesh.

    Unity OBJ vertices already contain component orientation and scale. The
    official Forge scene supplies the missing attachment translations, so only
    the translation component is applied to the local mesh.
    """

    data = assembly or load_assembly_catalog()
    family = data.get("items", {}).get(item.id)
    template = data.get("templates", {}).get(family)
    if not isinstance(template, dict):
        return (0.0, 0.0, 0.0)

    key = _normalized_name(mesh_name)
    material_key = _normalized_name(part.material)

    mesh_offsets = template.get("mesh_offsets", {})
    if isinstance(mesh_offsets, dict):
        position = _as_position(mesh_offsets.get(key))
        if position is not None:
            return position

        # Native mesh nodes commonly end in "_1". The node translation is more
        # authoritative than its parent material group because fold/mount nodes
        # between them carry the actual installation offset.
        closest = _closest_offset(key, mesh_offsets, minimum_score=0.9)
        if closest is not None:
            return closest

    group_offsets = template.get("group_offsets", {})
    if isinstance(group_offsets, dict):
        position = _as_position(group_offsets.get(key))
        if position is not None:
            return position

    material_offsets = template.get("material_offsets", {})
    if isinstance(material_offsets, dict):
        position = _as_position(material_offsets.get(material_key))
        if position is not None:
            return position
        closest = _closest_offset(material_key, material_offsets, minimum_score=0.92)
        if closest is not None:
            return closest

    if isinstance(group_offsets, dict):
        closest = _closest_offset(key, group_offsets, minimum_score=0.84)
        if closest is not None:
            return closest
    return (0.0, 0.0, 0.0)


def _closest_offset(
    key: str,
    table: dict[str, Any],
    *,
    minimum_score: float,
) -> Position | None:
    candidates: list[tuple[float, Position]] = []
    for candidate_name, raw_position in table.items():
        position = _as_position(raw_position)
        if position is None:
            continue
        candidate_key = str(candidate_name)
        score = SequenceMatcher(None, key, candidate_key).ratio()
        if key.startswith(candidate_key) or key.endswith(candidate_key):
            score = max(score, 0.9)
        candidates.append((score, position))
    if not candidates:
        return None
    candidates.sort(key=lambda candidate: candidate[0], reverse=True)
    return candidates[0][1] if candidates[0][0] >= minimum_score else None
