from __future__ import annotations

import json
import os
import re
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from pathlib import Path

from .catalog import item_display_name
from .mesh_preview import PreviewMesh, extract_weapon_meshes
from .models import CatalogItem, GameInstall, PartSpec, SkinProject


class ObjExportMode(str, Enum):
    SEPARATE = "separate"
    COMBINED = "combined"


@dataclass(slots=True)
class ObjExportRequest:
    game: GameInstall
    item: CatalogItem
    project: SkinProject
    output_dir: Path
    mode: ObjExportMode
    selected_part_ids: set[str]


@dataclass(slots=True)
class ObjExportResult:
    output_files: list[Path]
    mtl_path: Path
    manifest_path: Path
    texture_set_names: list[str]
    object_count: int
    errors: list[str] = field(default_factory=list)


class ObjExportError(RuntimeError):
    pass


@dataclass(slots=True)
class _ObjGeometry:
    vertices: list[tuple[float, float, float]]
    texcoords: list[tuple[float, float]]
    normals: list[tuple[float, float, float]]
    faces: list[list[str]]


@dataclass(slots=True)
class _ExportPart:
    part: PartSpec
    texture_set_name: str
    meshes: list[PreviewMesh]


_WINDOWS_INVALID = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_RESERVED_NAMES = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{index}" for index in range(1, 10)),
    *(f"LPT{index}" for index in range(1, 10)),
}


def _safe_name(value: str, fallback: str = "part") -> str:
    cleaned = _WINDOWS_INVALID.sub("_", value).strip().rstrip(".")
    if not cleaned:
        cleaned = fallback
    if cleaned.upper() in _RESERVED_NAMES:
        cleaned = f"_{cleaned}"
    return cleaned


def _parse_obj(path: Path) -> _ObjGeometry:
    vertices: list[tuple[float, float, float]] = []
    texcoords: list[tuple[float, float]] = []
    normals: list[tuple[float, float, float]] = []
    faces: list[list[str]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.startswith("v "):
            vertices.append(tuple(float(value) for value in line.split()[1:4]))
        elif line.startswith("vt "):
            texcoords.append(tuple(float(value) for value in line.split()[1:3]))
        elif line.startswith("vn "):
            normals.append(tuple(float(value) for value in line.split()[1:4]))
        elif line.startswith("f "):
            faces.append(line.split()[1:])
    if not vertices or not faces:
        raise ObjExportError(f"网格文件为空或格式无效：{path}")
    return _ObjGeometry(vertices, texcoords, normals, faces)


def _relative_index(raw: str, count: int) -> int:
    value = int(raw)
    return value if value > 0 else count + value + 1


def _append_geometry(
    lines: list[str],
    geometry: _ObjGeometry,
    offset: tuple[float, float, float],
    vertex_offset: int,
    texcoord_offset: int,
    normal_offset: int,
) -> tuple[int, int, int]:
    dx, dy, dz = offset
    lines.extend(
        f"v {x + dx:.9f} {y + dy:.9f} {z + dz:.9f}"
        for x, y, z in geometry.vertices
    )
    lines.extend(f"vt {u:.9f} {v:.9f}" for u, v in geometry.texcoords)
    lines.extend(f"vn {x:.9f} {y:.9f} {z:.9f}" for x, y, z in geometry.normals)
    for face in geometry.faces:
        converted = []
        for token in face:
            parts = token.split("/")
            vertex = _relative_index(parts[0], len(geometry.vertices)) + vertex_offset
            texcoord = (
                _relative_index(parts[1], len(geometry.texcoords)) + texcoord_offset
                if len(parts) > 1 and parts[1]
                else None
            )
            normal = (
                _relative_index(parts[2], len(geometry.normals)) + normal_offset
                if len(parts) > 2 and parts[2]
                else None
            )
            if texcoord is None and normal is None:
                converted.append(str(vertex))
            elif normal is None:
                converted.append(f"{vertex}/{texcoord}")
            else:
                converted.append(f"{vertex}/{texcoord or ''}/{normal}")
        lines.append("f " + " ".join(converted))
    return (
        vertex_offset + len(geometry.vertices),
        texcoord_offset + len(geometry.texcoords),
        normal_offset + len(geometry.normals),
    )


def _texture_set_names(parts: list[PartSpec], selected: set[str]) -> dict[str, str]:
    chosen = [part for part in parts if part.id in selected]
    bases = Counter(_safe_name(part.material, part.id).casefold() for part in chosen)
    result: dict[str, str] = {}
    used: set[str] = set()
    for part in chosen:
        base = _safe_name(part.material, part.id)
        candidate = (
            base
            if bases[base.casefold()] == 1
            else f"{base}__{part.asset}_{_safe_name(part.id)}"
        )
        candidate = _safe_name(candidate, f"asset_{part.asset}_{part.id}")
        suffix = 2
        unique = candidate
        while unique.casefold() in used:
            unique = f"{candidate}_{suffix}"
            suffix += 1
        used.add(unique.casefold())
        result[part.id] = unique
    return result


def _write_atomic(path: Path, text: str) -> None:
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(text, encoding="utf-8", newline="\n")
    os.replace(temporary, path)


def _write_part_geometry(
    lines: list[str],
    exported: _ExportPart,
    vertex_offset: int = 0,
    texcoord_offset: int = 0,
    normal_offset: int = 0,
) -> tuple[int, int, int, int]:
    vertex_count = 0
    for mesh in exported.meshes:
        geometry = _parse_obj(mesh.obj_path)
        vertex_offset, texcoord_offset, normal_offset = _append_geometry(
            lines,
            geometry,
            mesh.position,
            vertex_offset,
            texcoord_offset,
            normal_offset,
        )
        vertex_count += len(geometry.vertices)
    return vertex_offset, texcoord_offset, normal_offset, vertex_count


def _part_block(
    exported: _ExportPart,
    vertex_offset: int = 0,
    texcoord_offset: int = 0,
    normal_offset: int = 0,
) -> tuple[str, tuple[int, int, int, int]]:
    lines = [
        f"o {exported.texture_set_name}",
        f"g {exported.texture_set_name}",
        f"usemtl {exported.texture_set_name}",
    ]
    offsets = _write_part_geometry(
        lines,
        exported,
        vertex_offset,
        texcoord_offset,
        normal_offset,
    )
    return "\n".join(lines) + "\n", offsets


def _material_block(name: str) -> str:
    return "\n".join(
        (
            f"newmtl {name}",
            "Ka 1.000000 1.000000 1.000000",
            "Kd 1.000000 1.000000 1.000000",
            "Ks 0.000000 0.000000 0.000000",
            "d 1.000000",
            "illum 2",
            "",
        )
    )


def export_obj(request: ObjExportRequest) -> ObjExportResult:
    selected = set(request.selected_part_ids)
    if not selected:
        raise ObjExportError("没有勾选任何可导出的部件")
    known_ids = {part.id for part in request.item.parts}
    unknown = selected - known_ids
    if unknown:
        raise ObjExportError(f"选择中包含未知部件：{', '.join(sorted(unknown))}")

    names = _texture_set_names(request.item.parts, selected)
    disabled = known_ids - selected
    meshes, _ = extract_weapon_meshes(
        request.game,
        request.item,
        enabled_parts=selected,
        disabled_parts=disabled,
    )
    by_part: dict[str, list[PreviewMesh]] = {}
    for mesh in meshes:
        if mesh.part_id in selected:
            by_part.setdefault(mesh.part_id, []).append(mesh)
    missing = [
        part.display_name
        for part in request.item.parts
        if part.id in selected and not by_part.get(part.id)
    ]
    if missing:
        raise ObjExportError("以下勾选部件无法取得网格：" + "、".join(missing))

    export_parts = [
        _ExportPart(part, names[part.id], by_part[part.id])
        for part in request.item.parts
        if part.id in selected
    ]
    root = request.output_dir.expanduser().resolve()
    display_name = item_display_name(request.item)
    include_name = _safe_name(display_name, request.item.id)
    local_stamp = datetime.now().astimezone().strftime("%Y-%m-%d-%H%M%S")
    output_prefix = f"{local_stamp}-{include_name}.OBJ"
    output_dir = root / output_prefix
    suffix = 2
    while output_dir.exists():
        output_dir = root / f"{output_prefix}-{suffix}"
        suffix += 1
    try:
        if request.mode == ObjExportMode.SEPARATE:
            output_dir.mkdir(parents=True, exist_ok=True)
        else:
            output_dir.mkdir(parents=True, exist_ok=True)
            obj_name = f"{include_name}_assembled.obj"
        mtl_name = f"{include_name}_materials.mtl"
        mtl_path = output_dir / mtl_name
        manifest_path = output_dir / "obj-export-manifest.json"
        output_files: list[Path] = []
        if request.mode == ObjExportMode.COMBINED:
            blocks: list[str] = []
            vertex_offset = texcoord_offset = normal_offset = 0
            for exported in export_parts:
                block, offsets = _part_block(
                    exported,
                    vertex_offset,
                    texcoord_offset,
                    normal_offset,
                )
                blocks.append(block)
                vertex_offset, texcoord_offset, normal_offset, _ = offsets
            content = (
                "# GTFO Skin Forge assembled OBJ\n"
                "# units: meters\n"
                f"mtllib {mtl_name}\n\n"
                + "\n".join(blocks)
            )
            obj_path = output_dir / obj_name
            _write_atomic(obj_path, content)
            output_files.append(obj_path)
        else:
            for exported in export_parts:
                filename = f"{_safe_name(exported.texture_set_name)}.obj"
                obj_path = output_dir / filename
                block, _ = _part_block(exported)
                content = (
                    "# GTFO Skin Forge part OBJ\n"
                    "# units: meters\n"
                    f"mtllib {mtl_name}\n\n"
                    f"{block}"
                )
                _write_atomic(obj_path, content)
                output_files.append(obj_path)
        _write_atomic(
            mtl_path,
            "# GTFO Skin Forge materials\n"
            + "\n".join(_material_block(part.texture_set_name) for part in export_parts),
        )
    except OSError as exc:
        raise ObjExportError(f"无法写入 OBJ 输出目录：{exc}") from exc

    manifest = {
        "schema_version": 1,
        "application": "GTFO Skin Forge",
        "created_at": datetime.now(timezone.utc).isoformat(),
        "mode": request.mode.value,
        "units": "meters",
        "item": {
            "id": request.item.id,
            "name": display_name,
            "kind": request.item.kind,
        },
        "outputs": [str(path) for path in output_files],
        "mtl": str(mtl_path),
        "parts": [
            {
                "part_id": exported.part.id,
                "display_name": exported.part.display_name,
                "asset": exported.part.asset,
                "native_material": exported.part.material,
                "texture_set": exported.texture_set_name,
                "object_name": exported.texture_set_name,
                "meshes": [
                    {
                        "name": mesh.mesh_name,
                        "path_id": mesh.path_id,
                        "position": list(mesh.position),
                    }
                    for mesh in exported.meshes
                ],
            }
            for exported in export_parts
        ],
    }
    _write_atomic(
        manifest_path,
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
    )
    return ObjExportResult(
        output_files=output_files,
        mtl_path=mtl_path,
        manifest_path=manifest_path,
        texture_set_names=[part.texture_set_name for part in export_parts],
        object_count=len(export_parts),
    )
