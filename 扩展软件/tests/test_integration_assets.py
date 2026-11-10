from __future__ import annotations

import os
import shutil
from pathlib import Path

import numpy as np
import pytest

from gtfo_skin_forge.assets import (
    BuildContext,
    _material,
    asset_path,
    build_assets,
    inspect_material,
    load_unity_env,
    res_path,
)
from gtfo_skin_forge.catalog import load_catalog
from gtfo_skin_forge.game_locator import best_game_install
from gtfo_skin_forge.matching import analyze_texture_folder
from gtfo_skin_forge.mesh_preview import (
    extract_weapon_meshes,
    prepare_preview_materials,
)
from gtfo_skin_forge.models import CatalogItem, EmissiveSettings, PartSpec, SkinProject


def _hel_folder(reference_root: Path | None) -> Path | None:
    if reference_root is None:
        return None
    for base, _, files in os.walk(reference_root):
        if Path(base).name == "maps" and sum("HELContractKiss_" in file for file in files) >= 20:
            return Path(base)
    return None


@pytest.fixture(scope="module")
def integration_context(reference_root):
    game = best_game_install("")
    folder = _hel_folder(reference_root)
    if game is None or folder is None:
        pytest.skip("GTFO install or reference assets unavailable")
    item = load_catalog()["helgun"]
    analysis = analyze_texture_folder(game, item, folder)
    return game, folder, item, analysis


def test_hel_reference_is_fully_identified(integration_context):
    _, _, _, analysis = integration_context
    assert not analysis.missing_parts
    assert not analysis.missing_channels
    assert not analysis.unknown_files
    for slots in analysis.slots.values():
        for match in slots.values():
            assert match.path is not None
            assert match.confidence >= 0.9


def test_material_names_and_missing_emissive_are_not_cross_matched(
    tmp_path,
    integration_context,
):
    game, folder, _, _ = integration_context
    item = load_catalog()["hel_shotgun"]
    albedo = next(folder.glob("*_Albedo.png"))
    normal = next(folder.glob("*_Normal.png"))
    metallic = next(folder.glob("*_MetallicSmoothness.png"))
    emissive = next(folder.glob("*_Emissive.png"))
    files = {
        "Sight_1_Base_color.png": albedo,
        "Sight_1_Normal.png": normal,
        "Sight_1_Metallic.png": metallic,
        "IronSight_10_Base_color.png": emissive,
        "IronSight_10_Normal.png": emissive,
        "IronSight_10_Metallic.png": emissive,
        "Front_Pistol_1_Slide_Emissive.png": emissive,
    }
    for name, source in files.items():
        shutil.copy2(source, tmp_path / name)
    analysis = analyze_texture_folder(game, item, tmp_path)
    sight = analysis.slots["sight"]
    assert sight["Albedo"].path.name == "Sight_1_Base_color.png"
    assert sight["Normal"].path.name == "Sight_1_Normal.png"
    assert sight["MetallicSmoothness"].path.name == "Sight_1_Metallic.png"
    assert all(slots["Emissive"].path is None for slots in analysis.slots.values())


def test_build_assets_and_verify_emissive(tmp_path, forge_data, integration_context):
    game, folder, item, analysis = integration_context
    project = SkinProject.from_analysis(
        name="HEL Integration",
        game_root=game.root,
        item_id=item.id,
        folder=folder,
        analysis=analysis,
        resolution_mode="1k",
    )
    project.emissive = {
        part.id: EmissiveSettings(True, (0.2, 0.4, 0.8, 1.0), 1.5)
        for part in item.parts
    }
    output = tmp_path / "output"
    result = build_assets(
        BuildContext(
            game=game,
            item=item,
            project=project,
            output_dir=output,
            affected_items=["HELGUN", "SMG"],
            persistent_backup=True,
        )
    )
    assert len(result.files) == 5
    assert result.manifest_path.is_file()
    for built in result.files:
        env = load_unity_env(built.output_path)
        for material_name in built.materials:
            material = _material(env, material_name)
            floats = dict(material.m_SavedProperties.m_Floats)
            colors = {name: value for name, value in material.m_SavedProperties.m_Colors}
            slots = {name: value for name, value in material.m_SavedProperties.m_TexEnvs}
            assert floats["_EnableEmissive"] == 1.0
            assert abs(colors["_EmissiveColor"].b - 1.2) < 1e-5
            assert slots["_MainTex"].m_Texture.m_PathID
            assert slots["_BumpMap"].m_Texture.m_PathID
            assert slots["_MetallicGlossMap"].m_Texture.m_PathID
            assert slots["_EmissiveMap"].m_Texture.m_PathID


def test_build_assets_honors_disabled_parts(
    tmp_path,
    forge_data,
    integration_context,
):
    game, folder, item, analysis = integration_context
    project = SkinProject.from_analysis(
        name="Albedo only",
        game_root=game.root,
        item_id=item.id,
        folder=folder,
        analysis=analysis,
        resolution_mode="1k",
    )
    disabled_part = next(
        part for part in item.parts
        if part.id in project.assignments
    )
    project.disabled_parts = [disabled_part.id]
    result = build_assets(
        BuildContext(
            game=game,
            item=item,
            project=project,
            output_dir=tmp_path / "output",
            affected_items=[item.name],
            persistent_backup=True,
        )
    )

    assert result.files
    assert all(
        disabled_part.material not in built.materials
        for built in result.files
    )


def test_multiple_materials_in_one_asset_are_written_together(
    tmp_path,
    forge_data,
    integration_context,
):
    game, folder, _, _ = integration_context
    source_image = next(folder.glob("*_Albedo.png"))
    item = CatalogItem(
        id="shared_asset_test",
        name="Shared asset test",
        category="测试",
        aliases=[],
        parts=[
            PartSpec("shotgun_mag", "mag", 335, "Mag_12G_10rnds"),
            PartSpec("rifle_mag", "mag", 335, "Mag_556_60rnds_A"),
        ],
    )
    project = SkinProject(
        name="Shared asset",
        game_root=str(game.root),
        item_id=item.id,
        assignments={
            "shotgun_mag": {"Albedo": str(source_image)},
            "rifle_mag": {"Albedo": str(source_image)},
        },
    )
    result = build_assets(
        BuildContext(
            game=game,
            item=item,
            project=project,
            output_dir=tmp_path / "shared-output",
            affected_items=[],
            persistent_backup=True,
        )
    )
    assert len(result.files) == 1
    assert result.files[0].asset == 335
    env = load_unity_env(result.files[0].output_path)
    first = _material(env, "Mag_12G_10rnds")
    second = _material(env, "Mag_556_60rnds_A")
    first_main = dict(first.m_SavedProperties.m_TexEnvs)["_MainTex"].m_Texture.m_PathID
    second_main = dict(second.m_SavedProperties.m_TexEnvs)["_MainTex"].m_Texture.m_PathID
    assert first_main
    assert second_main
    assert first_main != second_main
    for material in (first, second):
        slots = dict(material.m_SavedProperties.m_TexEnvs)
        emissive = slots.get("_EmissiveMap")
        assert emissive is None or emissive.m_Texture.m_PathID == 0


def test_user_resolution_is_not_downscaled_to_small_vanilla_texture(
    tmp_path,
    forge_data,
    integration_context,
):
    game, folder, _, _ = integration_context
    source_image = next(folder.glob("*_Albedo.png"))
    item = CatalogItem(
        id="high_resolution_mag",
        name="High resolution magazine",
        category="测试",
        aliases=[],
        parts=[PartSpec("mag", "mag", 335, "Mag_12G_10rnds")],
    )
    project = SkinProject(
        name="High resolution",
        game_root=str(game.root),
        item_id=item.id,
        resolution_mode="user",
        assignments={"mag": {"Albedo": str(source_image)}},
    )
    result = build_assets(
        BuildContext(
            game=game,
            item=item,
            project=project,
            output_dir=tmp_path / "high-resolution-output",
            affected_items=[],
            persistent_backup=True,
        )
    )
    env = load_unity_env(result.files[0].output_path)
    material = _material(env, "Mag_12G_10rnds")
    path_id = dict(material.m_SavedProperties.m_TexEnvs)["_MainTex"].m_Texture.m_PathID
    texture = next(obj.read() for obj in env.objects if obj.path_id == path_id)
    assert texture.m_Width == 2048
    assert texture.m_Height == 2048


def test_every_catalog_material_exists_in_current_game():
    game = best_game_install("")
    if game is None:
        pytest.skip("GTFO install unavailable")
    items = load_catalog()
    materials_by_asset: dict[int, set[str]] = {}
    for item in items.values():
        for part in item.parts:
            if part.asset not in materials_by_asset:
                path = asset_path(game.data_dir, part.asset)
                env = load_unity_env(path, res_path(game.data_dir, part.asset))
                materials_by_asset[part.asset] = {
                    obj.peek_name()
                    for obj in env.objects
                    if obj.type.name == "Material"
                }
    missing = [
        (item.id, part.id, part.asset, part.material)
        for item in items.values()
        for part in item.parts
        if part.material not in materials_by_asset[part.asset]
    ]
    assert not missing


def test_hel_shotgun_3d_meshes_are_assembled_and_filtered():
    game = best_game_install("")
    if game is None:
        pytest.skip("GTFO install unavailable")
    item = load_catalog()["hel_shotgun"]
    with __import__("tempfile").TemporaryDirectory() as directory:
        meshes, (low, high) = extract_weapon_meshes(
            game,
            item,
            cache_root=Path(directory),
        )
        names = {mesh.mesh_name for mesh in meshes}
        assert "Front_AutoShotgun_2" in names
        assert "Receiver_SMG_1" in names
        assert "Stock_Shootgun_2" in names
        assert "Mag_12G_10rnds" in names
        assert "Sight_1" in names
        assert "g_mapper" not in names
        assert "Screen" not in names
        assert "Sight_1_Glass" not in names
        assert all(mesh.obj_path.is_file() for mesh in meshes)
        assert bool(np.any(high > low))


def test_3d_preview_materials_can_fall_back_to_vanilla_textures(forge_data):
    game = best_game_install("")
    if game is None:
        pytest.skip("GTFO install unavailable")
    item = load_catalog()["hel_shotgun"]
    project = SkinProject(name="preview", item_id=item.id)
    materials = prepare_preview_materials(game, item, project)
    assert set(materials) == {
        part.id for part in item.active_parts(set(project.enabled_parts))
    }
    assert materials["front"].albedo is not None
    assert materials["front"].albedo.is_file()


def test_asset_reader_is_closed_after_inspection():
    game = best_game_install("")
    if game is None:
        pytest.skip("GTFO install unavailable")
    part = load_catalog()["mag_50_20"].parts[0]
    inspect_material(game, part)
    target = asset_path(game.data_dir, part.asset)
    with target.open("r+b"):
        pass
