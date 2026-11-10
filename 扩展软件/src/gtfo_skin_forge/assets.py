from __future__ import annotations

import copy
import hashlib
import json
import math
import os
import shutil
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

import UnityPy
from PIL import Image

from .catalog import item_display_name
from .config import ensure_app_dirs
from .models import (
    CHANNELS,
    CHANNEL_SLOTS,
    AssetMaterialInfo,
    BuildFileResult,
    BuildResult,
    CatalogItem,
    EmissiveSettings,
    GameInstall,
    PartSpec,
    SkinProject,
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def asset_path(data_dir: Path, asset_id: int) -> Path:
    return data_dir / f"sharedassets{asset_id}.assets"


def res_path(data_dir: Path, asset_id: int) -> Path:
    return data_dir / f"sharedassets{asset_id}.assets.resS"


def load_unity_env(asset_file: Path, resource_file: Path | None = None):
    if resource_file and resource_file.is_file():
        return UnityPy.load(str(asset_file), str(resource_file))
    return UnityPy.load(str(asset_file))


def close_unity_env(env) -> None:
    """Close all file streams retained by UnityPy readers."""
    seen: set[int] = set()

    def close_item(item) -> None:
        item_id = id(item)
        if item_id in seen:
            return
        seen.add(item_id)
        reader = getattr(item, "reader", None)
        if reader is not None:
            close_item(reader)
        children = getattr(item, "files", None)
        if isinstance(children, dict):
            for child in children.values():
                close_item(child)
        dispose = getattr(item, "dispose", None)
        if callable(dispose):
            try:
                dispose()
            except (OSError, ValueError):
                pass
        stream = getattr(item, "stream", None)
        if stream is not None and hasattr(stream, "close"):
            try:
                stream.close()
            except (OSError, ValueError):
                pass

    close_item(env)
    files = getattr(env, "files", None)
    if isinstance(files, dict):
        for item in files.values():
            close_item(item)
    cabs = getattr(env, "cabs", None)
    if isinstance(cabs, dict):
        for item in cabs.values():
            close_item(item)


def _find_file(env, asset_file: Path):
    name = asset_file.name.casefold()
    for key, value in env.files.items():
        if str(key).casefold().endswith(name):
            return value
    if hasattr(env, "file") and env.file is not None:
        return env.file
    raise ValueError(f"鏃犳硶璇诲彇 Unity 鏂囦欢瀹瑰櫒: {asset_file.name}")


def _objects_by_id(env) -> dict[int, Any]:
    return {obj.path_id: obj for obj in env.objects}


def _material(env, material_name: str):
    for obj in env.objects:
        if obj.type.name != "Material":
            continue
        if obj.peek_name() == material_name:
            return obj.read()
    available = sorted(
        obj.peek_name()
        for obj in env.objects
        if obj.type.name == "Material"
    )
    raise KeyError(f"鏉愯川 {material_name} 涓嶅瓨鍦紱鍙敤鏉愯川: {', '.join(available)}")


def _slots(material) -> list[tuple[str, Any]]:
    return list(material.m_SavedProperties.m_TexEnvs)


def _slot_value(material, slot_name: str):
    for name, value in _slots(material):
        if name == slot_name:
            return value
    return None


def inspect_material(game: GameInstall, part: PartSpec) -> AssetMaterialInfo:
    path = asset_path(game.data_dir, part.asset)
    if not path.is_file():
        raise FileNotFoundError(path)
    env = load_unity_env(path, res_path(game.data_dir, part.asset))
    try:
        material = _material(env, part.material)
        slot_values = {name: value.m_Texture.m_PathID or None for name, value in _slots(material)}
        floats = {name: float(value) for name, value in material.m_SavedProperties.m_Floats}
        colors = {
            name: (float(value.r), float(value.g), float(value.b), float(value.a))
            for name, value in material.m_SavedProperties.m_Colors
        }
        mesh_names = [obj.peek_name() for obj in env.objects if obj.type.name == "Mesh"]
        has_custom_gear_marker = floats.get("_IsCustomGearMaterial", 0.0) == 1.0
        compatible = (
            "_EmissiveMap" in slot_values
            or "_EmissiveColor" in colors
            or has_custom_gear_marker
        )
        warnings: list[str] = []
        if not compatible:
            warnings.append("褰撳墠 shader 鏈彂鐜版爣鍑嗗彂鍏夊睘鎬э紝鍚敤鍙戝厜鍙兘鏃犳晥")
        emissive_texture_name = ""
        emissive_path = slot_values.get("_EmissiveMap")
        if emissive_path:
            objects = _objects_by_id(env)
            try:
                emissive_texture_name = objects[int(emissive_path)].peek_name()
            except (KeyError, ValueError):
                pass
        customized = bool(
            floats.get("_EnableEmissive", 0.0) > 0
            or emissive_texture_name
            or any(
                name and not any(token in name.casefold() for token in ("_1_", "_albedo", "_normal", "_metallicsmoothness"))
                for name in (
                    _texture_name_for_slot(env, material, "_MainTex"),
                )
            )
        )
        if customized:
            warnings.append("妫€娴嬪埌褰撳墠鏉愯川鍙兘宸茶鏃ф灙鐨慨鏀癸紝宸插姞鍏ュ熀鍑嗚鍛?)
        return AssetMaterialInfo(
            asset=part.asset,
            material=part.material,
            slots=slot_values,
            floats=floats,
            colors=colors,
            keywords=str(material.m_ShaderKeywords or ""),
            mesh_names=mesh_names,
            compatible_emissive=compatible,
            warnings=warnings,
        )
    finally:
        close_unity_env(env)


def _texture_name_for_slot(env, material, slot_name: str) -> str:
    slot = _slot_value(material, slot_name)
    if not slot:
        return ""
    path_id = int(slot.m_Texture.m_PathID or 0)
    if not path_id:
        return ""
    try:
        return str(_objects_by_id(env)[path_id].peek_name())
    except (KeyError, ValueError):
        return ""


def available_channels(info: AssetMaterialInfo) -> list[str]:
    result: list[str] = []
    for channel in CHANNELS:
        slot = CHANNEL_SLOTS[channel]
        if channel == "Emissive":
            if info.compatible_emissive:
                result.append(channel)
            continue
        if slot in info.slots:
            result.append(channel)
    return result


def extract_texture(
    game: GameInstall,
    part: PartSpec,
    channel: str,
    source_asset: Path | None = None,
) -> Image.Image | None:
    source = source_asset or asset_path(game.data_dir, part.asset)
    env = load_unity_env(source, res_path(game.data_dir, part.asset))
    try:
        material = _material(env, part.material)
        slot = _slot_value(material, CHANNEL_SLOTS[channel])
        if slot is None:
            return None
        path_id = int(slot.m_Texture.m_PathID or 0)
        if not path_id:
            return None
        objects = _objects_by_id(env)
        if path_id not in objects:
            return None
        texture = objects[path_id].read()
        try:
            return texture.image.convert("RGBA")
        except Exception:
            return None
    finally:
        close_unity_env(env)


class BaselineVault:
    def __init__(self, root: Path | None = None):
        self.root = root or ensure_app_dirs()["baselines"]
        self.root.mkdir(parents=True, exist_ok=True)

    def path_for(self, asset_id: int) -> Path:
        return self.root / f"sharedassets{asset_id}.assets"

    def metadata_path(self, asset_id: int) -> Path:
        return self.root / f"sharedassets{asset_id}.json"

    def ensure(
        self,
        game: GameInstall,
        asset_id: int,
        *,
        clean_dir: Path | None = None,
        overwrite_with_clean: bool = False,
        persistent: bool = True,
    ) -> tuple[Path, bool]:
        current = asset_path(game.data_dir, asset_id)
        if not current.is_file():
            raise FileNotFoundError(current)
        if not persistent:
            return current, False
        target = self.path_for(asset_id)
        clean_source = clean_dir / current.name if clean_dir else None
        if clean_source and clean_source.is_file() and (
            overwrite_with_clean
            or not target.is_file()
            or sha256(clean_source) != sha256(target)
        ):
            shutil.copy2(clean_source, target)
            self._write_metadata(asset_id, target, current, "clean_override")
            return target, True
        if not target.is_file():
            shutil.copy2(current, target)
            self._write_metadata(asset_id, target, current, "first_run_snapshot")
        return target, False

    def _write_metadata(
        self,
        asset_id: int,
        baseline: Path,
        original: Path,
        reason: str,
    ) -> None:
        value = {
            "asset": asset_id,
            "created_at": datetime.now(timezone.utc).isoformat(),
            "reason": reason,
            "original_path": str(original),
            "sha256": sha256(baseline),
            "size": baseline.stat().st_size,
        }
        self.metadata_path(asset_id).write_text(
            json.dumps(value, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )


@dataclass(slots=True)
class _PartChange:
    part: PartSpec
    files: dict[str, Path]
    emissive: EmissiveSettings


@dataclass(slots=True)
class BuildContext:
    game: GameInstall
    item: CatalogItem
    project: SkinProject
    output_dir: Path
    affected_items: list[str]
    warnings: list[str] = field(default_factory=list)
    persistent_backup: bool = True
    clean_asset_dir: Path | None = None


def _proportional_size(
    source_size: tuple[int, int],
    maximum: int,
) -> tuple[int, int]:
    width, height = source_size
    scale = maximum / max(width, height)
    return (
        max(1, int(round(width * scale))),
        max(1, int(round(height * scale))),
    )


def _pixel_size(mode: str, image: Image.Image, baseline: Image.Image | None) -> tuple[int, int]:
    if mode == "user":
        return image.size
    if mode == "source" and baseline is not None:
        return _proportional_size(image.size, max(baseline.size))
    forced = {"1k": 1024, "2k": 2048, "4k": 4096}.get(mode.casefold())
    if forced:
        return _proportional_size(image.size, forced)
    if image.width == image.height and 128 <= image.width <= 8192:
        return image.size
    size = max(256, min(8192, max(image.size)))
    return _proportional_size(image.size, size)


def _normalize_image(path: Path, size: tuple[int, int]) -> Image.Image:
    image = Image.open(path).convert("RGBA")
    if image.size != size:
        safe_size = _proportional_size(image.size, max(size))
        image = image.resize(safe_size, Image.Resampling.LANCZOS)
    return image


def _blank_emissive(size: tuple[int, int]) -> Image.Image:
    return Image.new("RGBA", size, (0, 0, 0, 255))


def _set_texture_slot(material, slot_name: str, texture_path_id: int) -> None:
    slots = _slots(material)
    for index, (name, value) in enumerate(slots):
        if name == slot_name:
            value.m_Texture.m_FileID = 0
            value.m_Texture.m_PathID = int(texture_path_id)
            slots[index] = (name, value)
            material.m_SavedProperties.m_TexEnvs = slots
            return
    main = next((value for name, value in slots if name == "_MainTex"), None)
    if main is None:
        raise ValueError(f"鏃犳硶鍒涘缓 {slot_name}: 鏉愯川娌℃湁 _MainTex 妯℃澘")
    new_value = copy.copy(main)
    new_value.m_Texture.m_FileID = 0
    new_value.m_Texture.m_PathID = int(texture_path_id)
    slots.append((slot_name, new_value))
    material.m_SavedProperties.m_TexEnvs = slots


def _clear_texture_slot(material, slot_name: str) -> None:
    slots = _slots(material)
    for index, (name, value) in enumerate(slots):
        if name == slot_name:
            value.m_Texture.m_FileID = 0
            value.m_Texture.m_PathID = 0
            slots[index] = (name, value)
            material.m_SavedProperties.m_TexEnvs = slots
            return


def _update_float(material, key: str, value: float) -> None:
    values = list(material.m_SavedProperties.m_Floats)
    for index, (name, _) in enumerate(values):
        if name == key:
            values[index] = (name, float(value))
            break
    else:
        values.append((key, float(value)))
    material.m_SavedProperties.m_Floats = values


def _update_color(material, key: str, color: tuple[float, float, float, float]) -> None:
    values = list(material.m_SavedProperties.m_Colors)
    for index, (name, old_value) in enumerate(values):
        if name == key:
            values[index] = (name, type(old_value)(*color))
            break
    else:
        template = values[0][1] if values else None
        if template is None:
            raise ValueError(f"鏃犳硶鍒涘缓棰滆壊灞炴€?{key}: 鏉愯川娌℃湁棰滆壊妯℃澘")
        values.append((key, type(template)(*color)))
    material.m_SavedProperties.m_Colors = values


def _set_emissive(material, enabled: bool, color: tuple[float, float, float, float]) -> None:
    _update_float(material, "_EnableEmissive", 1.0 if enabled else 0.0)
    _update_color(material, "_EmissiveColor", color)
    _update_color(material, "_EmissionColor", color)
    keywords = set(str(material.m_ShaderKeywords or "").split())
    if enabled:
        keywords.add("ENABLE_EMISSIVE")
    else:
        keywords.discard("ENABLE_EMISSIVE")
    material.m_ShaderKeywords = " ".join(sorted(keywords))


def _disable_emissive(material) -> None:
    floats = list(material.m_SavedProperties.m_Floats)
    for index, (name, _) in enumerate(floats):
        if name == "_EnableEmissive":
            floats[index] = (name, 0.0)
            material.m_SavedProperties.m_Floats = floats
            break
    keywords = {
        keyword
        for keyword in str(material.m_ShaderKeywords or "").split()
        if keyword != "ENABLE_EMISSIVE"
    }
    material.m_ShaderKeywords = " ".join(sorted(keywords))


def _allocate_texture(
    env,
    file,
    template_reader,
    template_texture,
    name: str,
    image: Image.Image,
) -> int:
    new_id = max(file.objects) + 1
    reader = copy.copy(template_reader)
    reader.path_id = new_id
    reader.data = None
    file.objects[new_id] = reader
    texture = copy.copy(template_texture)
    texture.set_object_reader(reader)
    texture.m_Name = name
    mipmaps = int(math.log2(max(image.size))) + 1
    texture.set_image(image, target_format=10, mipmap_count=mipmaps)
    texture.save()
    return new_id


def _build_one_asset(
    ctx: BuildContext,
    asset_id: int,
    changes: list[_PartChange],
    vault: BaselineVault,
) -> BuildFileResult:
    source, _ = vault.ensure(
        ctx.game,
        asset_id,
        clean_dir=ctx.clean_asset_dir,
        persistent=ctx.persistent_backup,
    )
    res = res_path(ctx.game.data_dir, asset_id)
    env = load_unity_env(source, res)
    texture_ids: dict[str, dict[str, int]] = {}
    emissive_records: dict[str, dict[str, Any]] = {}
    try:
        file = _find_file(env, source)
        objects = _objects_by_id(env)

        for change in changes:
            material = _material(env, change.part.material)
            main_slot = _slot_value(material, "_MainTex")
            if main_slot is None:
                ctx.warnings.append(f"{change.part.display_name}: 鏉愯川缂哄皯 _MainTex锛屽凡璺宠繃")
                continue
            main_reader = objects.get(int(main_slot.m_Texture.m_PathID or 0))
            if main_reader is None:
                raise ValueError(f"{change.part.material}: _MainTex 寮曠敤鏃犳晥")
            main_texture = main_reader.read()
            material_keywords = set(str(material.m_ShaderKeywords or "").split())
            emissive_compatible = (
                "_EmissiveMap" in {name for name, _ in _slots(material)}
                or "_EmissiveColor" in {name for name, _ in material.m_SavedProperties.m_Colors}
                or "_IsCustomGearMaterial" in {name for name, _ in material.m_SavedProperties.m_Floats}
            )
            if change.emissive.enabled and not emissive_compatible:
                ctx.warnings.append(
                    f"{change.part.display_name}: 褰撳墠 shader 鏈０鏄庢爣鍑嗗彂鍏夊睘鎬э紝浠嶄細鍐欏叆浣嗘父鎴忓彲鑳藉拷鐣?
                )

            assigned_paths = dict(change.files)
            if not change.emissive.enabled:
                assigned_paths.pop("Emissive", None)
            elif not assigned_paths.get("Emissive"):
                if change.emissive.mask_path and change.emissive.mask_path.is_file():
                    assigned_paths["Emissive"] = change.emissive.mask_path
                else:
                    ctx.warnings.append(
                        f"{change.part.display_name}: 鏈彁渚涘彂鍏夊浘锛屼粎淇敼鍙戝厜鍙傛暟锛?
                        "涓嶄細纭涓€寮犲彂鍏夎创鍥?
                    )

            ids: dict[str, int] = {}
            for channel, path in assigned_paths.items():
                slot_name = CHANNEL_SLOTS.get(channel)
                if not slot_name:
                    continue
                existing_slot = _slot_value(material, slot_name)
                if existing_slot is None and channel != "Emissive":
                    ctx.warnings.append(
                        f"{change.part.display_name}: 鏉愯川娌℃湁 {slot_name}锛屽凡蹇界暐 {channel}"
                    )
                    continue
                template_reader = main_reader
                template_texture = main_texture
                if existing_slot is not None and int(existing_slot.m_Texture.m_PathID or 0) in objects:
                    template_reader = objects[int(existing_slot.m_Texture.m_PathID)]
                    template_texture = template_reader.read()
                with Image.open(path) as opened:
                    baseline = extract_texture(
                        ctx.game,
                        change.part,
                        channel,
                        source_asset=source,
                    )
                    size = _pixel_size(ctx.project.resolution_mode, opened, baseline)
                    image = _normalize_image(path, size)
                texture_name = f"{change.part.material}_{change.part.id}_{channel}_{ctx.project.name}"
                path_id = _allocate_texture(
                    env,
                    file,
                    template_reader,
                    template_texture,
                    texture_name,
                    image,
                )
                _set_texture_slot(material, slot_name, path_id)
                ids[slot_name] = path_id

            existing_emissive = (
                "_EmissiveMap" in {name for name, _ in _slots(material)}
                or "_EmissiveColor"
                in {name for name, _ in material.m_SavedProperties.m_Colors}
                or "_EnableEmissive"
                in {name for name, _ in material.m_SavedProperties.m_Floats}
            )
            if not change.emissive.enabled and existing_emissive:
                _clear_texture_slot(material, "_EmissiveMap")
            if change.emissive.enabled:
                _set_emissive(
                    material,
                    True,
                    change.emissive.effective_color(),
                )
            elif existing_emissive:
                _disable_emissive(material)
            material.save()
            if ids:
                texture_ids[change.part.material] = ids
            emissive_records[change.part.material] = {
                "enabled": change.emissive.enabled,
                "color": list(change.emissive.effective_color()),
                "intensity": change.emissive.intensity,
            }
            if "ENABLE_EMISSIVE" in material_keywords:
                ctx.warnings.append(
                    f"{change.part.material}: 鍘熸潗璐ㄥ凡鏈夊彂鍏夊叧閿瓧锛屽凡鎸夊綋鍓嶈缃噸鏂拌绠?
                )

        output = ctx.output_dir / f"sharedassets{asset_id}.assets"
        output_bytes = file.save()
    finally:
        close_unity_env(env)
    output.write_bytes(output_bytes)
    return BuildFileResult(
        asset=asset_id,
        output_path=output,
        source_path=source,
        sha256=sha256(output),
        materials=[change.part.material for change in changes],
        texture_path_ids=texture_ids,
        emissive=emissive_records,
    )


def build_assets(ctx: BuildContext) -> BuildResult:
    ctx.output_dir.mkdir(parents=True, exist_ok=True)
    vault = BaselineVault()
    parts_by_id = {part.id: part for part in ctx.item.parts}
    changes_by_asset: dict[int, list[_PartChange]] = {}
    for part_id, files in ctx.project.assignments.items():
        part = parts_by_id.get(part_id)
        if not part:
            ctx.warnings.append(f"椤圭洰鍖呭惈鏈煡閮ㄤ欢 {part_id}锛屽凡蹇界暐")
            continue
        if part.id in set(ctx.project.disabled_parts):
            continue
        resolved_files: dict[str, Path] = {}
        for channel, raw_path in files.items():
            path = Path(raw_path)
            if channel in CHANNELS and path.is_file():
                resolved_files[channel] = path
        emissive = ctx.project.emissive.get(part_id, EmissiveSettings())
        if not resolved_files and not emissive.enabled:
            continue
        changes_by_asset.setdefault(part.asset, []).append(
            _PartChange(part=part, files=resolved_files, emissive=emissive)
        )

    if not changes_by_asset:
        raise ValueError("娌℃湁鍙瀯寤虹殑璐村浘鎴栧彂鍏夎缃?)

    outputs: list[BuildFileResult] = []
    for asset_id, changes in sorted(changes_by_asset.items()):
        outputs.append(_build_one_asset(ctx, asset_id, changes, vault))

    manifest = {
        "schema_version": 1,
        "application": "GTFO Skin Forge",
        "version": "0.1.53",
        "created_at": datetime.now(timezone.utc).isoformat(),
        "project": ctx.project.to_dict(),
        "item": {
            "id": ctx.item.id,
            "name": item_display_name(ctx.item),
            "category": ctx.item.category,
        },
        "game": ctx.game.to_dict(),
        "resolution_mode": ctx.project.resolution_mode,
        "affected_items": ctx.affected_items,
        "warnings": ctx.warnings,
        "files": [
            {
                "asset": result.asset,
                "output": result.output_path.name,
                "source": str(result.source_path),
                "sha256": result.sha256,
                "materials": result.materials,
                "texture_path_ids": result.texture_path_ids,
                "emissive": result.emissive,
            }
            for result in outputs
        ],
    }
    manifest_path = ctx.output_dir / "build-manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    return BuildResult(
        item_id=ctx.item.id,
        output_dir=ctx.output_dir,
        manifest_path=manifest_path,
        files=outputs,
        warnings=ctx.warnings,
        affected_items=ctx.affected_items,
        item_name=item_display_name(ctx.item),
    )


def affected_by_asset(items: Iterable[CatalogItem], asset_ids: Iterable[int]) -> list[str]:
    targets = set(asset_ids)
    result: set[str] = set()
    for item in items:
        if any(part.asset in targets for part in item.parts):
            result.add(item_display_name(item))
    return sorted(result, key=str.casefold)


def safe_output_name(name: str) -> str:
    safe = "".join(char if char not in '<>:"/\\|?*' else "_" for char in name).strip(" .")
    return safe or "GTFO_Skin"


def prepare_output_dir(base: Path, name: str) -> Path:
    target = base / safe_output_name(name)
    target.mkdir(parents=True, exist_ok=True)
    return target


def cleanup_temp_output(path: Path) -> None:
    if path.is_dir() and path.parent == ensure_app_dirs()["temp"]:
        shutil.rmtree(path, ignore_errors=True)
