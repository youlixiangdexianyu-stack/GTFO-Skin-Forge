from __future__ import annotations

import gzip
import itertools
import os
import re
from dataclasses import dataclass
from difflib import SequenceMatcher
from pathlib import Path

import numpy as np

from generate_preview_meshes import (
    OUTPUT_ROOT,
    load_document,
    matching_material,
    read_accessor,
    trs_matrix,
)
from gtfo_skin_forge.assets import close_unity_env, load_unity_env, res_path
from gtfo_skin_forge.assembly import load_assembly_catalog
from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.models import GameInstall, PartSpec


WORKSPACE = Path(__file__).resolve().parents[1]
GAME_ROOT = Path("D:/Steam/steamapps/common/GTFO")
ATTACHMENT_FAMILIES = {
    "gun_light_a": "part-flashlight-flashlight-a",
    "gun_light_b": "part-flashlight-flashlight-b",
    "gun_light_c": "part-flashlight-flashlight-c",
    "gun_light_d": "part-flashlight-flashlight-d",
    "peq_1_e": "architecture-6dc5de77023a7e0a",
    "peq_1_lens": "architecture-200e0b99ca113d4c",
}
MANUAL_MESH_MATRICES = {
    ("short_shotgun_2", "Front_Short_Shotgun_2"): np.asarray(
        [
            [1.0, 0.0, 0.0, 0.000322003],
            [0.0, 0.0, 1.0, -0.007498155],
            [0.0, -1.0, 0.0, 0.054508285],
            [0.0, 0.0, 0.0, 1.0],
        ],
        dtype=float,
    ),
    ("short_shotgun_2", "Front_Short_Shotgun_Back_2"): np.eye(4),
    ("short_shotgun_2", "Receiver_Short_Shotgun_2"): np.eye(4),
    ("short_shotgun_2", "Stock_Short_Shotgun_2"): np.eye(4),
    ("short_shotgun_2", "Stock_Short_Shotgun_Trigger_2"): np.eye(4),
    ("large_caliber", "Gun_Light_B"): np.asarray(
        [
            [0.0, 1.0, 0.0, -0.000000025],
            [-1.0, 0.0, 0.0, -0.060848209],
            [0.0, 0.0, 1.0, 0.039685676],
            [0.0, 0.0, 0.0, 1.0],
        ],
        dtype=float,
    ),
    ("large_caliber", "Gun_Light_Glass_B"): np.asarray(
        [
            [0.0, 1.0, 0.0, -0.000000025],
            [-1.0, 0.0, 0.0, -0.060848209],
            [0.0, 0.0, 1.0, 0.039685676],
            [0.0, 0.0, 0.0, 1.0],
        ],
        dtype=float,
    ),
    ("small_pistol", "Gun_Light_B"): np.asarray(
        [
            [0.0, 1.0, 0.0, -0.000000025],
            [-1.0, 0.0, 0.0, -0.060848209],
            [0.0, 0.0, 1.0, 0.039685676],
            [0.0, 0.0, 0.0, 1.0],
        ],
        dtype=float,
    ),
    ("small_pistol", "Gun_Light_Glass_B"): np.asarray(
        [
            [0.0, 1.0, 0.0, -0.000000025],
            [-1.0, 0.0, 0.0, -0.060848209],
            [0.0, 0.0, 1.0, 0.039685676],
            [0.0, 0.0, 0.0, 1.0],
        ],
        dtype=float,
    ),
}
PART_POST_TRANSLATIONS = {}
EXCLUDED_MESHES = {
    ("short_shotgun_2", "Stock_Short_Shotgun_Breach_2"),
}


@dataclass(slots=True)
class LowMesh:
    name: str
    matrix: np.ndarray
    positions: np.ndarray
    bounds_min: np.ndarray
    bounds_max: np.ndarray


@dataclass(slots=True)
class HighMesh:
    name: str
    text: str
    bounds_min: np.ndarray
    bounds_max: np.ndarray


def normalized(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.casefold())


def parse_obj_bounds(text: str) -> tuple[np.ndarray, np.ndarray]:
    vertices = np.asarray(
        [
            [float(value) for value in line.split()[1:4]]
            for line in text.splitlines()
            if line.startswith("v ")
        ],
        dtype=float,
    )
    return vertices.min(axis=0), vertices.max(axis=0)


def high_meshes_for_part(game: GameInstall, part: PartSpec) -> list[HighMesh]:
    source = GAME_ROOT / "GTFO_Data" / f"sharedassets{part.asset}.assets"
    env = load_unity_env(source, res_path(game.data_dir, part.asset))
    try:
        objects = {obj.path_id: obj for obj in env.objects}
        material_ids = {
            reader.path_id
            for reader in env.objects
            if reader.type.name == "Material" and reader.peek_name() == part.material
        }
        if not material_ids:
            raise ValueError(f"{part.asset}: material {part.material} not found")
        game_objects: set[int] = set()
        for reader in env.objects:
            if reader.type.name not in {"MeshRenderer", "SkinnedMeshRenderer"}:
                continue
            try:
                renderer = reader.read()
            except Exception:
                continue
            materials = getattr(renderer, "m_Materials", []) or []
            if not any(
                int(getattr(material, "m_FileID", -1)) == 0
                and int(getattr(material, "m_PathID", 0)) in material_ids
                for material in materials
            ):
                continue
            game_object = getattr(renderer, "m_GameObject", None)
            if game_object is not None:
                game_objects.add(int(game_object.m_PathID))
        result: list[HighMesh] = []
        seen: set[str] = set()
        for reader in env.objects:
            if reader.type.name != "MeshFilter":
                continue
            try:
                mesh_filter = reader.read()
            except Exception:
                continue
            game_object = getattr(mesh_filter, "m_GameObject", None)
            if game_object is None or int(game_object.m_PathID) not in game_objects:
                continue
            pointer = getattr(mesh_filter, "m_Mesh", None)
            if pointer is None:
                continue
            try:
                mesh = pointer.read()
                name = str(mesh.m_Name)
                text = mesh.export()
            except Exception:
                continue
            if name in seen or not text:
                continue
            seen.add(name)
            bounds_min, bounds_max = parse_obj_bounds(text)
            result.append(HighMesh(name=name, text=text, bounds_min=bounds_min, bounds_max=bounds_max))
        return result
    finally:
        close_unity_env(env)


def high_meshes_by_names(
    game: GameInstall,
    asset: int,
    names: set[str],
) -> list[HighMesh]:
    source = GAME_ROOT / "GTFO_Data" / f"sharedassets{asset}.assets"
    env = load_unity_env(source, res_path(game.data_dir, asset))
    try:
        result = []
        for reader in env.objects:
            if reader.type.name != "Mesh" or reader.peek_name() not in names:
                continue
            text = reader.read().export()
            bounds_min, bounds_max = parse_obj_bounds(text)
            result.append(
                HighMesh(
                    name=reader.peek_name(),
                    text=text,
                    bounds_min=bounds_min,
                    bounds_max=bounds_max,
                )
            )
        return result
    finally:
        close_unity_env(env)


def low_meshes(document: dict, binary: bytes, material: str) -> list[LowMesh]:
    nodes = document.get("nodes", [])
    parent = {
        child: index
        for index, node in enumerate(nodes)
        for child in node.get("children", [])
    }
    world_cache: dict[int, np.ndarray] = {}

    def world_matrix(index: int) -> np.ndarray:
        if index in world_cache:
            return world_cache[index]
        matrix = np.eye(4)
        current = index
        while current is not None:
            matrix = trs_matrix(nodes[current]) @ matrix
            current = parent.get(current)
        world_cache[index] = matrix
        return matrix

    result: list[LowMesh] = []
    for index, node in enumerate(nodes):
        if "mesh" not in node:
            continue
        mesh = document["meshes"][node["mesh"]]
        for primitive in mesh.get("primitives", []):
            material_index = primitive.get("material")
            if material_index is None:
                continue
            if document["materials"][material_index]["name"] != material:
                continue
            positions = read_accessor(document, binary, primitive["attributes"]["POSITION"])
            result.append(
                LowMesh(
                    name=str(node.get("name") or ""),
                    matrix=world_matrix(index),
                    positions=positions,
                    bounds_min=positions.min(axis=0),
                    bounds_max=positions.max(axis=0),
                )
            )
    return result


ROTATIONS: list[np.ndarray] = []
for permutation in itertools.permutations(range(3)):
    for signs in itertools.product((-1.0, 1.0), repeat=3):
        rotation = np.zeros((3, 3))
        for row, (column, sign) in enumerate(zip(permutation, signs)):
            rotation[row, column] = sign
        ROTATIONS.append(rotation)


def sample_points(points: np.ndarray, limit: int = 420) -> np.ndarray:
    if len(points) <= limit:
        return points
    indices = np.linspace(0, len(points) - 1, limit).astype(int)
    return points[indices]


def refine_icp(
    source: np.ndarray,
    target: np.ndarray,
    initial: np.ndarray,
) -> np.ndarray:
    source_sample = sample_points(source, limit=700)
    target_sample = sample_points(target, limit=900)
    matrix = initial.copy()
    rotation = initial[:3, :3].copy()
    column_scale = np.linalg.norm(rotation, axis=0)
    rotation /= np.maximum(column_scale, 1e-12)
    initial_scale = float(np.cbrt(abs(np.linalg.det(matrix[:3, :3]))))
    scale = initial_scale
    rotated = (rotation @ source_sample.T).T
    for _ in range(10):
        transformed = scale * rotated + matrix[:3, 3]
        distances = transformed[:, None, :] - target_sample[None, :, :]
        squared = np.einsum("ijk,ijk->ij", distances, distances)
        nearest = np.argmin(squared, axis=1)
        closest = np.sqrt(np.min(squared, axis=1))
        keep_count = max(60, int(len(closest) * 0.86))
        keep = np.argsort(closest)[:keep_count]
        moving = rotated[keep]
        fixed = target_sample[nearest[keep]]
        moving_center = moving.mean(axis=0)
        fixed_center = fixed.mean(axis=0)
        centered_moving = moving - moving_center
        centered_fixed = fixed - fixed_center
        denominator = float(np.sum(centered_moving * centered_moving))
        candidate_scale = (
            float(np.sum(centered_moving * centered_fixed)) / max(denominator, 1e-12)
            if denominator > 0
            else scale
        )
        candidate_scale = min(
            max(candidate_scale, initial_scale * 0.72),
            initial_scale * 1.4,
        )
        translation = fixed_center - candidate_scale * moving_center
        change = abs(candidate_scale - scale) + float(np.linalg.norm(translation - matrix[:3, 3]))
        scale = candidate_scale
        matrix[:3, :3] = scale * rotation
        matrix[:3, 3] = translation
        if change < 1e-7:
            break
    return matrix


def fit_transform(high: HighMesh, low: LowMesh) -> np.ndarray:
    high_center = (high.bounds_min + high.bounds_max) / 2.0
    low_center = (low.bounds_min + low.bounds_max) / 2.0
    high_extent = high.bounds_max - high.bounds_min
    low_extent = low.bounds_max - low.bounds_min
    ranked: list[tuple[float, float, np.ndarray]] = []
    for rotation in ROTATIONS:
        rotated_extent = np.abs(rotation) @ high_extent
        valid = rotated_extent > 1e-8
        ratios = low_extent[valid] / rotated_extent[valid]
        if len(ratios) < 2:
            continue
        scale = float(np.median(ratios))
        predicted = rotated_extent * scale
        residual = float(
            np.mean(np.square((predicted - low_extent) / np.maximum(low_extent, 1e-8)))
        )
        ranked.append((residual, scale, rotation))
    ranked.sort(key=lambda item: item[0])

    high_positions = np.asarray(
        [
            [float(value) for value in line.split()[1:4]]
            for line in high.text.splitlines()
            if line.startswith("v ")
        ],
        dtype=float,
    )
    low_sample = sample_points(low.positions)
    high_sample = sample_points(high_positions)
    best: tuple[float, np.ndarray] | None = None
    for residual, scale, rotation in ranked[:8]:
        translation = low_center - scale * (rotation @ high_center)
        transformed = (scale * (rotation @ high_sample.T)).T + translation
        distances = transformed[:, None, :] - low_sample[None, :, :]
        nearest = np.min(np.einsum("ijk,ijk->ij", distances, distances), axis=1)
        chamfer = float(np.mean(np.sqrt(nearest)))
        normalized_chamfer = chamfer / max(float(np.linalg.norm(low_extent)), 1e-8)
        score = normalized_chamfer + residual * 0.12
        if best is None or score < best[0]:
            matrix = np.eye(4)
            matrix[:3, :3] = scale * rotation
            matrix[:3, 3] = translation
            best = (score, matrix)
    if best is None:
        raise ValueError(f"Unable to fit {high.name}")
    refined = refine_icp(high_positions, low.positions, best[1])
    return low.matrix @ refined


def transform_obj(text: str, matrix: np.ndarray) -> str:
    linear = matrix[:3, :3]
    normal_matrix = np.linalg.inv(linear).T
    determinant = np.linalg.det(linear)
    lines: list[str] = []
    for line in text.splitlines():
        if line.startswith("v "):
            point = np.asarray([float(value) for value in line.split()[1:4]] + [1.0])
            transformed = matrix @ point
            lines.append(
                f"v {transformed[0]:.8f} {transformed[1]:.8f} {transformed[2]:.8f}"
            )
        elif line.startswith("vn "):
            normal = np.asarray([float(value) for value in line.split()[1:4]])
            normal = normal_matrix @ normal
            normal /= max(float(np.linalg.norm(normal)), 1e-12)
            lines.append(f"vn {normal[0]:.8f} {normal[1]:.8f} {normal[2]:.8f}")
        elif line.startswith("vt "):
            lines.append(line)
        elif line.startswith("f "):
            tokens = line.split()[1:]
            if determinant < 0:
                tokens.reverse()
            lines.append("f " + " ".join(tokens))
    return "\n".join(lines) + "\n"


def remap_indices(text: str, vertex_offset: int, uv_offset: int, normal_offset: int) -> str:
    lines: list[str] = []
    for line in text.splitlines():
        if not line.startswith("f "):
            continue
        faces: list[str] = []
        for token in line.split()[1:]:
            parts = token.split("/")
            if parts[0]:
                parts[0] = str(int(parts[0]) + vertex_offset)
            if len(parts) > 1 and parts[1]:
                parts[1] = str(int(parts[1]) + uv_offset)
            if len(parts) > 2 and parts[2]:
                parts[2] = str(int(parts[2]) + normal_offset)
            faces.append("/".join(parts))
        lines.append("f " + " ".join(faces))
    return "\n".join(lines) + "\n"


def choose_low_mesh(high: HighMesh, candidates: list[LowMesh]) -> tuple[LowMesh, float] | None:
    if not candidates:
        return None
    if len(candidates) == 1:
        return candidates[0], 1.0
    high_key = normalized(high.name)
    ranked = sorted(
        candidates,
        key=lambda low: SequenceMatcher(None, high_key, normalized(low.name)).ratio(),
        reverse=True,
    )
    score = SequenceMatcher(None, high_key, normalized(ranked[0].name)).ratio()
    return ranked[0], score


def main() -> None:
    game = GameInstall(
        root=GAME_ROOT,
        data_dir=GAME_ROOT / "GTFO_Data",
        source="high-preview-generation",
    )
    catalog = load_catalog()
    assembly = load_assembly_catalog()
    selected_items = {
        value
        for value in os.environ.get("GTFO_PREVIEW_ITEMS", "").split(",")
        if value
    }
    failures: list[str] = []
    total_vertices = 0
    for item in catalog.values():
        if selected_items and item.id not in selected_items:
            continue
        if item.kind == "weapon":
            family = assembly["items"].get(item.id)
        elif item.kind == "attachment":
            family = ATTACHMENT_FAMILIES.get(item.id)
            if family is None:
                continue
        else:
            continue
        if not family:
            failures.append(f"{item.id}: no Forge family")
            continue
        document, binary = load_document(family)
        all_materials = [material["name"] for material in document.get("materials", [])]
        for part in item.parts:
            material = matching_material(part, all_materials)
            if material is None:
                failures.append(f"{item.id}/{part.id}: material not found")
                continue
            low_candidates = low_meshes(document, binary, material)
            high_meshes = high_meshes_for_part(game, part)
            if part.id == "flashlight" and part.material == "Flashlight_B":
                high_meshes.extend(
                    high_meshes_by_names(
                        game,
                        part.asset,
                        {"Gun_Light_Glass_B"},
                    )
                )
            selected: list[HighMesh] = []
            for high in high_meshes:
                if (item.id, high.name) in EXCLUDED_MESHES:
                    continue
                match = choose_low_mesh(high, low_candidates)
                if match is None:
                    continue
                low, score = match
                high_key = normalized(high.name)
                if high_key not in normalized(low.name) and normalized(low.name) not in high_key and score < 0.72:
                    continue
                selected.append(high)
            if not selected:
                failures.append(f"{item.id}/{part.id}: no high meshes matched")
                continue
            output_lines = ["# High-detail GTFO meshes assembled with Forge transforms\n"]
            vertex_offset = 0
            uv_offset = 0
            normal_offset = 0
            for high in selected:
                match = choose_low_mesh(high, low_candidates)
                if match is None:
                    continue
                low, _ = match
                matrix = MANUAL_MESH_MATRICES.get((item.id, high.name))
                if matrix is None:
                    matrix = fit_transform(high, low)
                post_translation = PART_POST_TRANSLATIONS.get((item.id, part.id))
                if post_translation is not None:
                    translation = np.eye(4)
                    translation[:3, 3] = post_translation
                    matrix = translation @ matrix
                transformed = transform_obj(high.text, matrix)
                body_lines = []
                counts = [0, 0, 0]
                for line in transformed.splitlines():
                    if line.startswith("v "):
                        counts[0] += 1
                    elif line.startswith("vt "):
                        counts[1] += 1
                    elif line.startswith("vn "):
                        counts[2] += 1
                for line in transformed.splitlines():
                    if line.startswith("v "):
                        output_lines.append(line)
                    elif line.startswith("vt "):
                        output_lines.append(line)
                    elif line.startswith("vn "):
                        output_lines.append(line)
                    elif line.startswith("f "):
                        body_lines.append(line)
                output_lines.extend(body_lines)
                remapped = remap_indices(
                    "\n".join(body_lines),
                    vertex_offset,
                    uv_offset,
                    normal_offset,
                )
                if remapped.rstrip():
                    output_lines[-len(body_lines) :] = remapped.rstrip("\n").split("\n")
                vertex_offset += counts[0]
                uv_offset += counts[1]
                normal_offset += counts[2]
                total_vertices += counts[0]
            target = OUTPUT_ROOT / item.id / f"{part.id}.obj.gz"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(
                gzip.compress(("\n".join(output_lines) + "\n").encode("utf-8"), compresslevel=9)
            )
    for failure in failures:
        print("FAILURE", failure)
    print(
        "high preview meshes:",
        sum(1 for path in OUTPUT_ROOT.glob("*/*.obj.gz")),
        "vertices:",
        total_vertices,
        "bytes:",
        sum(path.stat().st_size for path in OUTPUT_ROOT.glob("*/*.obj.gz")),
    )
    if failures:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
