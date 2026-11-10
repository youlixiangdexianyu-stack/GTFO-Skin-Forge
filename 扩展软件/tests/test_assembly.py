from __future__ import annotations

import pytest

from gtfo_skin_forge.assembly import load_assembly_catalog, resolve_mesh_offset
from gtfo_skin_forge.catalog import load_catalog


@pytest.fixture(scope="module")
def catalog():
    return load_catalog()


@pytest.fixture(scope="module")
def assembly():
    return load_assembly_catalog()


def test_every_weapon_has_forge_assembly(catalog, assembly):
    expected = {item.id for item in catalog.values() if item.kind == "weapon"}
    assert expected <= set(assembly["items"])
    assert set(assembly["items"].values()) <= set(assembly["templates"])


def test_hel_shotgun_attachment_offsets_match_forge(catalog, assembly):
    item = catalog["hel_shotgun"]
    parts = {part.role: part for part in item.parts}

    sight = resolve_mesh_offset(item, parts["sight"], "Sight_1", assembly)
    magazine = resolve_mesh_offset(item, parts["mag"], "Mag_12G_10rnds", assembly)
    bolt = resolve_mesh_offset(
        item,
        parts["front"],
        "Front_AutoShotgun_Bolt_2",
        assembly,
    )

    assert sight == pytest.approx((0.0, 0.038479989, 0.026000013))
    assert magazine == pytest.approx((0.0, -0.000830609, 0.08299068))
    assert bolt == pytest.approx((0.014146503, -0.000689453, 0.093555996))
    assert sight != magazine


def test_unknown_mesh_keeps_local_coordinates(catalog, assembly):
    item = catalog["hel_shotgun"]
    part = item.parts[0]

    assert resolve_mesh_offset(item, part, "Not_A_GTFO_Mesh_99", assembly) == (
        0.0,
        0.0,
        0.0,
    )


def test_revolver_main_mesh_uses_fold_mount_instead_of_group_origin(catalog, assembly):
    item = catalog["hel_left"]
    part = next(part for part in item.parts if part.role == "front")

    offset = resolve_mesh_offset(item, part, "Front_Revolver_2", assembly)

    assert offset == pytest.approx(
        (0.0, -0.00601886, 0.048413591),
        abs=1e-8,
    )
