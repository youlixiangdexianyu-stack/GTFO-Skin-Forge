from __future__ import annotations

import json
import re
from pathlib import Path

import pytest

import gtfo_skin_forge.obj_export as obj_export
from gtfo_skin_forge.mesh_preview import PreviewMesh
from gtfo_skin_forge.models import CatalogItem, GameInstall, PartSpec, SkinProject
from gtfo_skin_forge.obj_export import (
    ObjExportError,
    ObjExportMode,
    ObjExportRequest,
    export_obj,
)


def _mesh_file(path: Path, x: float) -> Path:
    path.write_text(
        "\n".join(
            (
                f"v {x:.6f} 0 0",
                f"v {x + 1:.6f} 0 0",
                f"v {x:.6f} 1 0",
                "vt 0 0",
                "vt 1 0",
                "vt 0 1",
                "vn 0 0 1",
                "f 1/1/1 2/2/1 3/3/1",
            )
        )
        + "\n",
        encoding="utf-8",
    )
    return path


def _fixture_item_and_meshes(tmp_path: Path):
    parts = [
        PartSpec("front", "front", 101, "Front_Rifle/Test"),
        PartSpec("receiver", "receiver", 102, "Receiver_Rifle_Test"),
        PartSpec("ammo_bullet", "ammo", 1, "Casing_9mm", optional=True),
        PartSpec("ammo_shell", "ammo", 1, "Casing_9mm", optional=True),
    ]
    item = CatalogItem(
        id="test_weapon",
        name="Test: Weapon",
        category="步枪",
        aliases=[],
        parts=parts,
    )
    mesh_paths = [
        _mesh_file(tmp_path / f"{index}.obj", float(index))
        for index in range(5)
    ]
    meshes = [
        PreviewMesh(
            "front",
            parts[0].material,
            1,
            "Front_Rifle_Test",
            mesh_paths[0],
            (0, 0, 0),
            (1, 1, 0),
            (0.25, 0.5, 0.75),
        ),
        PreviewMesh(
            "front",
            parts[0].material,
            2,
            "Front_Rifle_Test_2",
            mesh_paths[1],
            (0, 0, 0),
            (1, 1, 0),
            (0.25, 0.5, 0.75),
        ),
        PreviewMesh(
            "receiver",
            parts[1].material,
            3,
            "Receiver_Rifle_Test",
            mesh_paths[2],
            (0, 0, 0),
            (1, 1, 0),
            (0, 0, 0),
        ),
        PreviewMesh(
            "ammo_bullet",
            parts[2].material,
            4,
            "Cartridge_9mm",
            mesh_paths[3],
            (0, 0, 0),
            (1, 1, 0),
            (0, 0, 0),
        ),
        PreviewMesh(
            "ammo_shell",
            parts[3].material,
            5,
            "Shell_9mm",
            mesh_paths[4],
            (0, 0, 0),
            (1, 1, 0),
            (0, 0, 0),
        ),
    ]
    return item, meshes


def _request(tmp_path: Path, item, mode: ObjExportMode, selected=None):
    return ObjExportRequest(
        game=GameInstall(
            root=tmp_path / "game",
            data_dir=tmp_path / "game/GTFO_Data",
            source="test",
        ),
        item=item,
        project=SkinProject(
            name="Test Weapon",
            item_id=item.id,
            enabled_parts=["ammo_bullet", "ammo_shell"],
        ),
        output_dir=tmp_path / "output",
        mode=mode,
        selected_part_ids=selected
        if selected is not None
        else {part.id for part in item.parts},
    )


def test_combined_obj_preserves_independent_objects_and_materials(
    tmp_path,
    monkeypatch,
):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )

    result = export_obj(_request(tmp_path, item, ObjExportMode.COMBINED))

    obj_text = result.output_files[0].read_text(encoding="utf-8")
    assert obj_text.count("\no ") == 4
    assert obj_text.count("\ng ") == 4
    assert obj_text.count("\nusemtl ") == 4
    assert "Front_Rifle_Test" in result.texture_set_names
    assert "Casing_9mm__1_ammo_bullet" in result.texture_set_names
    assert "Casing_9mm__1_ammo_shell" in result.texture_set_names
    assert result.object_count == 4
    # Two meshes for front stay in one object and remain separately addressable.
    assert obj_text.count("\nf ") == 5
    assert "v 1.250000000 0.500000000 0.750000000" in obj_text
    assert not list(result.mtl_path.parent.glob("*.png"))

    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    assert len(manifest["parts"]) == 4
    assert manifest["parts"][0]["texture_set"] == "Front_Rifle_Test"
    assert re.fullmatch(
        r"\d{4}-\d{2}-\d{2}-\d{6}-Test_ Weapon\.OBJ(?:-\d+)?",
        result.mtl_path.parent.name,
    )
    assert result.mtl_path.parent.parent == (tmp_path / "output").resolve()


def test_combined_obj_uses_file_wide_vertex_uv_and_normal_indices(
    tmp_path,
    monkeypatch,
):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )

    result = export_obj(_request(tmp_path, item, ObjExportMode.COMBINED))

    vertices = []
    texcoords = []
    normals = []
    faces = []
    for line in result.output_files[0].read_text(encoding="utf-8").splitlines():
        fields = line.split()
        if fields[:1] == ["v"]:
            vertices.append(tuple(map(float, fields[1:4])))
        elif fields[:1] == ["vt"]:
            texcoords.append(tuple(map(float, fields[1:3])))
        elif fields[:1] == ["vn"]:
            normals.append(tuple(map(float, fields[1:4])))
        elif fields[:1] == ["f"]:
            faces.extend(fields[1:])

    assert len(faces) == 15
    for face_index in range(5):
        indexes = [
            tuple(int(value) for value in face.split("/"))
            for face in faces[face_index * 3 : face_index * 3 + 3]
        ]
        assert [index[0] for index in indexes] == [
            face_index * 3 + 1,
            face_index * 3 + 2,
            face_index * 3 + 3,
        ]
        assert [index[1] for index in indexes] == [
            face_index * 3 + 1,
            face_index * 3 + 2,
            face_index * 3 + 3,
        ]
        assert {index[2] for index in indexes} == {face_index + 1}
        assert vertices[indexes[0][0] - 1][0] == (
            face_index + 0.25 if face_index < 2 else float(face_index)
        )
        assert texcoords[indexes[0][1] - 1] == (0.0, 0.0)
        assert normals[indexes[0][2] - 1] == (0.0, 0.0, 1.0)


def test_separate_obj_export_writes_one_file_per_part(tmp_path, monkeypatch):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )

    result = export_obj(_request(tmp_path, item, ObjExportMode.SEPARATE))

    assert re.fullmatch(
        r"\d{4}-\d{2}-\d{2}-\d{6}-Test_ Weapon\.OBJ(?:-\d+)?",
        result.mtl_path.parent.name,
    )
    assert len(result.output_files) == 4
    for output in result.output_files:
        text = output.read_text(encoding="utf-8")
        assert text.count("\no ") == 1
        assert text.count("\ng ") == 1
        assert text.count("\nusemtl ") == 1
    assert result.mtl_path.is_file()
    assert result.manifest_path.is_file()


def test_obj_export_folder_uses_official_weapon_full_name(tmp_path, monkeypatch):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    item.id = "sniper"
    item.name = "大狙"
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )

    result = export_obj(_request(tmp_path, item, ObjExportMode.COMBINED))

    assert re.fullmatch(
        r"\d{4}-\d{2}-\d{2}-\d{6}-狙击枪 · Köning PR 11\.OBJ(?:-\d+)?",
        result.mtl_path.parent.name,
    )
    manifest = json.loads(result.manifest_path.read_text(encoding="utf-8"))
    assert manifest["item"]["name"] == "狙击枪 · Köning PR 11"


def test_repeated_obj_export_never_reuses_output_folder(tmp_path, monkeypatch):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )

    first = export_obj(_request(tmp_path, item, ObjExportMode.COMBINED))
    second = export_obj(_request(tmp_path, item, ObjExportMode.COMBINED))

    assert first.mtl_path.parent != second.mtl_path.parent
    assert first.mtl_path.parent.is_dir()
    assert second.mtl_path.parent.is_dir()


def test_obj_export_only_includes_checked_parts(tmp_path, monkeypatch):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (meshes, None),
    )
    request = _request(
        tmp_path,
        item,
        ObjExportMode.COMBINED,
        selected={"receiver", "ammo_shell"},
    )

    result = export_obj(request)

    obj_text = result.output_files[0].read_text(encoding="utf-8")
    assert "Receiver_Rifle_Test" in obj_text
    assert "usemtl Casing_9mm\n" in obj_text
    assert "ammo_bullet" not in obj_text
    assert "front" not in obj_text


def test_obj_export_rejects_empty_or_missing_geometry(tmp_path, monkeypatch):
    item, meshes = _fixture_item_and_meshes(tmp_path)
    with pytest.raises(ObjExportError, match="没有勾选"):
        export_obj(
            _request(
                tmp_path,
                item,
                ObjExportMode.COMBINED,
                selected=set(),
            )
        )
    monkeypatch.setattr(
        obj_export,
        "extract_weapon_meshes",
        lambda *args, **kwargs: (
            [mesh for mesh in meshes if mesh.part_id != "ammo_shell"],
            None,
        ),
    )
    with pytest.raises(ObjExportError, match="无法取得网格"):
        export_obj(
            _request(
                tmp_path,
                item,
                ObjExportMode.COMBINED,
            )
        )
