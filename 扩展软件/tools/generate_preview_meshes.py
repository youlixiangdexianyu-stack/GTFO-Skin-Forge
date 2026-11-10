from __future__ import annotations

import gzip
import json
import subprocess
import struct
from pathlib import Path

import numpy as np

from gtfo_skin_forge.assembly import load_assembly_catalog
from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.models import PartSpec


WORKSPACE = Path(__file__).resolve().parents[1]
FORGE_ROOT = Path.home() / "AppData/Local/Temp/gtfo-forge-catalog-current"
OUTPUT_ROOT = WORKSPACE / "src/gtfo_skin_forge/data/preview_meshes"
DECODED_ROOT = FORGE_ROOT / "decoded"

COMPONENT_FORMATS = {
    5120: ("b", 1),
    5121: ("B", 1),
    5122: ("h", 2),
    5123: ("H", 2),
    5125: ("I", 4),
    5126: ("f", 4),
}
TYPE_COMPONENTS = {
    "SCALAR": 1,
    "VEC2": 2,
    "VEC3": 3,
    "VEC4": 4,
    "MAT2": 4,
    "MAT3": 9,
    "MAT4": 16,
}


def read_glb(path: Path) -> tuple[dict, bytes]:
    raw = path.read_bytes()
    offset = 12
    document = None
    binary = b""
    while offset < len(raw):
        chunk_length, chunk_type = struct.unpack_from("<II", raw, offset)
        offset += 8
        data = raw[offset : offset + chunk_length]
        offset += chunk_length
        if chunk_type == 0x4E4F534A:
            document = json.loads(data.decode("utf-8"))
        elif chunk_type == 0x004E4942:
            binary = data
    if document is None:
        raise ValueError(f"No JSON chunk in {path}")
    return document, binary


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


def read_accessor(document: dict, binary: bytes, index: int) -> np.ndarray:
    accessor = document["accessors"][index]
    view = document["bufferViews"][accessor["bufferView"]]
    component_format, component_size = COMPONENT_FORMATS[accessor["componentType"]]
    components = TYPE_COMPONENTS[accessor["type"]]
    count = int(accessor["count"])
    stride = int(view.get("byteStride", component_size * components))
    start = int(view.get("byteOffset", 0)) + int(accessor.get("byteOffset", 0))
    unpack_format = "<" + component_format * components
    values = [
        struct.unpack_from(unpack_format, binary, start + row * stride)
        for row in range(count)
    ]
    result = np.asarray(values, dtype=float)
    if accessor.get("normalized"):
        maximum = {
            5120: 127.0,
            5121: 255.0,
            5122: 32767.0,
            5123: 65535.0,
        }.get(accessor["componentType"])
        if maximum is not None:
            result = np.maximum(result / maximum, -1.0)
    return result


def load_document(family: str) -> tuple[dict, bytes]:
    source = FORGE_ROOT / "assets/models" / family / "low.glb"
    document, _ = read_glb(source)
    if "EXT_meshopt_compression" not in document.get("extensionsRequired", []):
        return read_glb(source)
    target = DECODED_ROOT / f"{family}.glb"
    if not target.is_file() or target.stat().st_mtime < source.stat().st_mtime:
        script = WORKSPACE / "tools/decode_meshopt.mjs"
        subprocess.run(
            ["node", str(script), str(source), str(target)],
            check=True,
            cwd=WORKSPACE,
        )
    return read_glb(target)


def matching_material(part: PartSpec, material_names: list[str]) -> str | None:
    expected = part.material.casefold()
    ranked: list[tuple[int, int, str]] = []
    for material in material_names:
        candidate = material.casefold()
        if candidate == expected:
            rank = 2
        elif expected.startswith(candidate) or candidate.startswith(expected):
            rank = 1
        else:
            continue
        ranked.append((rank, len(candidate), material))
    if not ranked:
        return None
    ranked.sort(reverse=True)
    return ranked[0][2]


def write_obj(document: dict, binary: bytes, material_names: list[str], target: Path) -> int:
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

    lines = ["# GTFO Forge preview geometry; textures are loaded from the local game\n"]
    vertex_base = 1
    emitted_vertices = 0
    for node_index, node in enumerate(nodes):
        if "mesh" not in node:
            continue
        matrix = world_matrix(node_index)
        linear = matrix[:3, :3]
        normal_matrix = np.linalg.inv(linear).T
        mesh = document["meshes"][node["mesh"]]
        for primitive in mesh.get("primitives", []):
            material_index = primitive.get("material")
            if material_index is None:
                continue
            if document["materials"][material_index]["name"] not in material_names:
                continue
            attributes = primitive["attributes"]
            positions = read_accessor(document, binary, attributes["POSITION"])
            transformed = (matrix @ np.column_stack((positions, np.ones(len(positions)))).T).T
            positions = transformed[:, :3]
            normals = None
            if "NORMAL" in attributes:
                normals = read_accessor(document, binary, attributes["NORMAL"])
                normals = (normal_matrix @ normals.T).T
                lengths = np.linalg.norm(normals, axis=1, keepdims=True)
                normals = normals / np.maximum(lengths, 1e-12)
            uvs = None
            if "TEXCOORD_0" in attributes:
                uvs = read_accessor(document, binary, attributes["TEXCOORD_0"])
            for index in range(len(positions)):
                x, y, z = positions[index]
                lines.append(f"v {x:.8f} {y:.8f} {z:.8f}\n")
                if uvs is not None:
                    u, v = uvs[index]
                    lines.append(f"vt {u:.8f} {v:.8f}\n")
                if normals is not None:
                    nx, ny, nz = normals[index]
                    lines.append(f"vn {nx:.8f} {ny:.8f} {nz:.8f}\n")
            if "indices" in primitive:
                indices = read_accessor(
                    document,
                    binary,
                    primitive["indices"],
                ).astype(int).reshape(-1)
            else:
                indices = np.arange(len(positions), dtype=int)
            for offset in range(0, len(indices), 3):
                face = indices[offset : offset + 3]
                if len(face) < 3:
                    continue
                a, b, c = (int(value) + vertex_base for value in face)
                if uvs is not None and normals is not None:
                    lines.append(f"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n")
                elif uvs is not None:
                    lines.append(f"f {a}/{a} {b}/{b} {c}/{c}\n")
                elif normals is not None:
                    lines.append(f"f {a}//{a} {b}//{b} {c}//{c}\n")
                else:
                    lines.append(f"f {a} {b} {c}\n")
            vertex_base += len(positions)
            emitted_vertices += len(positions)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(gzip.compress("".join(lines).encode("utf-8"), compresslevel=9))
    return emitted_vertices


def main() -> None:
    catalog = load_catalog()
    assembly = load_assembly_catalog()
    missing: list[str] = []
    total_vertices = 0
    for item in catalog.values():
        if item.kind != "weapon":
            continue
        family = assembly["items"].get(item.id)
        if not family:
            missing.append(f"{item.id}: no Forge family")
            continue
        document, binary = load_document(family)
        all_materials = [material["name"] for material in document.get("materials", [])]
        for part in item.parts:
            material = matching_material(part, all_materials)
            if material is None:
                missing.append(f"{item.id}/{part.id}: {part.material}")
                continue
            target = OUTPUT_ROOT / item.id / f"{part.id}.obj.gz"
            vertices = write_obj(document, binary, {material}, target)
            if vertices == 0:
                missing.append(f"{item.id}/{part.id}: empty geometry for {material}")
            total_vertices += vertices
    for item in missing:
        print("MISSING", item)
    print(
        "preview meshes:",
        sum(1 for path in OUTPUT_ROOT.glob("*/*.obj.gz")),
        "vertices:",
        total_vertices,
        "bytes:",
        sum(path.stat().st_size for path in OUTPUT_ROOT.glob("*/*.obj.gz")),
    )


if __name__ == "__main__":
    main()
