from __future__ import annotations

import gzip
import hashlib
import re
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image

from .assembly import (
    FORGE_SCENE_TO_GAME_SCALE,
    load_assembly_catalog,
    resolve_mesh_offset,
)
from .assets import (
    asset_path,
    close_unity_env,
    extract_texture,
    load_unity_env,
    res_path,
)
from .config import ensure_app_dirs
from .models import CatalogItem, GameInstall, PartSpec, SkinProject


EMISSIVE_PREVIEW_GAIN = 10.0

_PREFERRED_SIGHT_MESHES = {
    "ironsights_4_f": "ironsights_4_f",
    "ironsights_8": "ironsights_8_b",
    "ironsights_9": "ironsights_9_b",
    "ironsights_10": "ironsight_10",
}


@dataclass(slots=True)
class PreviewMesh:
    part_id: str
    material: str
    path_id: int
    mesh_name: str
    obj_path: Path
    bounds_min: tuple[float, float, float]
    bounds_max: tuple[float, float, float]
    position: tuple[float, float, float]


@dataclass(slots=True)
class PreviewMaterial:
    part_id: str
    material: str
    albedo: Path | None
    normal: Path | None
    emissive: Path | None = None


def _material_family(material: str) -> str:
    tokens = material.split("_")
    return "_".join(tokens[:2]).casefold() if len(tokens) >= 2 else material.casefold()


def _material_bound_meshes(env, material: str) -> list:
    """Return meshes whose renderer actually uses the requested material."""

    material_ids = {
        obj.path_id
        for obj in env.objects
        if obj.type.name == "Material"
        and obj.peek_name().casefold() == material.casefold()
    }
    if not material_ids:
        return []

    objects = {obj.path_id: obj for obj in env.objects}
    mesh_filters: dict[int, list] = {}
    for obj in env.objects:
        if obj.type.name != "MeshFilter":
            continue
        try:
            mesh_filter = obj.read()
        except Exception:
            continue
        game_object = getattr(mesh_filter, "m_GameObject", None)
        mesh_pointer = getattr(mesh_filter, "m_Mesh", None)
        if game_object is None or mesh_pointer is None:
            continue
        game_object_id = int(game_object.m_PathID)
        mesh_filters.setdefault(game_object_id, []).append(mesh_pointer)

    result: list = []
    seen: set[int] = set()

    def add_mesh(pointer) -> None:
        path_id = int(getattr(pointer, "m_PathID", 0))
        if not path_id or path_id in seen:
            return
        reader = objects.get(path_id)
        if reader is None:
            # Unity built-in meshes such as Quad are referenced by PPtr but
            # are not listed as local objects in the asset file.
            seen.add(path_id)
            result.append(pointer)
            return
        if reader.type.name != "Mesh":
            return
        seen.add(path_id)
        result.append(reader)

    for obj in env.objects:
        if obj.type.name not in {"MeshRenderer", "SkinnedMeshRenderer"}:
            continue
        try:
            renderer = obj.read()
        except Exception:
            continue
        references = getattr(renderer, "m_Materials", []) or []
        if not any(
            int(getattr(reference, "m_FileID", -1)) == 0
            and int(getattr(reference, "m_PathID", 0)) in material_ids
            for reference in references
        ):
            continue
        if obj.type.name == "SkinnedMeshRenderer":
            mesh_pointer = getattr(renderer, "m_Mesh", None)
            if mesh_pointer is not None:
                add_mesh(mesh_pointer)
            continue
        game_object = getattr(renderer, "m_GameObject", None)
        if game_object is None:
            continue
        for mesh_pointer in mesh_filters.get(int(game_object.m_PathID), []):
            add_mesh(mesh_pointer)
    return result


def _mesh_source_name(source) -> str:
    try:
        return str(source.peek_name())
    except (AttributeError, KeyError, TypeError, ValueError):
        mesh = source.read()
        return str(getattr(mesh, "m_Name", ""))


def _deduplicate_meshes(meshes: list) -> list:
    result = []
    seen: set[str] = set()
    for mesh in meshes:
        name = _mesh_source_name(mesh).casefold()
        if name in seen:
            continue
        seen.add(name)
        result.append(mesh)
    return result


def _mesh_names_for_part(env, part: PartSpec) -> list:
    bound_meshes = _deduplicate_meshes(
        _material_bound_meshes(env, part.material)
    )
    preferred = _PREFERRED_SIGHT_MESHES.get(part.material.casefold())
    if preferred is not None:
        preferred_candidates = bound_meshes or [
            obj
            for obj in env.objects
            if obj.type.name == "Mesh"
        ]
        exact_mesh = [
            obj
            for obj in preferred_candidates
            if _mesh_source_name(obj).casefold() == preferred
        ]
        if exact_mesh:
            return exact_mesh

    if bound_meshes:
        if part.role == "glass":
            return bound_meshes
        result = [
            obj
            for obj in bound_meshes
            if not any(
                token in _mesh_source_name(obj).casefold()
                for token in ("glass", "lens", "screen", "mapper", "_il")
            )
        ]
        return result or bound_meshes

    family = _material_family(part.material)
    exact = part.material.casefold()
    candidates = bound_meshes or list(env.objects)
    result = []
    for obj in candidates:
        if obj.type.name != "Mesh":
            continue
        lowered = _mesh_source_name(obj).casefold()
        if part.role != "glass" and any(
            token in lowered for token in ("glass", "lens", "screen", "mapper", "_il")
        ):
            continue
        if lowered == exact or lowered.startswith(family):
            result.append(obj)
    return _deduplicate_meshes(result)


def _obj_bounds(text: str) -> tuple[tuple[float, float, float], tuple[float, float, float]]:
    vertices = [
        tuple(float(value) for value in line.split()[1:4])
        for line in text.splitlines()
        if line.startswith("v ")
    ]
    if not vertices:
        zero = (0.0, 0.0, 0.0)
        return zero, zero
    array = np.asarray(vertices, dtype=np.float32)
    return (
        tuple(float(value) for value in array.min(axis=0)),
        tuple(float(value) for value in array.max(axis=0)),
    )


def _scale_obj_text(text: str, scale: float) -> str:
    """Return OBJ text with every vertex position multiplied by ``scale``."""
    lines: list[str] = []
    for line in text.splitlines(keepends=True):
        if line.startswith("v "):
            fields = line.split()
            x = float(fields[1]) * scale
            y = float(fields[2]) * scale
            z = float(fields[3]) * scale
            tail = " ".join(fields[4:])
            suffix = f" {tail}" if tail else ""
            lines.append(f"v {x:.8f} {y:.8f} {z:.8f}{suffix}\n")
        else:
            lines.append(line)
    return "".join(lines)


def _official_preview_meshes(
    item: CatalogItem,
    cache_root: Path | None = None,
    enabled_parts: set[str] | None = None,
    disabled_parts: set[str] | None = None,
    unit_scale: float = 1.0,
) -> tuple[list[PreviewMesh], tuple[np.ndarray, np.ndarray]] | None:
    root = Path(__file__).with_name("data") / "preview_meshes" / item.id
    if not root.is_dir():
        return None
    cache_name = "forge_preview_meshes_v1"
    if abs(unit_scale - 1.0) > 1e-9:
        cache_name = f"{cache_name}_x{unit_scale:g}"
    cache = (cache_root or ensure_app_dirs()["cache"]) / cache_name
    meshes: list[PreviewMesh] = []
    all_min: list[np.ndarray] = []
    all_max: list[np.ndarray] = []
    disabled = disabled_parts or set()
    for part in item.active_parts(enabled_parts):
        if part.id in disabled:
            continue
        source = root / f"{part.id}.obj.gz"
        if not source.is_file():
            return None
        path = cache / item.id / f"{part.id}.obj"
        if not path.is_file() or path.stat().st_mtime < source.stat().st_mtime:
            text = gzip.decompress(source.read_bytes()).decode("utf-8")
            if abs(unit_scale - 1.0) > 1e-9:
                text = _scale_obj_text(text, unit_scale)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        bounds_min, bounds_max = _obj_bounds(path.read_text(encoding="utf-8"))
        all_min.append(np.asarray(bounds_min, dtype=np.float32))
        all_max.append(np.asarray(bounds_max, dtype=np.float32))
        meshes.append(
            PreviewMesh(
                part_id=part.id,
                material=part.material,
                path_id=0,
                mesh_name=part.material,
                obj_path=path,
                bounds_min=bounds_min,
                bounds_max=bounds_max,
                position=(0.0, 0.0, 0.0),
            )
        )
    zero = np.zeros(3, dtype=np.float32)
    return meshes, (
        np.min(np.stack(all_min), axis=0) if all_min else zero,
        np.max(np.stack(all_max), axis=0) if all_max else zero,
    )


def extract_weapon_meshes(
    game: GameInstall,
    item: CatalogItem,
    cache_root: Path | None = None,
    enabled_parts: set[str] | None = None,
    disabled_parts: set[str] | None = None,
    unit_scale: float = 1.0,
) -> tuple[list[PreviewMesh], tuple[np.ndarray, np.ndarray]]:
    official = _official_preview_meshes(
        item,
        cache_root,
        enabled_parts,
        disabled_parts,
        unit_scale=unit_scale,
    )
    if official is not None:
        return official

    cache = (cache_root or ensure_app_dirs()["cache"]) / "3d_meshes"
    cache.mkdir(parents=True, exist_ok=True)
    meshes: list[PreviewMesh] = []
    all_min: list[np.ndarray] = []
    all_max: list[np.ndarray] = []
    assembly = load_assembly_catalog()
    disabled = disabled_parts or set()
    for part in item.active_parts(enabled_parts):
        if part.id in disabled:
            continue
        source = asset_path(game.data_dir, part.asset)
        if not source.is_file():
            continue
        env = load_unity_env(source, res_path(game.data_dir, part.asset))
        try:
            for reader in _mesh_names_for_part(env, part):
                mesh = reader.read()
                native_name = getattr(mesh, "m_Name", None)
                mesh_name = str(native_name or _mesh_source_name(reader))
                path_id = int(
                    getattr(reader, "path_id", getattr(reader, "m_PathID", 0))
                )
                obj_text = mesh.export()
                safe_name = re.sub(r"[^A-Za-z0-9_.-]+", "_", mesh_name) or "mesh"
                target = cache / f"{part.asset}_{path_id}_{safe_name}.obj"
                if not target.is_file() or target.stat().st_size < 32:
                    target.write_text(obj_text, encoding="utf-8")
                bounds_min, bounds_max = _obj_bounds(obj_text)
                # assembly.json node translations come from the Forge scene and
                # are therefore 0.8x game weapon space; convert them before they
                # are applied to real game meshes.
                position = tuple(
                    float(component) * FORGE_SCENE_TO_GAME_SCALE
                    for component in resolve_mesh_offset(
                        item,
                        part,
                        mesh_name,
                        assembly,
                    )
                )
                offset = np.asarray(position, dtype=np.float32)
                all_min.append(np.asarray(bounds_min, dtype=np.float32) + offset)
                all_max.append(np.asarray(bounds_max, dtype=np.float32) + offset)
                meshes.append(
                    PreviewMesh(
                        part_id=part.id,
                        material=part.material,
                        path_id=path_id,
                        mesh_name=mesh_name,
                        obj_path=target,
                        bounds_min=bounds_min,
                        bounds_max=bounds_max,
                        position=position,
                    )
                )
        finally:
            close_unity_env(env)
    if not meshes:
        zero = np.zeros(3, dtype=np.float32)
        return meshes, (zero, zero)
    return meshes, (np.min(np.stack(all_min), axis=0), np.max(np.stack(all_max), axis=0))


def _cached_baseline_texture(
    game: GameInstall,
    part: PartSpec,
    channel: str,
    cache_root: Path | None = None,
) -> Path | None:
    cache = (cache_root or ensure_app_dirs()["cache"]) / "3d_textures"
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / f"{part.asset}_{re.sub(r'[^A-Za-z0-9_.-]+', '_', part.material)}_{channel}.png"
    source = asset_path(game.data_dir, part.asset)
    if target.is_file() and target.stat().st_mtime >= source.stat().st_mtime:
        return target
    image = extract_texture(game, part, channel)
    if image is None:
        return None
    image.save(target, optimize=False)
    return target


def _emissive_preview_texture(
    emissive: Path,
    color: tuple[float, float, float, float],
    target: Path,
) -> Path | None:
    with Image.open(emissive) as emissive_image:
        source = np.asarray(emissive_image.convert("RGBA"), dtype=np.float32)

    source_rgb = source[:, :, :3]
    source_alpha = source[:, :, 3] / 255.0
    brightness = np.max(source_rgb, axis=2) / 255.0
    alpha_range = float(np.ptp(source_alpha))

    # Emissive maps are usually opaque images with a black background. In that
    # case the RGB luminance is the glow mask. If the artist supplied a real
    # alpha mask, retain it as well so textured glow edges remain intact.
    luminance_mask = np.clip((brightness - 1.0 / 255.0) / (254.0 / 255.0), 0.0, 1.0)
    has_alpha_mask = alpha_range > 0.01 or float(np.min(source_alpha)) < 0.98
    mask = luminance_mask
    if has_alpha_mask:
        mask = np.maximum(mask, source_alpha)

    tint = np.asarray(color[:3], dtype=np.float32).reshape((1, 1, 3))
    intensity = max(float(np.max(np.abs(tint))), 0.0)
    tinted_rgb = source_rgb * tint * color[3] * EMISSIVE_PREVIEW_GAIN
    if has_alpha_mask:
        tinted_rgb *= source_alpha[:, :, np.newaxis]
    tinted_rgb = np.clip(tinted_rgb, 0.0, 255.0)

    # Qt3D's standard texture material is composited in LDR. Use the mask to
    # carry sub-one intensity into opacity while allowing values above one to
    # visibly brighten the glow instead of merely clipping its color.
    alpha = np.clip(
        mask * min(1.0, intensity) * max(0.0, min(color[3], 1.0)),
        0.0,
        1.0,
    )
    preview = np.empty_like(source, dtype=np.uint8)
    preview[:, :, :3] = np.uint8(np.rint(tinted_rgb))
    preview[:, :, 3] = np.uint8(np.rint(alpha * 255.0))
    if np.max(preview[:, :, 3]) == 0:
        return None

    array = np.ascontiguousarray(preview)
    Image.fromarray(array, "RGBA").save(target, optimize=False)
    return target


def _emissive_cache_digest(
    emissive: Path,
    color: tuple[float, float, float, float],
) -> str:
    stat = emissive.stat()
    key = (
        f"{emissive.resolve()}|{stat.st_size}|{stat.st_mtime_ns}|"
        f"{repr(color)}|glow-gain={EMISSIVE_PREVIEW_GAIN}"
    )
    return hashlib.sha256(key.encode("utf-8")).hexdigest()[:12]


def prepare_preview_materials(
    game: GameInstall,
    item: CatalogItem,
    project: SkinProject,
    cache_root: Path | None = None,
) -> dict[str, PreviewMaterial]:
    result: dict[str, PreviewMaterial] = {}
    cache = (cache_root or ensure_app_dirs()["cache"]) / "3d_textures"
    cache.mkdir(parents=True, exist_ok=True)
    disabled_parts = set(project.disabled_parts)
    for part in item.active_parts(set(project.enabled_parts)):
        if part.id in disabled_parts:
            continue
        files = project.assignments.get(part.id, {})
        albedo = Path(files["Albedo"]) if files.get("Albedo") else None
        if albedo is None or not albedo.is_file():
            albedo = _cached_baseline_texture(game, part, "Albedo", cache_root)
        normal = Path(files["Normal"]) if files.get("Normal") else None
        if normal is None or not normal.is_file():
            normal = _cached_baseline_texture(game, part, "Normal", cache_root)
        emissive_source = Path(files["Emissive"]) if files.get("Emissive") else None
        emissive: Path | None = None
        emissive_settings = project.emissive.get(part.id)
        if (
            emissive_source
            and emissive_source.is_file()
            and emissive_settings
            and emissive_settings.enabled
        ):
            effective_color = emissive_settings.effective_color()
            digest = _emissive_cache_digest(emissive_source, effective_color)
            target = cache / f"emissive_preview_{digest}.png"
            if not target.is_file() or target.stat().st_size == 0:
                emissive = _emissive_preview_texture(
                    emissive_source,
                    emissive_settings.effective_color(),
                    target,
                )
            else:
                emissive = target
        result[part.id] = PreviewMaterial(
            part_id=part.id,
            material=part.material,
            albedo=albedo,
            normal=normal,
            emissive=emissive,
        )
    return result
