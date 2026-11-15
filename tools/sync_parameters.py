"""Sync the project's JSON parameters with a MonsterIntentRandomizer game config."""

import argparse
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path


FILES = {"Settings": "settings.json", "Profiles": "monster-profiles.json"}
HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent if HERE.name == "tools" else HERE


def default_game_config():
    appdata = os.environ.get("APPDATA")
    return (
        Path(appdata) / "SlayTheSpire2" / "mod_data" / "MonsterIntentRandomizer" / "config.json"
        if appdata else Path("config.json")
    )


def project_revision():
    source = PROJECT / "mod" / "ModEntry.cs"
    if source.exists():
        match = re.search(r"SettingsRevision\s*=\s*(\d+)", source.read_text(encoding="utf-8"))
        if match:
            return int(match.group(1))
    return None


def read_json(path):
    with path.open(encoding="utf-8-sig") as file:
        value = json.load(file)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object in {path}")
    return value


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        shutil.copy2(path, path.with_name(path.name + ".bak"))
    with tempfile.NamedTemporaryFile(
        mode="w", encoding="utf-8", dir=path.parent, delete=False
    ) as file:
        temporary = Path(file.name)
        try:
            json.dump(data, file, ensure_ascii=False, indent=2, allow_nan=False)
            file.write("\n")
            file.close()
            temporary.replace(path)
        finally:
            file.close()
            temporary.unlink(missing_ok=True)


def sync(command, game_config, data_dir, scope, revision=None, dry_run=False):
    sections = FILES if scope == "both" else (scope.title(),)
    if revision is None:
        revision = project_revision()

    exists = game_config.exists()
    if not exists and command != "push":
        raise FileNotFoundError(game_config)
    game = read_json(game_config) if exists else {"Enabled": True, "ShowIntentDetails": True}
    saved_revision = game.get("Revision")
    revision_changed = revision is not None and saved_revision != revision
    if revision_changed and (command == "pull" or (command == "push" and scope != "both")):
        raise ValueError("Schema revisions differ. Save settings once in the updated game before syncing.")

    messages = []
    updates = {}
    for section in sections:
        path = data_dir / FILES[section]
        if game_config.resolve() == path.resolve():
            raise ValueError("Choose a game config file separate from the project JSON files.")
        project_value = read_json(path) if path.exists() else None
        saved_value = game.get(section)
        if isinstance(project_value, dict) and project_value == saved_value:
            messages.append(f"{section}: equal")
        elif command == "diff":
            messages.append(f"{section}: different")
        else:
            value = saved_value if command == "pull" else project_value
            if not isinstance(value, dict):
                raise ValueError(f"Missing input JSON object for {section}")
            updates[section] = value

    if command == "pull":
        for section, value in updates.items():
            path = data_dir / FILES[section]
            if not dry_run:
                write_json(path, value)
            messages.append(f"{section} -> {path}")
    elif command == "push" and (updates or revision_changed):
        if not exists and scope != "both":
            raise ValueError("Use --scope both to create a new game config.")
        if not exists and revision is None:
            raise ValueError("Pass --revision to create a game config from a standalone copy.")
        game.update(updates)
        if revision is not None:
            game["Revision"] = revision
        if not dry_run:
            write_json(game_config, game)
        messages.append(f"{', '.join(updates) or 'Revision'} -> {game_config}")
    return messages


def show_window():
    import tkinter as tk
    from tkinter import filedialog, ttk

    window = tk.Tk()
    window.title("怪物意图随机 · 参数同步")
    window.minsize(640, 240)
    frame = ttk.Frame(window, padding=16)
    frame.pack(fill="both", expand=True)
    frame.columnconfigure(1, weight=1)

    def path_row(row, label, initial, choose):
        value = tk.StringVar(value=str(initial))
        ttk.Label(frame, text=label).grid(row=row, column=0, sticky="w", padx=(0, 8), pady=5)
        ttk.Entry(frame, textvariable=value).grid(row=row, column=1, sticky="ew", pady=5)

        def browse():
            selected = choose()
            if selected:
                value.set(selected)

        ttk.Button(frame, text="浏览…", command=browse).grid(row=row, column=2, padx=(8, 0))
        return value

    game = path_row(0, "游戏配置", default_game_config(), lambda: filedialog.askopenfilename(
        parent=window, initialdir=str(Path(game.get()).parent), filetypes=[("JSON 文件", "*.json")]
    ))
    data = path_row(1, "项目 data 目录", PROJECT / "data", lambda: filedialog.askdirectory(
        parent=window, initialdir=data.get()
    ))
    scopes = {"全部": "both", "规则参数": "settings", "怪物画像": "profiles"}
    scope = ttk.Combobox(frame, values=list(scopes), state="readonly", width=12)
    scope.current(0)
    ttk.Label(frame, text="同步范围").grid(row=2, column=0, sticky="w", pady=5)
    scope.grid(row=2, column=1, sticky="w")

    message = tk.StringVar(value="请选择比较或同步方向。")
    ttk.Label(frame, textvariable=message, wraplength=600).grid(
        row=4, column=0, columnspan=3, sticky="w", pady=(12, 0)
    )

    def run(command):
        try:
            results = sync(command, Path(game.get().strip()), Path(data.get().strip()), scopes[scope.get()])
            message.set("\n".join(results))
        except (OSError, ValueError) as error:
            message.set(str(error))

    buttons = ttk.Frame(frame)
    buttons.grid(row=3, column=0, columnspan=3, sticky="w", pady=(10, 0))
    for label, command in (("比较", "diff"), ("从游戏拉取", "pull"), ("推送到游戏", "push")):
        ttk.Button(buttons, text=label, command=lambda cmd=command: run(cmd)).pack(side="left", padx=(0, 8))
    window.mainloop()


def main():
    if len(sys.argv) == 1:
        show_window()
        return

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("diff", "pull", "push"), nargs="?", default="diff")
    parser.add_argument("--game-config", type=Path, default=default_game_config())
    parser.add_argument("--data-dir", type=Path, default=PROJECT / "data")
    parser.add_argument("--scope", choices=("settings", "profiles", "both"), default="both")
    parser.add_argument("--revision", type=int, help="Schema revision, for standalone copies")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    try:
        for result in sync(args.command, args.game_config, args.data_dir, args.scope, args.revision, args.dry_run):
            print(result)
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
