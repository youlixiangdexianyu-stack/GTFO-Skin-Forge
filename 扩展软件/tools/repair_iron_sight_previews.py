from __future__ import annotations

import gzip
import sys
from pathlib import Path

import numpy as np

WORKSPACE = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(WORKSPACE / "src"))

from gtfo_skin_forge.assets import close_unity_env, load_unity_env, res_path


GAME_ROOT = Path("D:/Steam/steamapps/common/GTFO")
PREVIEW_ROOT = WORKSPACE / "src/gtfo_skin_forge/data/preview_meshes"

# These are the meshes used by the corresponding official weapon prefabs.
# The other F/B mesh in the same asset is an unused alternate and must not be
# rendered on top of the selected sight.
ATTACHMENT_SIGHTS = {
    "iron_sights_4": (307, "IronSights_4_F"),
    "iron_sights_8": (317, "IronSights_8_B"),
    "iron_sights_9": (306, "IronSights_9_B"),
    "iron_sights_10": (306, "IronSight_10"),
}

WEAPON_SIGHT_BLOCKS = {
    ("pump_shotgun", "sight"): (
        317,
        ("IronSights_8_B", "IronSights_8_F"),
        "IronSights_8_B",
    ),
    ("combat_shotgun", "sight"): (
        306,
        ("IronSights_9_F", "IronSights_9_B"),
        "IronSights_9_B",
    ),
}

TRANSFORMED_ATTACHMENT_MESHES = {
    "sight_glass_opaque": (303, "OpaqueSightGlass", "Quad"),
}


def _asset_meshes(asset: int, names: tuple[str, ...]) -> dict[str, str]:
    data_dir = GAME_ROOT / "GTFO_Data"
    env = load_unity_env(
        data_dir / f"sharedassets{asset}.assets",
        res_path(data_dir, asset),
    )
    try:
        result: dict[str, str] = {}
        for reader in env.objects:
            if reader.type.name != "Mesh" or reader.peek_name() not in names:
                continue
            result[reader.peek_name()] = reader.read().export()
        return result
    finally:
        close_unity_env(env)


def _obj_counts(text: str) -> tuple[int, int, int, int]:
    return (
        sum(line.startswith("v ") for line in text.splitlines()),
        sum(line.startswith("vt ") for line in text.splitlines()),
        sum(line.startswith("vn ") for line in text.splitlines()),
        sum(line.startswith("f ") for line in text.splitlines()),
    )


def _write_attachment_previews() -> None:
    for item_id, (asset, mesh_name) in ATTACHMENT_SIGHTS.items():
        meshes = _asset_meshes(asset, (mesh_name,))
        if mesh_name not in meshes:
            raise RuntimeError(f"{asset}: missing mesh {mesh_name}")
        text = meshes[mesh_name]
        if _obj_counts(text)[3] == 0:
            raise RuntimeError(f"{mesh_name}: no exported faces")
        target = PREVIEW_ROOT / item_id / "main.obj.gz"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(
            gzip.compress(
                (
                    "# Official GTFO mesh selected from the weapon prefab\n"
                    + text.rstrip()
                    + "\n"
                ).encode("utf-8"),
                compresslevel=9,
            )
        )
        print("attachment", item_id, mesh_name, _obj_counts(text))


def _selected_block(
    path: Path,
    mesh_order: tuple[str, ...],
    selected_name: str,
    source_meshes: dict[str, str],
) -> str:
    text = gzip.decompress(path.read_bytes()).decode("utf-8")
    vertices: list[str] = []
    texcoords: list[str] = []
    normals: list[str] = []
    faces: list[tuple[int, list[str]]] = []
    for line in text.splitlines():
        if line.startswith("v "):
            vertices.append(line)
        elif line.startswith("vt "):
            texcoords.append(line)
        elif line.startswith("vn "):
            normals.append(line)
        elif line.startswith("f "):
            faces.append((len(faces), line))

    counts = [_obj_counts(source_meshes[name]) for name in mesh_order]
    vertex_counts = [count[0] for count in counts]
    texcoord_counts = [count[1] for count in counts]
    normal_counts = [count[2] for count in counts]
    selected_index = mesh_order.index(selected_name)
    if len(vertices) == vertex_counts[selected_index]:
        return text.rstrip() + "\n"
    if sum(vertex_counts) != len(vertices):
        raise RuntimeError(
            f"{path}: vertex count mismatch, {sum(vertex_counts)} != {len(vertices)}"
        )
    if sum(texcoord_counts) != len(texcoords):
        raise RuntimeError(
            f"{path}: UV count mismatch, {sum(texcoord_counts)} != {len(texcoords)}"
        )
    if sum(normal_counts) != len(normals):
        raise RuntimeError(
            f"{path}: normal count mismatch, {sum(normal_counts)} != {len(normals)}"
        )

    vertex_starts = np.cumsum([0, *vertex_counts[:-1]]).tolist()
    texcoord_starts = np.cumsum([0, *texcoord_counts[:-1]]).tolist()
    normal_starts = np.cumsum([0, *normal_counts[:-1]]).tolist()
    start = vertex_starts[selected_index]
    end = start + vertex_counts[selected_index]

    output_faces: list[str] = []
    for _, line in faces:
        tokens = line.split()[1:]
        indexes = [int(token.split("/")[0]) for token in tokens]
        if not all(start < value <= end for value in indexes):
            continue
        converted: list[str] = []
        for token in tokens:
            parts = token.split("/")
            parts[0] = str(int(parts[0]) - start)
            if len(parts) > 1 and parts[1]:
                parts[1] = str(int(parts[1]) - texcoord_starts[selected_index])
            if len(parts) > 2 and parts[2]:
                parts[2] = str(int(parts[2]) - normal_starts[selected_index])
            converted.append("/".join(parts))
        output_faces.append("f " + " ".join(converted))

    if not output_faces:
        raise RuntimeError(f"{path}: selected mesh has no faces")
    output = [
        "# High-detail GTFO mesh with the unused sight alternate removed",
        *vertices[start:end],
        *texcoords[
            texcoord_starts[selected_index] :
            texcoord_starts[selected_index] + texcoord_counts[selected_index]
        ],
        *normals[
            normal_starts[selected_index] :
            normal_starts[selected_index] + normal_counts[selected_index]
        ],
        *output_faces,
    ]
    return "\n".join(output) + "\n"


def _write_weapon_previews() -> None:
    for (item_id, part_id), (asset, mesh_order, selected_name) in (
        WEAPON_SIGHT_BLOCKS.items()
    ):
        source_meshes = _asset_meshes(asset, mesh_order)
        missing = set(mesh_order) - source_meshes.keys()
        if missing:
            raise RuntimeError(f"{asset}: missing meshes {sorted(missing)}")
        target = PREVIEW_ROOT / item_id / f"{part_id}.obj.gz"
        output = _selected_block(target, mesh_order, selected_name, source_meshes)
        target.write_bytes(gzip.compress(output.encode("utf-8"), compresslevel=9))
        print("weapon", item_id, part_id, selected_name, _obj_counts(output))


def _transform_matrix(transform) -> np.ndarray:
    position = np.asarray(
        [
            transform.m_LocalPosition.x,
            transform.m_LocalPosition.y,
            transform.m_LocalPosition.z,
        ],
        dtype=float,
    )
    quaternion = transform.m_LocalRotation
    scale = np.asarray(
        [
            transform.m_LocalScale.x,
            transform.m_LocalScale.y,
            transform.m_LocalScale.z,
        ],
        dtype=float,
    )
    qx, qy, qz, qw = (
        float(quaternion.x),
        float(quaternion.y),
        float(quaternion.z),
        float(quaternion.w),
    )
    rotation = np.asarray(
        [
            [
                1 - 2 * (qy * qy + qz * qz),
                2 * (qx * qy - qz * qw),
                2 * (qx * qz + qy * qw),
            ],
            [
                2 * (qx * qy + qz * qw),
                1 - 2 * (qx * qx + qz * qz),
                2 * (qy * qz - qx * qw),
            ],
            [
                2 * (qx * qz - qy * qw),
                2 * (qy * qz + qx * qw),
                1 - 2 * (qx * qx + qy * qy),
            ],
        ],
        dtype=float,
    )
    matrix = np.eye(4)
    matrix[:3, :3] = rotation @ np.diag(scale)
    matrix[:3, 3] = position
    return matrix


def _transformed_obj(text: str, matrix: np.ndarray) -> str:
    normal_matrix = np.linalg.inv(matrix[:3, :3]).T
    reverse = np.linalg.det(matrix[:3, :3]) < 0
    output: list[str] = []
    for line in text.splitlines():
        if line.startswith("v "):
            point = np.asarray(
                [float(value) for value in line.split()[1:4]] + [1.0],
                dtype=float,
            )
            transformed = matrix @ point
            output.append(
                f"v {transformed[0]:.8f} {transformed[1]:.8f} "
                f"{transformed[2]:.8f}"
            )
        elif line.startswith("vn "):
            normal = np.asarray(
                [float(value) for value in line.split()[1:4]],
                dtype=float,
            )
            normal = normal_matrix @ normal
            normal /= max(float(np.linalg.norm(normal)), 1e-12)
            output.append(
                f"vn {normal[0]:.8f} {normal[1]:.8f} {normal[2]:.8f}"
            )
        elif line.startswith("f "):
            tokens = line.split()[1:]
            if reverse:
                tokens.reverse()
            output.append("f " + " ".join(tokens))
        elif line.startswith("vt "):
            output.append(line)
    return "\n".join(output) + "\n"


def _write_transformed_attachment_previews() -> None:
    for item_id, (asset, material, mesh_name) in (
        TRANSFORMED_ATTACHMENT_MESHES.items()
    ):
        data_dir = GAME_ROOT / "GTFO_Data"
        env = load_unity_env(
            data_dir / f"sharedassets{asset}.assets",
            res_path(data_dir, asset),
        )
        try:
            objects = {obj.path_id: obj for obj in env.objects}
            material_ids = {
                obj.path_id
                for obj in env.objects
                if obj.type.name == "Material" and obj.peek_name() == material
            }
            game_object_id = None
            for obj in env.objects:
                if obj.type.name != "MeshRenderer":
                    continue
                renderer = obj.read()
                references = getattr(renderer, "m_Materials", []) or []
                if any(int(reference.m_PathID) in material_ids for reference in references):
                    game_object_id = int(renderer.m_GameObject.m_PathID)
                    break
            if game_object_id is None:
                raise RuntimeError(f"{asset}: no renderer uses {material}")
            mesh_pointer = None
            for obj in env.objects:
                if obj.type.name != "MeshFilter":
                    continue
                mesh_filter = obj.read()
                game_object = getattr(mesh_filter, "m_GameObject", None)
                if (
                    game_object is not None
                    and int(game_object.m_PathID) == game_object_id
                ):
                    mesh_pointer = mesh_filter.m_Mesh
                    break
            if mesh_pointer is None:
                raise RuntimeError(f"{asset}: no MeshFilter for {material}")
            mesh = mesh_pointer.read()
            if mesh.m_Name != mesh_name:
                raise RuntimeError(
                    f"{asset}: expected {mesh_name}, found {mesh.m_Name}"
                )

            transforms_by_game_object = {}
            for obj in env.objects:
                if obj.type.name != "Transform":
                    continue
                transform = obj.read()
                game_object = getattr(transform, "m_GameObject", None)
                if game_object is not None:
                    transforms_by_game_object[int(game_object.m_PathID)] = transform
            transform = transforms_by_game_object[game_object_id]
            world_matrix = np.eye(4)
            current = transform
            while current is not None:
                world_matrix = _transform_matrix(current) @ world_matrix
                father = getattr(current, "m_Father", None)
                current = father.read() if father else None

            output = _transformed_obj(mesh.export(), world_matrix)
            target = PREVIEW_ROOT / item_id / "main.obj.gz"
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(
                gzip.compress(
                    (
                        "# Official GTFO mesh with its renderer transform baked\n"
                        + output
                    ).encode("utf-8"),
                    compresslevel=9,
                )
            )
            print("transformed attachment", item_id, mesh_name, _obj_counts(output))
        finally:
            close_unity_env(env)


def main() -> None:
    _write_attachment_previews()
    _write_weapon_previews()
    _write_transformed_attachment_previews()


if __name__ == "__main__":
    main()
