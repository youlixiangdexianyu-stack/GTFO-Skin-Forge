from __future__ import annotations

import traceback
from pathlib import Path

from .assets import BuildContext, affected_by_asset, build_assets
from .catalog import load_catalog
from .game_locator import best_game_install
from .project_store import load_project


def build_project_cli(argv: list[str]) -> int:
    try:
        index = argv.index("--build-project")
        project_path = Path(argv[index + 1])
        output = Path(argv[index + 2])
    except (ValueError, IndexError):
        return 2
    log_path = output / "cli-build.log"
    output.mkdir(parents=True, exist_ok=True)
    try:
        project = load_project(project_path)
        game = best_game_install(project.game_root)
        if game is None:
            raise FileNotFoundError("GTFO installation not found")
        item = load_catalog().get(project.item_id)
        if item is None:
            raise KeyError(f"Unknown catalog item: {project.item_id}")
        affected = affected_by_asset(
            load_catalog().values(),
            [part.asset for part in item.parts if part.id in project.assignments],
        )
        result = build_assets(
            BuildContext(
                game=game,
                item=item,
                project=project,
                output_dir=output,
                affected_items=affected,
            )
        )
        log_path.write_text(
            "\n".join(
                [
                    "BUILD_OK",
                    f"output={result.output_dir}",
                    f"files={len(result.files)}",
                    *(str(file.output_path) for file in result.files),
                ]
            ),
            encoding="utf-8",
        )
        return 0
    except Exception:
        log_path.write_text(traceback.format_exc(), encoding="utf-8")
        return 1

