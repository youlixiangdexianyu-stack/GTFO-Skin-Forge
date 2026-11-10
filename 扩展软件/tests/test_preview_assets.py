from __future__ import annotations

import gzip
from pathlib import Path
from types import SimpleNamespace

import pytest
from PIL import Image

from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.mesh_preview import (
    _emissive_preview_texture,
    _mesh_names_for_part,
    _obj_bounds,
    extract_weapon_meshes,
    prepare_preview_materials,
)
from gtfo_skin_forge.models import (
    CatalogItem,
    EmissiveSettings,
    GameInstall,
    SkinProject,
)


def test_every_weapon_has_compressed_forge_preview_geometry():
    root = Path(__file__).parents[1] / "src/gtfo_skin_forge/data/preview_meshes"
    catalog = load_catalog()

    for item in catalog.values():
        if item.kind != "weapon":
            continue
        for part in item.parts:
            path = root / item.id / f"{part.id}.obj.gz"
            assert path.is_file(), path
            assert path.stat().st_size > 32, path

    sample = root / "hel_shotgun/front.obj.gz"
    header = gzip.decompress(sample.read_bytes())[:120].decode("utf-8")
    assert "High-detail GTFO meshes" in header


def test_gun_light_attachments_have_preview_geometry():
    root = Path(__file__).parents[1] / "src/gtfo_skin_forge/data/preview_meshes"
    catalog = load_catalog()

    for item_id in (
        "gun_light_a",
        "gun_light_b",
        "gun_light_c",
        "gun_light_d",
        "peq_1_e",
        "peq_1_lens",
    ):
        path = root / item_id / "main.obj.gz"
        assert path.is_file(), path
        assert gzip.decompress(path.read_bytes()).startswith(
            b"# High-detail GTFO meshes"
        )


def test_iron_sight_attachments_use_one_official_mesh(tmp_path):
    root = Path(__file__).parents[1] / "src/gtfo_skin_forge/data/preview_meshes"
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    expected = {
        "iron_sights_4": (3051, 2534),
        "iron_sights_8": (9850, 6950),
        "iron_sights_9": (6495, 5362),
        "iron_sights_10": (8013, 7748),
    }

    for item_id, (expected_vertices, expected_faces) in expected.items():
        item = load_catalog()[item_id]
        meshes, bounds = extract_weapon_meshes(game, item, tmp_path / "cache")
        assert len(meshes) == 1, item_id
        text = meshes[0].obj_path.read_text(encoding="utf-8")
        assert sum(line.startswith("v ") for line in text.splitlines()) == (
            expected_vertices
        )
        assert sum(line.startswith("f ") for line in text.splitlines()) == expected_faces
        assert bounds[1][0] > bounds[0][0]

    assert (root / "iron_sights_10/main.obj.gz").is_file()


def test_weapon_iron_sight_previews_do_not_merge_f_b_alternates(tmp_path):
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    expected = {
        "pump_shotgun": (9850, 6950, "IronSights_8"),
        "combat_shotgun": (6495, 5362, "IronSights_9"),
        "dragon_shotgun": (3051, 2534, "IronSights_4_F"),
        "choke_shotgun": (8013, 7748, "IronSights_10"),
    }
    for item_id, (vertex_count, face_count, material_name) in expected.items():
        item = load_catalog()[item_id]
        meshes, _ = extract_weapon_meshes(
            game,
            item,
            tmp_path / "cache",
            enabled_parts={"sight"},
        )
        sight_meshes = [mesh for mesh in meshes if mesh.part_id == "sight"]
        assert len(sight_meshes) == 1, item_id
        assert sight_meshes[0].material == material_name
        text = sight_meshes[0].obj_path.read_text(encoding="utf-8")
        assert sum(line.startswith("v ") for line in text.splitlines()) == vertex_count
        assert sum(line.startswith("f ") for line in text.splitlines()) == face_count


def test_iron_sight_name_matching_prefers_the_official_variant():
    class _Reader:
        def __init__(self, name: str):
            self.name = name
            self.type = type("Type", (), {"name": "Mesh"})()

        def peek_name(self):
            return self.name

    env = type(
        "Env",
        (),
        {
            "objects": [
                _Reader("IronSights_9_F"),
                _Reader("IronSights_9_B"),
                _Reader("IronSight_10"),
            ]
        },
    )()
    parts = {
        part.material: part
        for item in load_catalog().values()
        for part in item.parts
    }

    assert [
        reader.peek_name()
        for reader in _mesh_names_for_part(env, parts["IronSights_9"])
    ] == ["IronSights_9_B"]
    assert [
        reader.peek_name()
        for reader in _mesh_names_for_part(env, parts["IronSights_10"])
    ] == ["IronSight_10"]


def test_attachment_mesh_selection_uses_renderer_material_binding():
    class _Reference:
        def __init__(self, path_id: int):
            self.m_PathID = path_id
            self.m_FileID = 0

    class _Reader:
        def __init__(
            self,
            path_id: int,
            type_name: str,
            name: str,
            value=None,
        ):
            self.path_id = path_id
            self.type = SimpleNamespace(name=type_name)
            self.name = name
            self.value = value

        def peek_name(self):
            return self.name

        def read(self):
            return self.value

    objects = [
        _Reader(3, "Material", "Sight_20"),
        _Reader(4, "Material", "OpaqueSightGlass"),
        _Reader(7, "Material", "Sight_14"),
        _Reader(11, "Mesh", "Sight_20"),
        _Reader(12, "Mesh", "Sight_20_Optic_1"),
        _Reader(21, "Mesh", "Sight_14"),
        _Reader(22, "Mesh", "Sight_14"),
        _Reader(10210, "Mesh", "Quad"),
        _Reader(
            27,
            "MeshFilter",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(16),
                m_Mesh=_Reference(11),
            ),
        ),
        _Reader(
            61,
            "MeshFilter",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(35),
                m_Mesh=_Reference(10210),
            ),
        ),
        _Reader(
            26,
            "MeshRenderer",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(16),
                m_Materials=[_Reference(3)],
            ),
        ),
        _Reader(
            58,
            "MeshRenderer",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(35),
                m_Materials=[_Reference(4)],
            ),
        ),
        _Reader(
            70,
            "MeshFilter",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(80),
                m_Mesh=_Reference(21),
            ),
        ),
        _Reader(
            71,
            "MeshFilter",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(81),
                m_Mesh=_Reference(22),
            ),
        ),
        _Reader(
            72,
            "MeshRenderer",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(80),
                m_Materials=[_Reference(7)],
            ),
        ),
        _Reader(
            73,
            "MeshRenderer",
            "",
            SimpleNamespace(
                m_GameObject=_Reference(81),
                m_Materials=[_Reference(7)],
            ),
        ),
    ]
    env = SimpleNamespace(objects=objects)
    catalog = load_catalog()
    sight_part = catalog["sight_20"].parts[0]
    glass_part = catalog["sight_glass_opaque"].parts[0]

    assert [
        reader.peek_name()
        for reader in _mesh_names_for_part(env, sight_part)
    ] == ["Sight_20"]
    assert [
        reader.peek_name()
        for reader in _mesh_names_for_part(env, glass_part)
    ] == ["Quad"]
    assert len(
        _mesh_names_for_part(env, catalog["sight_14"].parts[0])
    ) == 1


def test_opaque_sight_glass_preview_uses_renderer_scale(tmp_path):
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    item = load_catalog()["sight_glass_opaque"]

    meshes, _ = extract_weapon_meshes(game, item, tmp_path / "cache")

    assert len(meshes) == 1
    assert meshes[0].mesh_name == "OpaqueSightGlass"
    assert meshes[0].bounds_min == pytest.approx(
        (-0.01942278, 0.02292296, 0.0175),
        abs=1e-6,
    )
    assert meshes[0].bounds_max == pytest.approx(
        (0.01942278, 0.05589704, 0.0175),
        abs=1e-6,
    )


def test_every_weapon_has_native_ammo_showcase(tmp_path):
    root = Path(__file__).parents[1] / "src/gtfo_skin_forge/data/preview_meshes"
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )

    for item in load_catalog().values():
        if item.kind != "weapon":
            continue
        ammo_parts = [
            part for part in item.parts
            if part.id in {"ammo_speedloader", "ammo_bullet", "ammo_shell"}
        ]
        expected_ammo_ids = {"ammo_bullet", "ammo_shell"}
        if item.id in {"hel_left", "heavy_revolver"}:
            expected_ammo_ids.add("ammo_speedloader")
        assert {part.id for part in ammo_parts} == expected_ammo_ids
        assert all(part.optional for part in ammo_parts)
        assert {part.asset for part in ammo_parts} <= {1, 563}
        for filename in ("ammo_bullet.obj.gz", "ammo_shell.obj.gz"):
            path = root / item.id / filename
            assert path.is_file(), path
        speedloader_path = root / item.id / "ammo_speedloader.obj.gz"
        assert speedloader_path.is_file() == (
            "ammo_speedloader" in expected_ammo_ids
        )
        bullet_bounds = _obj_bounds(
            gzip.decompress(
                (root / item.id / "ammo_bullet.obj.gz").read_bytes()
            ).decode("utf-8")
        )
        shell_bounds = _obj_bounds(
            gzip.decompress(
                (root / item.id / "ammo_shell.obj.gz").read_bytes()
            ).decode("utf-8")
        )
        bullet_center_x = (bullet_bounds[0][0] + bullet_bounds[1][0]) / 2
        shell_center_x = (shell_bounds[0][0] + shell_bounds[1][0]) / 2
        assert bullet_center_x == pytest.approx(shell_center_x, abs=1e-5)
        assert bullet_bounds[0][1] == pytest.approx(
            shell_bounds[0][1],
            abs=1e-5,
        )
        meshes, _ = extract_weapon_meshes(
            game,
            item,
            tmp_path / "cache",
            enabled_parts=expected_ammo_ids,
        )
        weapon_parts = [
            (
                tuple(
                    mesh.bounds_min[axis] + mesh.position[axis]
                    for axis in range(3)
                ),
                tuple(
                    mesh.bounds_max[axis] + mesh.position[axis]
                    for axis in range(3)
                ),
            )
            for mesh in meshes
            if not mesh.part_id.startswith("ammo_")
        ]
        weapon_low = [
            min(bounds[0][axis] for bounds in weapon_parts)
            for axis in range(3)
        ]
        weapon_high = [
            max(bounds[1][axis] for bounds in weapon_parts)
            for axis in range(3)
        ]
        weapon_center_x = (weapon_low[0] + weapon_high[0]) / 2
        weapon_center_z = (weapon_low[2] + weapon_high[2]) / 2
        ammo_center_x = (
            bullet_bounds[0][0] + bullet_bounds[1][0]
        ) / 2
        ammo_center_z = (
            (bullet_bounds[0][2] + bullet_bounds[1][2]) / 2
            + (shell_bounds[0][2] + shell_bounds[1][2]) / 2
        ) / 2
        assert ammo_center_x == pytest.approx(weapon_center_x, abs=1e-5)
        assert ammo_center_z == pytest.approx(weapon_center_z, abs=1e-5)
        assert {
            mesh.part_id for mesh in meshes if mesh.part_id.startswith("ammo_")
        } == expected_ammo_ids


def test_each_ammo_attachment_has_full_round_and_empty_case(tmp_path):
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    catalog = load_catalog()

    for item_id in (
        "ammo_9mm",
        "ammo_308",
        "ammo_12g",
        "ammo_45acp",
        "ammo_revolver",
    ):
        item = catalog[item_id]
        assert item.kind == "attachment"
        assert item.category == "子弹"
        part_ids = {part.id for part in item.parts}
        assert {"ammo_bullet", "ammo_shell"} <= part_ids
        meshes, bounds = extract_weapon_meshes(
            game,
            item,
            tmp_path / "cache",
            enabled_parts=part_ids,
        )
        assert {mesh.part_id for mesh in meshes} == part_ids
        assert bounds[1][0] > bounds[0][0]
        assert bounds[1][1] > bounds[0][1]


def test_weapon_ammo_names_include_caliber():
    catalog = load_catalog()
    expected = {
        "assault_rifle": ".308",
        "small_pistol": "9mm",
        "hel_shotgun": "12G",
        "hel_left": ".44",
    }

    for item_id, caliber in expected.items():
        item = catalog[item_id]
        names = [
            part.display_name
            for part in item.parts
            if part.id in {"ammo_bullet", "ammo_shell"}
        ]
        assert len(names) == 2
        assert all(caliber in name for name in names)


def test_manual_pistol_light_uses_complete_body_and_glass_pose():
    root = Path(__file__).parents[1] / "src/gtfo_skin_forge/data/preview_meshes"
    expected_bounds = (
        (-0.020019559, -0.083431222, -0.029406119),
        (0.022506690, -0.052043889, 0.066449590),
    )

    for item_id in ("large_caliber", "small_pistol"):
        text = gzip.decompress(
            (root / item_id / "flashlight.obj.gz").read_bytes()
        ).decode("utf-8")
        assert sum(line.startswith("v ") for line in text.splitlines()) == 2543
        bounds_min, bounds_max = _obj_bounds(text)
        assert bounds_min == pytest.approx(expected_bounds[0], abs=1e-6)
        assert bounds_max == pytest.approx(expected_bounds[1], abs=1e-6)


def test_every_forge_preview_loads_from_user_cache(tmp_path):
    game = GameInstall(root=tmp_path / "game", data_dir=tmp_path / "game/GTFO_Data", source="test")

    for item in load_catalog().values():
        if item.kind != "weapon":
            continue
        meshes, bounds = extract_weapon_meshes(game, item, tmp_path / "cache")
        assert len(meshes) == len(item.active_parts()), item.id
        assert all(mesh.obj_path.is_file() for mesh in meshes), item.id
        assert all(mesh.obj_path.parent.name == item.id for mesh in meshes), item.id
        assert bounds[1][2] > bounds[0][2], item.id

    hel_revolver, _ = extract_weapon_meshes(game, load_catalog()["hel_left"], tmp_path / "cache")
    assert all(
        mesh.obj_path.parent == tmp_path / "cache/forge_preview_meshes_v1/hel_left"
        for mesh in hel_revolver
    )


def test_disabled_part_is_removed_from_3d_preview(tmp_path):
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    item = load_catalog()["small_pistol"]

    meshes, _ = extract_weapon_meshes(
        game,
        item,
        tmp_path / "cache",
        disabled_parts={"front"},
    )

    assert meshes
    assert "front" not in {mesh.part_id for mesh in meshes}


def test_emissive_preview_keeps_native_color_and_black_transparency(tmp_path):
    source = tmp_path / "emissive.png"
    target = tmp_path / "emissive-preview.png"
    pixels = [
        (0, 0, 0, 255),
        (255, 196, 16, 255),
        (40, 40, 40, 255),
    ]
    image = Image.new("RGBA", (len(pixels), 1))
    image.putdata(pixels)
    image.save(source)

    generated = _emissive_preview_texture(
        source,
        (1.0, 1.0, 1.0, 1.0),
        target,
    )

    assert generated == target
    with Image.open(target) as preview:
        result = list(preview.convert("RGBA").getdata())
    assert result[0] == (0, 0, 0, 0)
    assert result[1][:3] == (255, 255, 160)
    assert result[1][3] == 255
    assert 0 < result[2][3] < 255


def test_emissive_preview_applies_project_color_and_intensity(tmp_path):
    source = tmp_path / "emissive.png"
    target = tmp_path / "emissive-preview.png"
    image = Image.new("RGBA", (2, 1), (80, 80, 80, 255))
    image.save(source)

    _emissive_preview_texture(source, (0.5, 2.0, 0.25, 1.0), target)

    with Image.open(target) as preview:
        result = list(preview.convert("RGBA").getdata())
    assert result[0][:3] == (255, 255, 200)
    assert result[0][3] == pytest.approx(80, abs=1)


def test_prepare_preview_materials_uses_separate_emissive_layer(tmp_path):
    item = load_catalog()["hel_left"]
    part = next(part for part in item.parts if part.id == "receiver")
    preview_item = CatalogItem(
        id=item.id,
        name=item.name,
        category=item.category,
        aliases=item.aliases,
        parts=[part],
        description=item.description,
        kind=item.kind,
    )
    albedo = tmp_path / "albedo.png"
    normal = tmp_path / "normal.png"
    emissive = tmp_path / "emissive.png"
    Image.new("RGB", (8, 8), (70, 90, 110)).save(albedo)
    Image.new("RGB", (8, 8), (128, 128, 255)).save(normal)
    glow_map = Image.new("RGB", (8, 8), (0, 0, 0))
    glow_map.paste((255, 30, 10), (0, 0, 8, 1))
    glow_map.save(emissive)
    game = GameInstall(
        root=tmp_path / "game",
        data_dir=tmp_path / "game/GTFO_Data",
        source="test",
    )
    project = SkinProject(
        name="Emissive",
        item_id=item.id,
        assignments={
            part.id: {
                "Albedo": str(albedo),
                "Normal": str(normal),
                "Emissive": str(emissive),
            }
        },
        emissive={
            part.id: EmissiveSettings(
                enabled=True,
                color=(1.0, 1.0, 1.0, 1.0),
            )
        },
    )

    materials = prepare_preview_materials(
        game,
        preview_item,
        project,
        cache_root=tmp_path / "cache",
    )

    prepared = materials[part.id]
    assert prepared.albedo == albedo
    assert prepared.emissive is not None
    assert prepared.emissive.parent == tmp_path / "cache/3d_textures"
    with Image.open(prepared.emissive) as glow:
        assert glow.getpixel((0, 0))[:3] == (255, 255, 100)
        assert glow.getpixel((0, 1))[:3] == (0, 0, 0)
    project.emissive[part.id].enabled = False
    hidden = prepare_preview_materials(
        game,
        preview_item,
        project,
        cache_root=tmp_path / "cache",
    )
    assert hidden[part.id].albedo == albedo
    assert hidden[part.id].emissive is None
