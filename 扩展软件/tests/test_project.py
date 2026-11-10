from __future__ import annotations

from gtfo_skin_forge.models import EmissiveSettings, SkinProject
from gtfo_skin_forge.project_store import load_project, save_project


def test_project_roundtrip(tmp_path):
    project = SkinProject(
        name="Test Skin",
        game_root=r"D:\Steam\steamapps\common\GTFO",
        item_id="helgun",
        source_folder=str(tmp_path / "textures"),
        resolution_mode="2k",
        assignments={"front": {"Albedo": str(tmp_path / "a.png")}},
        disabled_parts=["stock"],
        emissive={"front": EmissiveSettings(True, (0.2, 0.3, 0.4, 1.0), 2.5)},
        enabled_parts=["flashlight"],
    )
    path = tmp_path / "skin.gtfoskin"
    save_project(project, path)
    loaded = load_project(path)
    assert loaded.name == project.name
    assert loaded.assignments == project.assignments
    assert loaded.disabled_parts == ["stock"]
    assert loaded.emissive["front"].intensity == 2.5
    assert loaded.enabled_parts == ["flashlight"]


def test_old_project_source_mode_migrates_to_user_size():
    loaded = SkinProject.from_dict(
        {
            "schema_version": 1,
            "name": "old",
            "item_id": "helgun",
            "resolution_mode": "source",
        }
    )
    assert loaded.resolution_mode == "user"
