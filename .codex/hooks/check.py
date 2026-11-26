"""Claude hooks: validate edited source syntax and report whitespace errors."""
import ast
import json
import os
from pathlib import Path
import subprocess
import sys


def check(event):
    root = Path(os.environ.get("CLAUDE_PROJECT_DIR", Path(__file__).resolve().parents[2]))
    if event.get("hook_event_name") == "Stop":
        if event.get("stop_hook_active"):
            return
        subprocess.run(["git", "diff", "--check"], cwd=root, check=True)
        subprocess.run(["git", "diff", "--cached", "--check"], cwd=root, check=True)
        return

    filename = event.get("tool_input", {}).get("file_path")
    if not filename:
        return
    path = Path(filename)
    if not path.is_absolute():
        path = Path(event.get("cwd", str(root))) / path
    path = path.resolve()
    if not path.is_relative_to(root.resolve()) or not path.is_file():
        return
    if path.suffix == ".py":
        ast.parse(path.read_text(encoding="utf-8-sig"), filename=str(path))
    elif path.suffix == ".json":
        json.loads(path.read_text(encoding="utf-8-sig"))
    elif path.suffix == ".sh":
        subprocess.run(["bash", "-n", str(path)], check=True)


if __name__ == "__main__":
    try:
        check(json.load(sys.stdin))
    except (OSError, ValueError, SyntaxError, subprocess.CalledProcessError) as error:
        print(f"SIWS 검사 실패: {error}", file=sys.stderr)
        sys.exit(2)
