from __future__ import annotations

from PIL import Image

from gtfo_skin_forge.assets import _normalize_image, _pixel_size


def test_resolution_modes_keep_source_aspect_ratio():
    landscape = Image.new("RGBA", (2048, 1024))
    portrait = Image.new("RGBA", (512, 1024))
    baseline = Image.new("RGBA", (256, 256))

    assert _pixel_size("1k", landscape, baseline) == (1024, 512)
    assert _pixel_size("2k", portrait, baseline) == (1024, 2048)
    assert _pixel_size("source", landscape, baseline) == (256, 128)
    assert _pixel_size("user", landscape, baseline) == (2048, 1024)


def test_normalized_texture_is_scaled_proportionally(tmp_path):
    source = tmp_path / "rectangular.png"
    Image.new("RGBA", (400, 200), (255, 0, 0, 255)).save(source)

    result = _normalize_image(source, (1024, 1024))

    assert result.size == (1024, 512)
