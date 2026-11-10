from __future__ import annotations

import math
import re
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import numpy as np
from PIL import Image, ImageFilter, ImageOps

from .assets import available_channels, extract_texture, inspect_material
from .catalog import load_catalog
from .models import (
    CHANNELS,
    CatalogItem,
    GameInstall,
    MatchAnalysis,
    SlotMatch,
    TextureCandidate,
)


SUPPORTED_EXTENSIONS = {".png", ".tga", ".tif", ".tiff", ".webp", ".jpg", ".jpeg"}
CHANNEL_NAME_PATTERNS = {
    "Albedo": (
        r"albedo",
        r"base[\s_-]*colou?r",
        r"diffuse",
        r"(?:^|[\s_-])diff(?:use)?(?:$|[\s_-])",
        r"(?:^|[\s_-])color(?:$|[\s_-])",
        r"colou?r",
        r"基础色",
        r"底色",
        r"漫反射",
    ),
    "Normal": (
        r"normal",
        r"(?:^|[\s_-])nrm(?:$|[\s_-])",
        r"(?:^|[\s_-])n(?:$|[\s_-])",
        r"法线",
    ),
    "MetallicSmoothness": (
        r"metallic",
        r"metal(?:ness)?",
        r"smoothness",
        r"(?:^|[\s_-])gloss(?:iness)?(?:$|[\s_-])",
        r"roughness",
        r"metal(?:lic)?[\s_-]*(?:smooth|gloss)",
        r"金属",
        r"光滑",
        r"粗糙",
    ),
    "Emissive": (
        r"emissive",
        r"emission",
        r"(?:^|[\s_-])glow(?:$|[\s_-])",
        r"(?:^|[\s_-])e(?:$|[\s_-])",
        r"自发光",
        r"发光",
    ),
}
ROLE_TOKENS = {
    "front": ("front", "body", "slide", "barrel", "枪身", "前部", "套筒", "枪管"),
    "receiver": ("receiver", "machinegun", "smg", "rifle", "frame", "机匣", "框架"),
    "stock": ("stock", "grip", "shootgun", "shotgun", "枪托", "握把"),
    "mag": ("mag", "magazine", "弹匣"),
    "sight": ("sight", "optic", "scope", "iron", "瞄具", "准星", "瞄"),
    "glass": ("glass", "lens", "镜片", "镜"),
    "flashlight": ("flashlight", "gunlight", "light", "peq", "枪灯", "手电"),
    "ammo": (
        "ammo",
        "cartridge",
        "casing",
        "bullet",
        "shell",
        "子弹",
        "弹壳",
        "弹药",
    ),
}


def _normalized(value: str) -> str:
    value = unicodedata.normalize("NFKC", value).casefold()
    return re.sub(r"[^a-z0-9\u4e00-\u9fff]+", "", value)


def _tokens(value: str) -> tuple[str, ...]:
    value = unicodedata.normalize("NFKC", value).casefold()
    return tuple(re.findall(r"[a-z]+|\d+|[\u4e00-\u9fff]+", value))


def _contains_tokens(haystack: tuple[str, ...], needle: tuple[str, ...]) -> bool:
    if not needle or len(needle) > len(haystack):
        return False
    width = len(needle)
    return any(haystack[index : index + width] == needle for index in range(len(haystack) - width + 1))


def _feature_from_image(source: Image.Image) -> np.ndarray:
    image = ImageOps.exif_transpose(source).convert("RGBA")
    rgb = image.convert("RGB").resize((128, 128), Image.Resampling.BILINEAR)
    array = np.asarray(rgb, dtype=np.float32) / 255.0
    alpha = np.asarray(image.getchannel("A").resize((128, 128), Image.Resampling.BILINEAR))
    gray = (
        array[:, :, 0] * 0.2126
        + array[:, :, 1] * 0.7152
        + array[:, :, 2] * 0.0722
    )
    gray_image = Image.fromarray(np.uint8(np.clip(gray, 0.0, 1.0) * 255), "L")
    gray_image = gray_image.filter(ImageFilter.GaussianBlur(1.2))
    gray_small = np.asarray(
        gray_image.resize((48, 48), Image.Resampling.BILINEAR),
        dtype=np.float32,
    ) / 255.0
    gray_small = (gray_small - float(gray_small.mean())) / (float(gray_small.std()) + 1e-6)
    coarse = np.asarray(
        gray_image.resize((64, 64), Image.Resampling.BILINEAR),
        dtype=np.float32,
    ) / 255.0
    gradient_y, gradient_x = np.gradient(coarse)
    edge = np.hypot(gradient_x, gradient_y)
    edge = np.asarray(
        Image.fromarray(np.uint8(np.clip(edge * 8.0, 0.0, 1.0) * 255), "L").resize(
            (24, 24), Image.Resampling.BILINEAR
        ),
        dtype=np.float32,
    ) / 255.0
    edge = (edge - float(edge.mean())) / (float(edge.std()) + 1e-6)
    alpha_small = np.asarray(
        Image.fromarray(alpha, "L").resize((24, 24), Image.Resampling.BILINEAR),
        dtype=np.float32,
    ) / 255.0
    alpha_small = (alpha_small - float(alpha_small.mean())) / (float(alpha_small.std()) + 1e-6)
    return np.concatenate(
        (gray_small.ravel(), edge.ravel(), alpha_small.ravel())
    ).astype(np.float32)


def _feature(path: Path) -> np.ndarray:
    with Image.open(path) as opened:
        return _feature_from_image(opened)


def _correlation(left: np.ndarray, right: np.ndarray) -> float:
    if left.shape != right.shape:
        return 0.0
    value = float(np.corrcoef(left, right)[0, 1])
    if math.isnan(value):
        return 0.0
    return max(0.0, min(1.0, value))


def _image_statistics(path: Path) -> tuple[dict[str, float], bool, int, int]:
    with Image.open(path) as opened:
        image = ImageOps.exif_transpose(opened)
        has_alpha = "A" in image.getbands()
        alpha_array = None
        if has_alpha:
            alpha_array = np.asarray(image.getchannel("A"))
        rgb = np.asarray(image.convert("RGB").resize((192, 192), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
        width, height = image.size
    means = rgb.mean(axis=(0, 1))
    stds = rgb.std(axis=(0, 1))
    gray = (
        rgb[:, :, 0] * 0.2126
        + rgb[:, :, 1] * 0.7152
        + rgb[:, :, 2] * 0.0722
    )
    stats = {
        "r": float(means[0]),
        "g": float(means[1]),
        "b": float(means[2]),
        "r_std": float(stds[0]),
        "g_std": float(stds[1]),
        "b_std": float(stds[2]),
        "black": float((gray < 0.025).mean()),
        "color_std": float(rgb.std(axis=2).mean()),
        "alpha_transparent": float((alpha_array < 250).mean()) if alpha_array is not None else 0.0,
    }
    return stats, has_alpha, width, height


def _filename_channel(path: Path) -> tuple[str | None, float]:
    stem = unicodedata.normalize("NFKC", path.stem).casefold()
    hits: list[tuple[str, float]] = []
    for channel, patterns in CHANNEL_NAME_PATTERNS.items():
        for pattern in patterns:
            if re.search(pattern, stem, flags=re.IGNORECASE):
                specificity = 0.99 if len(pattern) > 3 else 0.90
                hits.append((channel, specificity))
                break
    if not hits:
        return None, 0.0
    hits.sort(key=lambda item: item[1], reverse=True)
    if len(hits) > 1 and abs(hits[0][1] - hits[1][1]) < 0.03:
        return None, 0.45
    return hits[0]


def _visual_channel(stats: dict[str, float]) -> tuple[str, float]:
    if stats["black"] >= 0.72:
        return "Emissive", min(0.95, 0.62 + stats["black"] * 0.4)
    if (
        stats["b"] > 0.48
        and stats["g"] > 0.30
        and stats["b"] >= stats["r"] - 0.02
        and stats["r_std"] < 0.22
        and stats["g_std"] < 0.22
    ):
        return "Normal", 0.72
    if (
        stats["b"] > 0.78
        and stats["b_std"] < 0.12
        and stats["g"] > 0.34
    ):
        return "MetallicSmoothness", 0.72
    return "Albedo", 0.56


def classify_channel(path: Path, stats: dict[str, float]) -> tuple[str | None, float]:
    named, name_confidence = _filename_channel(path)
    visual, visual_confidence = _visual_channel(stats)
    if named:
        return named, max(name_confidence, visual_confidence if named == visual else 0.0)
    return visual, visual_confidence


def _filename_base(path: Path) -> str:
    stem = unicodedata.normalize("NFKC", path.stem)
    stem = re.sub(
        r"[_\s-]+(?:base[\s_-]*colou?r|albedo|metallic(?:_raw)?|metallic[\s_-]*smoothness|"
        r"metallicgloss|normal(?:_raw)?|emissive|emission|glow)"
        r"(?:[_\s-]+(?:s?rgb|linear|raw))?$",
        "",
        stem,
        flags=re.IGNORECASE,
    )
    return stem


def _filename_part_score(
    path: Path,
    item: CatalogItem,
    part_id: str,
    material: str,
    asset: int,
    known_materials: dict[str, str],
    known_material_tokens: dict[tuple[str, ...], str],
) -> float:
    base = _normalized(_filename_base(path))
    base_tokens = _tokens(_filename_base(path))
    haystack = _normalized(path.stem)
    material_key = _normalized(material)
    material_tokens = _tokens(material)
    part_key = _normalized(part_id)
    item_key = _normalized(item.name)
    score = 0.0
    known_material = known_materials.get(base)
    if known_material is not None and _normalized(known_material) != material_key:
        return 0.0
    token_material = known_material_tokens.get(base_tokens)
    if token_material is not None and _normalized(token_material) != material_key:
        return 0.0
    if material_key and base == material_key:
        score = max(score, 0.995)
    elif material_key and base.startswith(material_key):
        score = max(score, 0.90)
    elif _contains_tokens(base_tokens, material_tokens):
        score = max(score, 0.90)
    elif material_key and material_key in haystack:
        score = max(score, 0.72)
    if str(asset) in path.stem:
        score = max(score, 0.70)
    if part_key and len(part_key) >= 3 and part_key in haystack:
        score = max(score, 0.52)
    part = next(part for part in item.parts if part.id == part_id)
    for token in ROLE_TOKENS.get(part.role, ()):
        if _normalized(token) in haystack:
            score = max(score, 0.36)
            break
    if item_key and item_key in haystack:
        score = max(score, 0.52)
    return score


def _list_images(folder: Path) -> list[Path]:
    return sorted(
        (
            path
            for path in folder.rglob("*")
            if path.is_file() and path.suffix.casefold() in SUPPORTED_EXTENSIONS
        ),
        key=lambda path: str(path).casefold(),
    )


@dataclass(slots=True)
class _Baseline:
    part_id: str
    channel: str
    feature: np.ndarray | None


@dataclass(slots=True)
class _Proposal:
    score: float
    path: Path
    part_id: str
    channel: str
    part_confidence: float
    channel_confidence: float
    warnings: list[str]


def analyze_texture_folder(
    game: GameInstall,
    item: CatalogItem,
    folder: Path,
    enabled_parts: set[str] | None = None,
) -> MatchAnalysis:
    if not folder.is_dir():
        raise NotADirectoryError(folder)
    analysis = MatchAnalysis(item_id=item.id, folder=folder)
    active_parts = item.active_parts(enabled_parts)
    images = _list_images(folder)
    if not images:
        raise ValueError("所选目录中没有受支持的贴图文件")

    info_by_part = {}
    known_materials: dict[str, str] = {}
    known_material_tokens: dict[tuple[str, ...], str] = {}
    for catalog_item in load_catalog().values():
        for catalog_part in catalog_item.parts:
            known_materials[_normalized(catalog_part.material)] = catalog_part.material
            known_material_tokens[_tokens(catalog_part.material)] = catalog_part.material
    for part in active_parts:
        try:
            info = inspect_material(game, part)
            info_by_part[part.id] = info
            analysis.warnings.extend(f"{part.display_name}: {warning}" for warning in info.warnings)
        except Exception as exc:
            analysis.warnings.append(f"{part.display_name}: 无法读取材质 {part.material}: {exc}")
            info_by_part[part.id] = None

    baseline_features: dict[tuple[str, str], np.ndarray | None] = {}
    for part in active_parts:
        info = info_by_part.get(part.id)
        if info is None:
            continue
        channels = available_channels(info)
        for channel in channels:
            try:
                image = extract_texture(game, part, channel)
                if image is None:
                    baseline_features[(part.id, channel)] = None
                    continue
                baseline_features[(part.id, channel)] = _feature_from_image(image)
            except Exception:
                baseline_features[(part.id, channel)] = None

    candidate_features: dict[Path, np.ndarray] = {}
    proposals: list[_Proposal] = []
    for path in images:
        try:
            stats, has_alpha, width, height = _image_statistics(path)
            channel_guess, channel_conf = classify_channel(path, stats)
            named_channel, named_channel_confidence = _filename_channel(path)
            feature = _feature(path)
            candidate_features[path] = feature
            warnings: list[str] = []
            if width != height:
                warnings.append(f"非方形贴图 {width}×{height}")
            if width > 4096 or height > 4096:
                warnings.append(f"超大贴图 {width}×{height}")
            if path.suffix.casefold() in {".jpg", ".jpeg"}:
                warnings.append("JPEG 为有损格式，建议提供 PNG 或 TGA")
            analysis.candidates.append(
                TextureCandidate(
                    path=path,
                    width=width,
                    height=height,
                    has_alpha=has_alpha,
                    channel_guess=channel_guess,
                    channel_confidence=channel_conf,
                    warnings=warnings,
                )
            )
            for part in active_parts:
                info = info_by_part.get(part.id)
                if info is None:
                    continue
                part_name_score = _filename_part_score(
                    path,
                    item,
                    part.id,
                    part.material,
                    part.asset,
                    known_materials,
                    known_material_tokens,
                )
                if named_channel and part_name_score < 0.85:
                    continue
                channels = available_channels(info)
                if channel_guess and channel_conf >= 0.50:
                    channels = [channel_guess] if channel_guess in channels else []
                for channel in channels:
                    baseline = baseline_features.get((part.id, channel))
                    structural = _correlation(feature, baseline) if baseline is not None else 0.0
                    if named_channel:
                        channel_match = (
                            max(named_channel_confidence, channel_conf)
                            if channel == named_channel
                            else 0.08
                        )
                    else:
                        channel_match = max(0.45, structural)
                    part_score = max(part_name_score, structural)
                    total = part_score * 0.68 + channel_match * 0.32
                    if part_score < 0.075:
                        continue
                    if total < 0.10:
                        continue
                    proposals.append(
                        _Proposal(
                            score=total,
                            path=path,
                            part_id=part.id,
                            channel=channel,
                            part_confidence=part_score,
                            channel_confidence=channel_match,
                            warnings=warnings,
                        )
                    )
        except Exception as exc:
            analysis.warnings.append(f"{path.name}: 无法读取贴图: {exc}")

    assigned_paths: set[Path] = set()
    assigned_slots: set[tuple[str, str]] = set()
    for proposal in sorted(proposals, key=lambda value: value.score, reverse=True):
        slot_key = (proposal.part_id, proposal.channel)
        if proposal.path in assigned_paths or slot_key in assigned_slots:
            continue
        if proposal.score < 0.27:
            continue
        assigned_paths.add(proposal.path)
        assigned_slots.add(slot_key)
        current = analysis.slots.setdefault(proposal.part_id, {})
        current[proposal.channel] = SlotMatch(
            part_id=proposal.part_id,
            channel=proposal.channel,
            path=proposal.path,
            part_confidence=proposal.part_confidence,
            channel_confidence=proposal.channel_confidence,
            source="automatic",
            warnings=list(proposal.warnings),
        )

    for part in active_parts:
        info = info_by_part.get(part.id)
        channels = available_channels(info) if info is not None else []
        slots = analysis.slots.setdefault(part.id, {})
        missing = []
        for channel in channels:
            if channel not in slots or slots[channel].path is None:
                slots[channel] = SlotMatch(part_id=part.id, channel=channel)
                missing.append(channel)
        if missing:
            analysis.missing_channels[part.id] = missing
            if len(missing) == len(channels):
                analysis.missing_parts.append(part.id)

    analysis.unknown_files = sorted(
        set(images) - assigned_paths,
        key=lambda path: str(path).casefold(),
    )
    if analysis.unknown_files:
        analysis.warnings.append(f"{len(analysis.unknown_files)} 张图片未能自动归类")
    low = [
        match
        for slots in analysis.slots.values()
        for match in slots.values()
        if match.needs_confirmation
    ]
    if low:
        analysis.warnings.append(f"{len(low)} 项自动匹配置信度较低，请人工确认")
    if analysis.missing_parts:
        analysis.warnings.append(f"{len(analysis.missing_parts)} 个部件没有识别到可用贴图")
    return analysis


def refresh_missing(
    analysis: MatchAnalysis,
    item: CatalogItem,
    enabled_parts: set[str] | None = None,
) -> None:
    analysis.missing_parts.clear()
    analysis.missing_channels.clear()
    for part in item.active_parts(enabled_parts):
        slots = analysis.slots.get(part.id, {})
        missing = [channel for channel, match in slots.items() if match.path is None]
        if missing:
            analysis.missing_channels[part.id] = missing
        if slots and len(missing) == len(slots):
            analysis.missing_parts.append(part.id)
    analysis.warnings = [
        warning
        for warning in analysis.warnings
        if "个部件没有识别到" not in warning and "项自动匹配" not in warning
    ]
    low_count = sum(
        1
        for slots in analysis.slots.values()
        for match in slots.values()
        if match.needs_confirmation
    )
    if low_count:
        analysis.warnings.append(f"{low_count} 项自动匹配置信度较低，请人工确认")
    if analysis.missing_parts:
        analysis.warnings.append(f"{len(analysis.missing_parts)} 个部件没有识别到可用贴图")


def manual_assign(
    analysis: MatchAnalysis,
    item: CatalogItem,
    part_id: str,
    channel: str,
    path: Path | None,
) -> None:
    slots = analysis.slots.setdefault(part_id, {})
    if path is not None:
        for other_part, other_slots in analysis.slots.items():
            for other_channel, match in other_slots.items():
                if match.path == path and (other_part, other_channel) != (part_id, channel):
                    match.path = None
                    match.source = "automatic"
                    match.part_confidence = 0.0
                    match.channel_confidence = 0.0
    slots[channel] = SlotMatch(
        part_id=part_id,
        channel=channel,
        path=path,
        part_confidence=1.0 if path else 0.0,
        channel_confidence=1.0 if path else 0.0,
        source="manual",
    )
    refresh_missing(analysis, item)
