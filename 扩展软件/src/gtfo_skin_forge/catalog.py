from __future__ import annotations

import json
from importlib import resources
from pathlib import Path
from typing import Iterable

from .models import CHANNELS, CatalogItem, PartSpec


MAIN_WEAPON_IDS = frozenset(
    {
        "hel_left",
        "smg",
        "bullpup",
        "heavy_smg",
        "pdw",
        "carbine",
        "dmr",
        "double_dmr",
        "assault_rifle",
        "hel_shotgun",
        "short_shotgun_2",
        "semi_auto_rifle",
        "small_pistol",
        "machine_pistol",
        "auto_pistol",
        "slug_shotgun",
        "battle_rifle",
        "double_pistol",
    }
)
SECONDARY_WEAPON_IDS = frozenset(
    {
        "machinegun_2",
        "heavy_assault",
        "pump_shotgun",
        "choke_shotgun",
        "heavy_revolver",
        "machinegun",
        "sniper",
        "infrared_precision",
        "helgun",
        "large_caliber",
        "burst_cannon",
        "helr",
        "combat_shotgun",
        "dragon_shotgun",
        "drekker_clr",
    }
)
ATTACHMENT_CATEGORY_ORDER = ("弹匣", "子弹", "枪灯", "瞄具", "镜片")
OFFICIAL_WEAPON_META = {
    "small_pistol": (1, "手枪", "Shelling S49"),
    "double_pistol": (2, "连发手枪", "Shelling Nano"),
    "hel_left": (3, "HEL左轮手枪", "Bataldo 3RB"),
    "machine_pistol": (4, "冲锋手枪", "Raptus Treffen 2"),
    "auto_pistol": (5, "HEL自动手枪", "Raptus Steigro"),
    "bullpup": (6, "无托步枪", "Accrat Golok DA"),
    "smg": (7, "冲锋枪", "Van Auken LTC5"),
    "pdw": (8, "PDW", "Accrat STB"),
    "heavy_smg": (9, "重型冲锋枪", "Accrat ND6"),
    "carbine": (10, "卡宾枪", "Van Auken CAB F4"),
    "dmr": (11, "精确步枪", "TR22 Hanaway"),
    "double_dmr": (12, "双发步枪", "Hanaway PSB"),
    "assault_rifle": (13, "突击步枪", "Malatack LX"),
    "semi_auto_rifle": (14, "连发步枪", "Malatack CH 4"),
    "battle_rifle": (15, "步枪", "Drekker Pres MOD 556"),
    "short_shotgun_2": (16, "短管霰弹枪", "Buckland SBS III"),
    "hel_shotgun": (17, "HEL霰弹枪", "Bataldo J 300"),
    "slug_shotgun": (18, "独头弹霰弹枪", "Bataldo Custom K330"),
    "heavy_assault": (1, "重型突击步枪", "Malatack HXC"),
    "drekker_clr": (2, "短管步枪", "Drekker CLR"),
    "pump_shotgun": (3, "霰弹枪", "Buckland s870"),
    "combat_shotgun": (4, "战斗霰弹枪", "Buckland AF6"),
    "dragon_shotgun": (5, "三管霰弹枪", "Drekker INEX Drei"),
    "choke_shotgun": (6, "收束器霰弹枪", "Buckland XDist2"),
    "heavy_revolver": (7, "左轮手枪", "Mastaba R66"),
    "machinegun_2": (8, "机关枪", "TechMan Arbalist V"),
    "machinegun": (9, "机关枪", "TechMan Veruta XII"),
    "burst_cannon": (10, "连发炮", "TechMan Klust 6"),
    "helgun": (11, "HEL枪", "Omneco exp1"),
    "large_caliber": (12, "大口径手枪", "Shelling Arid 5"),
    "infrared_precision": (13, "精准步枪", "Drekker DEL P1"),
    "sniper": (14, "狙击枪", "Köning PR 11"),
    "helr": (15, "HEL步枪", "Omneco LRG"),
}
FLASHLIGHT_COMPONENTS = {
    "A": (350, "Flashlight_A", "Gun Light A"),
    "B": (351, "Flashlight_B", "Gun Light B"),
    "C": (352, "Flashlight_C", "Gun Light C"),
    "D": (353, "Flashlight_D", "Gun Light D"),
    "E": (348, "Flashlight_E", "PEQ-1"),
}
WEAPON_FLASHLIGHT_TYPES = {
    "hel_left": "B",
    "machinegun_2": "D",
    "smg": "A",
    "bullpup": "E",
    "heavy_smg": "B",
    "pdw": "D",
    "carbine": "B",
    "dmr": "B",
    "double_dmr": "E",
    "assault_rifle": "B",
    "hel_shotgun": "E",
    "short_shotgun_2": "B",
    "semi_auto_rifle": "C",
    "heavy_assault": "E",
    "pump_shotgun": "A",
    "choke_shotgun": "E",
    "heavy_revolver": "A",
    "machinegun": "D",
    "sniper": "C",
    "infrared_precision": "A",
    "helgun": "E",
    "large_caliber": "B",
    "small_pistol": "B",
    "burst_cannon": "D",
    "helr": "C",
    "combat_shotgun": "A",
    "dragon_shotgun": "E",
    "machine_pistol": "B",
    "auto_pistol": "B",
    "slug_shotgun": "E",
    "battle_rifle": "E",
    "double_pistol": "B",
    "drekker_clr": "B",
}
AMMO_COMPONENTS = {
    "308": (1, "Casing_308", ".308 步枪弹"),
    "9mm": (1, "Casing_9mm", "9mm 手枪弹"),
    "shotgun": (1, "Shotgun_Shell_Empty_1", "12G 霰弹"),
    "revolver": (563, "Revolver_Shell_1", ".44 左轮弹"),
    "45": (1, "Casing_45", ".45 ACP 手枪弹"),
}
WEAPON_AMMO_TYPES = {
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
AMMO_ATTACHMENTS = (
    ("ammo_9mm", "9mm 手枪弹", "9mm"),
    ("ammo_308", ".308 步枪弹", "308"),
    ("ammo_12g", "12G 霰弹", "shotgun"),
    ("ammo_45acp", ".45 ACP 手枪弹", "45"),
    ("ammo_revolver", ".44 左轮弹", "revolver"),
)


def catalog_path() -> Path:
    return Path(__file__).with_name("data") / "catalog.json"


def load_catalog(path: Path | None = None) -> dict[str, CatalogItem]:
    source = path or catalog_path()
    raw = json.loads(source.read_text(encoding="utf-8"))
    items: dict[str, CatalogItem] = {}
    for value in raw.get("weapons", []):
        parts = [
            PartSpec(
                id=str(part["id"]),
                role=str(part["role"]),
                asset=int(part["asset"]),
                material=str(part["material"]),
                name_zh=str(part.get("name_zh", "")),
                meshes=list(part.get("meshes", [])),
                channels=list(part.get("channels", CHANNELS)),
                glass_material=part.get("glass_material"),
            )
            for part in value.get("parts", [])
        ]
        item = CatalogItem(
            id=str(value["id"]),
            name=str(value["name"]),
            category=str(value.get("category", "其他")),
            aliases=[str(alias) for alias in value.get("aliases", [])],
            parts=parts,
            description=str(value.get("description", "")),
            kind="weapon",
        )
        flashlight_type = WEAPON_FLASHLIGHT_TYPES.get(item.id)
        if flashlight_type:
            asset, material, label = FLASHLIGHT_COMPONENTS[flashlight_type]
            item.parts.append(
                PartSpec(
                    id="flashlight",
                    role="flashlight",
                    asset=asset,
                    material=material,
                    name_zh=f"枪灯 · {label}",
                    optional=True,
                )
            )
        ammo_type = WEAPON_AMMO_TYPES.get(item.id)
        if ammo_type:
            asset, material, label = AMMO_COMPONENTS[ammo_type]
            if ammo_type == "revolver":
                item.parts.append(
                    PartSpec(
                        id="ammo_speedloader",
                        role="ammo",
                        asset=563,
                        material="Revolver_Speedloader_1",
                        name_zh="快速装弹器 · 左轮",
                        optional=True,
                    )
                )
            for part_id, display_name in (
                ("ammo_bullet", f"整颗{label}"),
                ("ammo_shell", f"空弹壳 · {label}"),
            ):
                item.parts.append(
                    PartSpec(
                        id=part_id,
                        role="ammo",
                        asset=asset,
                        material=material,
                        name_zh=display_name,
                        optional=True,
                    )
                )
        _ensure_unique(item, items)
        items[item.id] = item
    for value in raw.get("attachments", []):
        part = PartSpec(
            id="main",
            role="main",
            asset=int(value["asset"]),
            material=str(value["material"]),
            name_zh=str(value.get("name", "主体")),
            channels=list(value.get("channels", CHANNELS)),
        )
        item = CatalogItem(
            id=str(value["id"]),
            name=str(value["name"]),
            category=str(value.get("category", "附件")),
            aliases=[str(alias) for alias in value.get("aliases", [])],
            parts=[part],
            description=str(value.get("description", "")),
            kind="attachment",
        )
        _ensure_unique(item, items)
        items[item.id] = item
    for item_id, name, ammo_type in AMMO_ATTACHMENTS:
        asset, material, label = AMMO_COMPONENTS[ammo_type]
        parts: list[PartSpec] = []
        if ammo_type == "revolver":
            parts.append(
                PartSpec(
                    id="ammo_speedloader",
                    role="ammo",
                    asset=563,
                    material="Revolver_Speedloader_1",
                    name_zh="快速装弹器 · 左轮",
                    optional=True,
                )
            )
        parts.extend(
            (
                PartSpec(
                    id="ammo_bullet",
                    role="ammo",
                    asset=asset,
                    material=material,
                    name_zh=f"整颗{label}",
                    optional=True,
                ),
                PartSpec(
                    id="ammo_shell",
                    role="ammo",
                    asset=asset,
                    material=material,
                    name_zh=f"空弹壳 · {label}",
                    optional=True,
                ),
            )
        )
        item = CatalogItem(
            id=item_id,
            name=name,
            category="子弹",
            aliases=[name, label, material],
            parts=parts,
            description=f"{name} 独立模型与贴图通道",
            kind="attachment",
        )
        _ensure_unique(item, items)
        items[item.id] = item
    return items


def _ensure_unique(item: CatalogItem, items: dict[str, CatalogItem]) -> None:
    if item.id in items:
        raise ValueError(f"Duplicate catalog item id: {item.id}")
    seen_parts: set[str] = set()
    for part in item.parts:
        if part.id in seen_parts:
            raise ValueError(f"Duplicate part id {part.id} in {item.id}")
        seen_parts.add(part.id)


def group_items(items: Iterable[CatalogItem]) -> dict[str, list[CatalogItem]]:
    grouped: dict[str, list[CatalogItem]] = {}
    for item in items:
        grouped.setdefault(item.category, []).append(item)
    for values in grouped.values():
        values.sort(key=lambda value: value.name.casefold())
    return dict(sorted(grouped.items(), key=lambda pair: pair[0].casefold()))


def weapon_loadout_group(item: CatalogItem) -> str:
    if item.kind != "weapon":
        return "枪械配件"
    if item.id in MAIN_WEAPON_IDS:
        return "主武器"
    if item.id in SECONDARY_WEAPON_IDS:
        return "副武器"
    raise ValueError(f"Unclassified weapon: {item.id}")


def official_weapon_meta(item: CatalogItem) -> tuple[int, str, str]:
    try:
        return OFFICIAL_WEAPON_META[item.id]
    except KeyError as exc:
        raise ValueError(f"Missing official weapon metadata: {item.id}") from exc


def weapon_display_label(item: CatalogItem) -> str:
    _, chinese, english = official_weapon_meta(item)
    return f"{chinese} · {english}"


def item_display_name(item: CatalogItem) -> str:
    """Return the user-facing full name used outside internal matching."""

    if item.kind != "weapon":
        return item.name
    try:
        return weapon_display_label(item)
    except ValueError:
        return item.name


def weapon_sections(items: Iterable[CatalogItem]) -> dict[str, list[CatalogItem]]:
    sections = {"主武器": [], "副武器": []}
    for item in items:
        if item.kind == "weapon":
            sections[weapon_loadout_group(item)].append(item)
    for values in sections.values():
        values.sort(key=lambda item: official_weapon_meta(item)[0])
    return sections


def shared_assets(items: Iterable[CatalogItem]) -> dict[int, list[str]]:
    usage: dict[int, list[str]] = {}
    for item in items:
        for asset in sorted({part.asset for part in item.parts}):
            usage.setdefault(asset, []).append(item.id)
    return usage
