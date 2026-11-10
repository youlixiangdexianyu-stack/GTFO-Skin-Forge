from __future__ import annotations

import gzip
import re
import sys
from dataclasses import dataclass
from pathlib import Path

import UnityPy


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT / "src") not in sys.path:
    sys.path.insert(0, str(ROOT / "src"))

from gtfo_skin_forge.mesh_preview import _obj_bounds
from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.mesh_preview import extract_weapon_meshes
from gtfo_skin_forge.models import GameInstall


GAME_DATA = Path(r"D:\Steam\steamapps\common\GTFO\GTFO_Data")
OUTPUT = ROOT / "src/gtfo_skin_forge/data/preview_meshes"

FAMILIES = {
    "308": {
        "full": [(323, "Cartridge_308")],
        "shell": [(1, "Shell_339")],
    },
    "9mm": {
        "full": [(1, "shell_9_18mm1"), (563, "Revolver_Bullet_1")],
        "shell": [(1, "shell_9_18mm1")],
    },
    "shotgun": {
        "full": [(329, "Cartridge_Buckshot")],
        "shell": [(1, "Shotgun_Shell_Empty_1")],
    },
    "revolver": {
        "full": [(563, "Revolver_Shell_1"), (563, "Revolver_Bullet_1")],
        "shell": [(563, "Revolver_Shell_1")],
    },
    "45": {
        "full": [(1, "Shell_45_ACP"), (563, "Revolver_Bullet_1")],
        "shell": [(1, "Shell_45_ACP")],
    },
}

WEAPON_FAMILIES = {
    "assault_rifle": "308",
    "auto_pistol": "9mm",
    "battle_rifle": "308",
    "bullpup": "9mm",
    "burst_cannon": "308",
    "carbine": "9mm",
    "choke_shotgun": "shotgun",
    "combat_shotgun": "shotgun",
    "dmr": "308",
    "double_dmr": "308",
    "double_pistol": "9mm",
    "dragon_shotgun": "shotgun",
    "drekker_clr": "9mm",
    "heavy_assault": "308",
    "heavy_revolver": "revolver",
    "heavy_smg": "9mm",
    "hel_left": "revolver",
    "hel_shotgun": "shotgun",
    "helgun": "308",
    "helr": "308",
    "infrared_precision": "308",
    "large_caliber": "9mm",
    "machine_pistol": "9mm",
    "machinegun": "308",
    "machinegun_2": "308",
    "pdw": "9mm",
    "pump_shotgun": "shotgun",
    "semi_auto_rifle": "308",
    "short_shotgun_2": "shotgun",
    "slug_shotgun": "shotgun",
    "small_pistol": "9mm",
    "smg": "9mm",
    "sniper": "308",
}
ATTACHMENT_FAMILIES = {
    "ammo_9mm": "9mm",
    "ammo_308": "308",
    "ammo_12g": "shotgun",
    "ammo_45acp": "45",
    "ammo_revolver": "revolver",
}
TARGET_LENGTHS = {
    "308": 0.075,
    "9mm": 0.045,
    "shotgun": 0.070,
    "revolver": 0.060,
    "45": 0.045,
}


@dataclass
class ObjData:
    vertices: list[tuple[float, float, float]]
    texcoords: list[tuple[float, float]]
    normals: list[tuple[float, float, float]]
    faces: list[list[str]]


def read_mesh(asset_id: int, mesh_name: str) -> ObjData:
    asset = GAME_DATA / f"sharedassets{asset_id}.assets"
    resource = Path(f"{asset}.resS")
    env = UnityPy.load(str(asset), str(resource))
    try:
        for obj in env.objects:
            if obj.type.name == "Mesh" and obj.peek_name() == mesh_name:
                return parse_obj(obj.read().export())
        raise KeyError(f"{mesh_name} not found in sharedassets{asset_id}.assets")
    finally:
        files = getattr(env, "files", {})
        for item in files.values():
            dispose = getattr(item, "dispose", None)
            if callable(dispose):
                dispose()


def parse_obj(text: str) -> ObjData:
    vertices = []
    texcoords = []
    normals = []
    faces = []
    for line in text.splitlines():
        if line.startswith("v "):
            vertices.append(tuple(float(value) for value in line.split()[1:4]))
        elif line.startswith("vt "):
            texcoords.append(tuple(float(value) for value in line.split()[1:3]))
        elif line.startswith("vn "):
            normals.append(tuple(float(value) for value in line.split()[1:4]))
        elif line.startswith("f "):
            faces.append(line.split()[1:])
    return ObjData(vertices, texcoords, normals, faces)


def z_axis_to_y(data: ObjData) -> ObjData:
    vertices = [(x, z, -y) for x, y, z in data.vertices]
    normals = [(x, z, -y) for x, y, z in data.normals]
    return ObjData(vertices, data.texcoords, normals, data.faces)


def y_axis_to_x(data: ObjData) -> ObjData:
    vertices = [(y, -x, z) for x, y, z in data.vertices]
    normals = [(y, -x, z) for x, y, z in data.normals]
    return ObjData(vertices, data.texcoords, normals, data.faces)


def face_y_axis_toward_x(data: ObjData) -> ObjData:
    vertices = [(z, y, -x) for x, y, z in data.vertices]
    normals = [(z, y, -x) for x, y, z in data.normals]
    return ObjData(vertices, data.texcoords, normals, data.faces)


def scale_radial(data: ObjData, scale: float) -> ObjData:
    vertices = [(x * scale, y * scale, z * scale) for x, y, z in data.vertices]
    return ObjData(vertices, data.texcoords, data.normals, data.faces)


def translated(data: ObjData, offset: tuple[float, float, float]) -> ObjData:
    dx, dy, dz = offset
    vertices = [(x + dx, y + dy, z + dz) for x, y, z in data.vertices]
    return ObjData(vertices, data.texcoords, data.normals, data.faces)


def merge(items: list[tuple[str, ObjData]]) -> obj:
    output = [
        "# GTFO high-detail ammunition preview",
        "# Generated from native GTFO meshes",
    ]
    vertex_offset = texcoord_offset = normal_offset = 0
    for name, data in items:
        output.append(f"o {name}")
        output.extend(f"v {x:.8f} {y:.8f} {z:.8f}" for x, y, z in data.vertices)
        output.extend(f"vt {u:.8f} {v:.8f}" for u, v in data.texcoords)
        output.extend(f"vn {x:.8f} {y:.8f} {z:.8f}" for x, y, z in data.normals)
        for face in data.faces:
            converted = []
            for token in face:
                parts = token.split("/")
                values = [
                    int(parts[0]) + vertex_offset,
                    int(parts[1]) + texcoord_offset if len(parts) > 1 and parts[1] else None,
                    int(parts[2]) + normal_offset if len(parts) > 2 and parts[2] else None,
                ]
                converted.append(
                    "/".join("" if value is None else str(value) for value in values)
                )
            output.append("f " + " ".join(converted))
        vertex_offset += len(data.vertices)
        texcoord_offset += len(data.texcoords)
        normal_offset += len(data.normals)
    return ObjData.__new__(ObjData)


def append(source: list[str], data: ObjData, indices: list[int]) -> None:
    indices.extend(range(1, len(data.vertices) + 1))
    source.extend(f"v {x:.8f} {y:.8f} {z:.8f}" for x, y, z in data.vertices)


def obj_text(full: ObjData, shell: ObjData, scale: float, z_offset: float, y_base: float, x_base: float) -> str:
    vertices: list[tuple[float, float, float]] = []
    texcoords: list[tuple[float, float]] = []
    normals: list[tuple[float, float, float]] = []
    faces: list[tuple[str, list[str]]] = []

    def append_data(name: str, data: ObjData, cz: float) -> None:
        vertex_offset = len(vertices)
        texcoord_offset = len(texcoords)
        normal_offset = len(normals)
        vertices.extend(
            (
                x * scale + x_base,
                y * scale + y_base,
                z * scale + cz,
            )
            for x, y, z in data.vertices
        )
        texcoords.extend(data.texcoords)
        normals.extend(data.normals)
        converted_faces = []
        for face in data.faces:
            converted = []
            for token in face:
                parts = token.split("/")
                values = [
                    int(parts[0]) + vertex_offset,
                    int(parts[1]) + texcoord_offset if len(parts) > 1 and parts[1] else None,
                    int(parts[2]) + normal_offset if len(parts) > 2 and parts[2] else None,
                ]
                converted.append(
                    "/".join("" if value is None else str(value) for value in values)
                )
            converted_faces.append(" ".join(converted))
        faces.append((name, converted_faces))

    append_data("Ammo_Full", full, -z_offset)
    append_data("Ammo_Casing", shell, z_offset)
    output = [
        "# GTFO high-detail ammunition preview",
        "# Generated from native GTFO meshes",
    ]
    output.extend(f"v {x:.8f} {y:.8f} {z:.8f}" for x, y, z in vertices)
    output.extend(f"vt {u:.8f} {v:.8f}" for u, v in texcoords)
    output.extend(f"vn {x:.8f} {y:.8f} {z:.8f}" for x, y, z in normals)
    for name, object_faces in faces:
        output.append(f"g {name}")
        output.extend(f"f {face}" for face in object_faces)
    return "\n".join(output) + "\n"


def single_obj_text(
    name: str,
    data: ObjData,
    scale: float,
    x_base: float,
    y_base: float,
    z_base: float,
) -> str:
    output = [
        "# GTFO high-detail ammunition preview",
        "# Generated from native GTFO meshes",
        f"g {name}",
    ]
    output.extend(
        f"v {x * scale + x_base:.8f} {y * scale + y_base:.8f} {z * scale + z_base:.8f}"
        for x, y, z in data.vertices
    )
    output.extend(f"vt {u:.8f} {v:.8f}" for u, v in data.texcoords)
    output.extend(f"vn {x:.8f} {y:.8f} {z:.8f}" for x, y, z in data.normals)
    output.extend(f"f {' '.join(face)}" for face in data.faces)
    return "\n".join(output) + "\n"


def centered_obj_text(
    name: str,
    data: ObjData,
    scale: float,
    center_x: float,
    center_y: float,
    center_z: float,
) -> str:
    low, high = _obj_bounds_from_data(data)
    source_center_x = (low[0] + high[0]) / 2.0
    source_center_y = (low[1] + high[1]) / 2.0
    source_center_z = (low[2] + high[2]) / 2.0
    output = [
        "# GTFO high-detail ammunition preview",
        "# Generated from native GTFO meshes",
        f"g {name}",
    ]
    output.extend(
        f"v {(x - source_center_x) * scale + center_x:.8f} "
        f"{(y - source_center_y) * scale + center_y:.8f} "
        f"{(z - source_center_z) * scale + center_z:.8f}"
        for x, y, z in data.vertices
    )
    output.extend(f"vt {u:.8f} {v:.8f}" for u, v in data.texcoords)
    output.extend(f"vn {x:.8f} {y:.8f} {z:.8f}" for x, y, z in data.normals)
    output.extend(f"f {' '.join(face)}" for face in data.faces)
    return "\n".join(output) + "\n"


def prepared_family(family: str) -> tuple[ObjData, ObjData]:
    config = FAMILIES[family]
    if family == "308":
        full = read_mesh(*config["full"][0])
        shell = read_mesh(*config["shell"][0])
    elif family == "shotgun":
        full = read_mesh(*config["full"][0])
        shell = read_mesh(*config["shell"][0])
    elif family in {"9mm", "45"}:
        shell_name = config["shell"][0]
        shell = read_mesh(*shell_name)
        shell_bounds = _obj_bounds_from_data(shell)
        shell = translated(shell, (0.0, -shell_bounds[0][1], 0.0))
        shell_top = shell_bounds[1][1] - shell_bounds[0][1]
        bullet_source = read_mesh(*config["full"][1])
        bullet_source_bounds = _obj_bounds_from_data(bullet_source)
        shell_radius = (shell_bounds[1][0] - shell_bounds[0][0]) / 2.0
        bullet_radius = (
            bullet_source_bounds[1][0] - bullet_source_bounds[0][0]
        ) / 2.0
        bullet = z_axis_to_y(bullet_source)
        bullet = scale_radial(bullet, shell_radius / bullet_radius)
        bullet_bounds = _obj_bounds_from_data(bullet)
        bullet = translated(
            bullet,
            (0.0, shell_top - bullet_bounds[0][1], 0.0),
        )
        full = combine_objects(shell, bullet)
    else:
        raw_shell = z_axis_to_y(read_mesh(*config["shell"][0]))
        shell_bounds = _obj_bounds_from_data(raw_shell)
        shell = translated(raw_shell, (0.0, -shell_bounds[0][1], 0.0))
        shell_top = shell_bounds[1][1] - shell_bounds[0][1]
        bullet = z_axis_to_y(read_mesh(*config["full"][1]))
        bullet_bounds = _obj_bounds_from_data(bullet)
        bullet = translated(
            bullet,
            (0.0, shell_top - bullet_bounds[0][1], 0.0),
        )
        full = combine_objects(shell, bullet)
    return full, shell


def _obj_bounds_from_data(data: ObjData):
    xs = [value[0] for value in data.vertices]
    ys = [value[1] for value in data.vertices]
    zs = [value[2] for value in data.vertices]
    return (min(xs), min(ys), min(zs)), (max(xs), max(ys), max(zs))


def combine_objects(*objects: ObjData) -> ObjData:
    vertices = []
    texcoords = []
    normals = []
    faces = []
    vo = to = no = 0
    for data in objects:
        vertices.extend(data.vertices)
        texcoords.extend(data.texcoords)
        normals.extend(data.normals)
        for face in data.faces:
            converted = []
            for token in face:
                parts = token.split("/")
                values = [
                    int(parts[0]) + vo,
                    int(parts[1]) + to if len(parts) > 1 and parts[1] else None,
                    int(parts[2]) + no if len(parts) > 2 and parts[2] else None,
                ]
                converted.append(
                    "/".join("" if value is None else str(value) for value in values)
                )
            faces.append(converted)
        vo += len(data.vertices)
        to += len(data.texcoords)
        no += len(data.normals)
    return ObjData(vertices, texcoords, normals, faces)


def weapon_bounds(item_id: str) -> tuple[tuple[float, float, float], tuple[float, float, float]]:
    game_root = GAME_DATA.parent
    game = GameInstall(
        root=game_root,
        data_dir=GAME_DATA,
        source="generator",
    )
    _, bounds = extract_weapon_meshes(
        game,
        load_catalog()[item_id],
        ROOT / "artifacts/ammo-generator-cache",
    )
    return tuple(bounds[0]), tuple(bounds[1])


def write_for_weapon(item_id: str, family: str) -> None:
    full, shell = prepared_family(family)
    full_bounds = _obj_bounds_from_data(full)
    shell_bounds = _obj_bounds_from_data(shell)
    speedloader = None
    speedloader_bounds = None
    if family == "revolver":
        speedloader = face_y_axis_toward_x(
            read_mesh(563, "Revolver_Speedloader_1")
        )
        speedloader_bounds = _obj_bounds_from_data(speedloader)
    max_height = max(
        full_bounds[1][1] - full_bounds[0][1],
        shell_bounds[1][1] - shell_bounds[0][1],
        (
            speedloader_bounds[1][1] - speedloader_bounds[0][1]
            if speedloader_bounds
            else 0.0
        ),
    )
    scale = TARGET_LENGTHS[family] / max(max_height, 1e-6)
    low, high = weapon_bounds(item_id)
    center_z = (low[2] + high[2]) / 2.0
    center_x = (low[0] + high[0]) / 2.0
    y_base = low[1] - 0.035 - max_height * scale
    full_half_width = (full_bounds[1][2] - full_bounds[0][2]) * scale * 0.5
    shell_half_width = (shell_bounds[1][2] - shell_bounds[0][2]) * scale * 0.5
    gap = 0.009
    if speedloader and speedloader_bounds:
        speed_half_width = (
            speedloader_bounds[1][2] - speedloader_bounds[0][2]
        ) * scale * 0.5
        shell_center_z = center_z + speed_half_width + shell_half_width + gap
        bullet_center_z = center_z - speed_half_width - full_half_width - gap
    else:
        shell_center_z = center_z + full_half_width + shell_half_width + gap
        bullet_center_z = center_z - full_half_width - shell_half_width - gap
    bullet_text = single_obj_text(
        "Ammo_Bullet",
        full,
        scale,
        center_x,
        y_base - full_bounds[0][1] * scale,
        bullet_center_z,
    )
    shell_text = single_obj_text(
        "Ammo_Casing",
        shell,
        scale,
        center_x,
        y_base - shell_bounds[0][1] * scale,
        shell_center_z,
    )
    target_dir = OUTPUT / item_id
    target_dir.mkdir(parents=True, exist_ok=True)
    for filename, text in (
        ("ammo_bullet.obj.gz", bullet_text),
        ("ammo_shell.obj.gz", shell_text),
    ):
        (target_dir / filename).write_bytes(
            gzip.compress(text.encode("utf-8"), compresslevel=9)
        )
    if speedloader and speedloader_bounds:
        speedloader_text = single_obj_text(
            "Ammo_Speedloader",
            speedloader,
            scale,
            center_x,
            y_base - speedloader_bounds[0][1] * scale,
            center_z,
        )
        (target_dir / "ammo_speedloader.obj.gz").write_bytes(
            gzip.compress(speedloader_text.encode("utf-8"), compresslevel=9)
        )
    else:
        (target_dir / "ammo_speedloader.obj.gz").unlink(missing_ok=True)
    (target_dir / "ammo.obj.gz").unlink(missing_ok=True)
    print(f"{item_id}: {family} -> {target_dir}")


def write_centered_family(item_id: str, family: str) -> None:
    full, shell = prepared_family(family)
    full_bounds = _obj_bounds_from_data(full)
    shell_bounds = _obj_bounds_from_data(shell)
    speedloader = None
    speedloader_bounds = None
    if family == "revolver":
        speedloader = face_y_axis_toward_x(
            read_mesh(563, "Revolver_Speedloader_1")
        )
        speedloader_bounds = _obj_bounds_from_data(speedloader)
    max_height = max(
        full_bounds[1][1] - full_bounds[0][1],
        shell_bounds[1][1] - shell_bounds[0][1],
        (
            speedloader_bounds[1][1] - speedloader_bounds[0][1]
            if speedloader_bounds
            else 0.0
        ),
    )
    scale = TARGET_LENGTHS[family] / max(max_height, 1e-6)
    y_base = -max_height * scale / 2.0
    full_half_width = (full_bounds[1][2] - full_bounds[0][2]) * scale * 0.5
    shell_half_width = (shell_bounds[1][2] - shell_bounds[0][2]) * scale * 0.5
    gap = 0.009
    if speedloader and speedloader_bounds:
        speed_half_width = (
            speedloader_bounds[1][2] - speedloader_bounds[0][2]
        ) * scale * 0.5
        shell_center_z = speed_half_width + shell_half_width + gap
        bullet_center_z = -speed_half_width - full_half_width - gap
    else:
        shell_center_z = full_half_width + shell_half_width + gap
        bullet_center_z = -full_half_width - shell_half_width - gap
    target_dir = OUTPUT / item_id
    target_dir.mkdir(parents=True, exist_ok=True)
    (target_dir / "ammo_bullet.obj.gz").write_bytes(
        gzip.compress(
            single_obj_text(
                "Ammo_Bullet",
                full,
                scale,
                0.0,
                y_base - full_bounds[0][1] * scale,
                bullet_center_z,
            ).encode("utf-8"),
            compresslevel=9,
        )
    )
    (target_dir / "ammo_shell.obj.gz").write_bytes(
        gzip.compress(
            single_obj_text(
                "Ammo_Casing",
                shell,
                scale,
                0.0,
                y_base - shell_bounds[0][1] * scale,
                shell_center_z,
            ).encode("utf-8"),
            compresslevel=9,
        )
    )
    if speedloader and speedloader_bounds:
        (target_dir / "ammo_speedloader.obj.gz").write_bytes(
            gzip.compress(
                single_obj_text(
                    "Ammo_Speedloader",
                    speedloader,
                    scale,
                    0.0,
                    y_base - speedloader_bounds[0][1] * scale,
                    0.0,
                ).encode("utf-8"),
                compresslevel=9,
            )
        )
    else:
        (target_dir / "ammo_speedloader.obj.gz").unlink(missing_ok=True)
    print(f"{item_id}: {family} -> {target_dir}")


def main() -> None:
    for item_id, family in WEAPON_FAMILIES.items():
        write_for_weapon(item_id, family)
    for item_id, family in ATTACHMENT_FAMILIES.items():
        write_centered_family(item_id, family)


if __name__ == "__main__":
    main()
