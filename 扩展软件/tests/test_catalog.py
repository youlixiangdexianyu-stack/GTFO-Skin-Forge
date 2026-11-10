from __future__ import annotations

from gtfo_skin_forge.assets import affected_by_asset
from gtfo_skin_forge.catalog import (
    MAIN_WEAPON_IDS,
    SECONDARY_WEAPON_IDS,
    group_items,
    item_display_name,
    load_catalog,
    shared_assets,
    weapon_display_label,
    weapon_sections,
)


def test_catalog_loads_weapons_and_attachments():
    items = load_catalog()
    assert len(items) >= 60
    assert items["helgun"].name == "HELGUN"
    assert {part.material for part in items["helgun"].parts} >= {
        "Front_MachineGun_2",
        "Receiver_Machinegun_2",
        "Stock_Precision_2",
        "Mag_762_40rnds_A",
        "Sight_12",
    }
    assert items["gun_light_a"].kind == "attachment"
    assert {part.role for part in items["machinegun_2"].active_parts()} == {
        "front",
        "receiver",
        "stock",
        "mag",
        "sight",
    }
    assert {part.role for part in items["semi_auto_rifle"].active_parts()} == {
        "front",
        "receiver",
        "stock",
        "mag",
        "sight",
    }
    assert {part.role for part in items["slug_shotgun"].active_parts()} == {
        "front",
        "receiver",
        "stock",
        "sight",
    }
    assert "flashlight" in {
        part.role
        for part in items["slug_shotgun"].active_parts({"flashlight"})
    }
    assert {part.material for part in items["drekker_clr"].active_parts()} == {
        "Front_SMG_2",
        "Receiver_SMG_4",
        "Stock_Rifle_3",
        "Mag_9mm_30rdns_A",
        "Sight_25",
    }
    pdw_light = next(part for part in items["pdw"].parts if part.id == "flashlight")
    assert pdw_light.optional
    assert pdw_light.material == "Flashlight_D"
    assert "flashlight" not in {part.id for part in items["pdw"].active_parts()}
    assert "flashlight" in {
        part.id for part in items["pdw"].active_parts({"flashlight"})
    }


def test_catalog_groups_and_shared_asset_index():
    items = load_catalog()
    groups = group_items(items.values())
    assert "步枪" in groups
    usage = shared_assets(items.values())
    assert "helgun" in usage[578]
    assert "machinegun" in usage[578]


def test_user_facing_weapon_names_use_official_full_labels():
    items = load_catalog()

    assert item_display_name(items["pump_shotgun"]) == (
        "霰弹枪 · Buckland s870"
    )
    assert item_display_name(items["sniper"]) == "狙击枪 · Köning PR 11"
    assert item_display_name(items["sight_22"]) == "Sight 22"
    affected = affected_by_asset(items.values(), [572])
    assert "狙击枪 · Köning PR 11" in affected
    assert "大狙" not in affected


def test_every_weapon_is_in_exactly_one_loadout_section():
    items = load_catalog()
    weapon_ids = {item.id for item in items.values() if item.kind == "weapon"}

    assert not (MAIN_WEAPON_IDS & SECONDARY_WEAPON_IDS)
    assert weapon_ids == MAIN_WEAPON_IDS | SECONDARY_WEAPON_IDS
    sections = weapon_sections(items.values())
    assert {item.id for item in sections["主武器"]} == MAIN_WEAPON_IDS
    assert {item.id for item in sections["副武器"]} == SECONDARY_WEAPON_IDS
    assert [weapon_display_label(item) for item in sections["主武器"][:3]] == [
        "手枪 · Shelling S49",
        "连发手枪 · Shelling Nano",
        "HEL左轮手枪 · Bataldo 3RB",
    ]
    assert weapon_display_label(sections["副武器"][0]) == (
        "重型突击步枪 · Malatack HXC"
    )
