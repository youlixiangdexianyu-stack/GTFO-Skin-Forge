from __future__ import annotations

import json
import struct
from pathlib import Path

import numpy as np

from gtfo_skin_forge.assembly import load_assembly_catalog, resolve_mesh_offset
from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.mesh_preview import extract_weapon_meshes
from gtfo_skin_forge.models import GameInstall


GAME_ROOT = Path("D:/Steam/steamapps/common/GTFO")
FORGE_ROOT = Path.home() / "AppData/Local/Temp/gtfo-forge-catalog-current"
CACHE_ROOT = Path(__file__).resolve().parents[1] / "artifacts/assembly-validation-cache"


def read_glb(path: Path) -> dict:
    raw = path.read_bytes()
    offset = 12
    while offset < len(raw):
        chunk_length, chunk_type = struct.unpack_from("<II", raw, offset)
        offset += 8
        data = raw[offset : offset + chunk_length]
        offset += chunk_length
        if chunk_type == 0x4E4F534A:
            return json.loads(data.decode("utf-8"))
    raise ValueError(f"No JSON chunk in {path}")


def trs_matrix(node: dict) -> np.ndarray:
    position = np.asarray(node.get("translation", [0, 0, 0]), dtype=float)
    qx, qy, qz, qw = (float(v) for v in node.get("rotation", [0, 0, 0, 1]))
    scale = np.asarray(node.get("scale", [1, 1, 1]), dtype=float)
    rotation = np.asarray(
        [
            [1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
            [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
            [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)],
        ],
        dtype=float,
    )
    matrix = np.eye(4)
    matrix[:3, :3] = rotation * scale[None, :]
    matrix[:3, 3] = position
    return matrix


def official_mesh_centers(family: str) -> dict[str, list[np.ndarray]]:
    document = read_glb(FORGE_ROOT / "assets/models" / family / "low.glb")
    nodes = document["nodes"]
    parent = {
        child: index
        for index, node in enumerate(nodes)
        for child in node.get("children", [])
    }

    def world_matrix(index: int) -> np.ndarray:
        matrix = np.eye(4)
        current = index
        while current is not None:
            matrix = trs_matrix(nodes[current]) @ matrix
            current = parent.get(current)
        return matrix

    result: dict[str, list[np.ndarray]] = {}
    for index, node in enumerate(nodes):
        if "mesh" not in node:
            continue
        mesh = document["meshes"][node["mesh"]]
        for primitive in mesh["primitives"]:
            if "material" not in primitive:
                continue
            material = document["materials"][primitive["material"]]["name"]
            accessor = document["accessors"][primitive["attributes"]["POSITION"]]
            low = np.asarray(accessor["min"], dtype=float)
            high = np.asarray(accessor["max"], dtype=float)
            corners = np.asarray(
                [
                    [x, y, z, 1.0]
                    for x in (low[0], high[0])
                    for y in (low[1], high[1])
                    for z in (low[2], high[2])
                ]
            )
            transformed = (world_matrix(index) @ corners.T).T[:, :3]
            result.setdefault(material, []).append(
                (transformed.min(axis=0) + transformed.max(axis=0)) / 2.0
            )
    return result


def main() -> None:
    game = GameInstall(
        root=GAME_ROOT,
        data_dir=GAME_ROOT / "GTFO_Data",
        source="assembly-validation",
    )
    catalog = load_catalog()
    assembly = load_assembly_catalog()
    rows: list[tuple[float, str, str, str]] = []
    for item in catalog.values():
        if item.kind != "weapon":
            continue
        family = assembly["items"][item.id]
        centers = official_mesh_centers(family)
        meshes, _ = extract_weapon_meshes(game, item, CACHE_ROOT)
        for mesh in meshes:
            offset = np.asarray(
                resolve_mesh_offset(item, next(p for p in item.parts if p.id == mesh.part_id), mesh.mesh_name, assembly)
            )
            center = (np.asarray(mesh.bounds_min) + np.asarray(mesh.bounds_max)) / 2.0 + offset
            candidates = centers.get(mesh.material, [])
            distance = (
                min(float(np.linalg.norm(center - candidate)) for candidate in candidates)
                if candidates
                else float("inf")
            )
            rows.append((distance, item.id, mesh.part_id, mesh.mesh_name))
    rows.sort(reverse=True)
    for distance, item_id, part_id, mesh_name in rows:
        print(f"{distance:8.4f}  {item_id:22} {part_id:10} {mesh_name}")


if __name__ == "__main__":
    main()
