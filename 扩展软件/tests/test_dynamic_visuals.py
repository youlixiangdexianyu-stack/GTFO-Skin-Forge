from __future__ import annotations

import json
from pathlib import Path

from PIL import Image

from gtfo_skin_forge.dynamic_visuals import (
    DynamicLayer,
    DynamicVisualProject,
    effective_timeline,
    frame_for_layer,
    import_image_layer,
    layer_frames,
    load_dynamic_project,
    render_text_layer,
    save_dynamic_project,
)


def test_dynamic_project_round_trip(tmp_path: Path) -> None:
    project = DynamicVisualProject(name="demo")
    project.layers.append(
        DynamicLayer(
            name="sticker",
            channel="Normal",
            uv_x=0.1,
            uv_y=0.2,
            uv_width=0.3,
            uv_height=0.4,
        )
    )
    path = save_dynamic_project(project, tmp_path / "demo.gtfdyn")
    loaded = load_dynamic_project(path)
    assert loaded.name == "demo"
    assert loaded.layers[0].channel == "Normal"
    assert loaded.layers[0].uv_width == 0.3


def test_dynamic_timeline_uses_max_layer_fps() -> None:
    project = DynamicVisualProject()
    project.layers = [
        DynamicLayer(kind="sequence", media_paths=["a", "b", "c"], fps=6),
        DynamicLayer(kind="sequence", media_paths=["a", "b", "c", "d"], fps=12),
    ]
    fps, frames = effective_timeline(project)
    assert fps == 12
    assert frames == 6


def test_image_import_copies_into_private_media(tmp_path: Path) -> None:
    source = tmp_path / "source.png"
    Image.new("RGBA", (16, 16), (255, 0, 0, 255)).save(source)
    project_path = tmp_path / "project.gtfdyn"
    project = DynamicVisualProject()
    layer = import_image_layer(
        project,
        project_path,
        source,
        channel="Albedo",
        part_id="front",
    )
    assert layer.media_paths
    copied = project_path.parent / layer.media_paths[0]
    assert copied.is_file()
    assert copied != source


def test_text_and_sequence_frame_selection(tmp_path: Path) -> None:
    layer = DynamicLayer(
        kind="text",
        text="GTFO",
        font_size=32,
        text_color="#FFFFFFFF",
    )
    image = render_text_layer(layer)
    assert image.mode == "RGBA"
    assert image.width > 16
    frames = [
        Image.new("RGBA", (2, 2), (index, 0, 0, 255))
        for index in range(3)
    ]
    layer.kind = "sequence"
    assert frame_for_layer(layer, frames, 0.21).getpixel((0, 0))[0] == 2


def test_layer_schema_is_json_serializable(tmp_path: Path) -> None:
    project = DynamicVisualProject(name="json")
    path = save_dynamic_project(project, tmp_path / "json.gtfdyn")
    value = json.loads(path.read_text(encoding="utf-8"))
    assert value["schema_version"] == 1
    assert value["item_id"] == "auto_pistol"
