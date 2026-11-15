"""Build a reproducible, clean source download, excluding runtime data and Site identity."""
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

root = Path(__file__).resolve().parents[1]
output = root / "dist" / "CineVault-MVC.zip"
excluded = {".git", ".openai", ".runtime", "node_modules", "bin", "obj", "App_Data", "__pycache__", "artifacts", ".secrets", "downloads"}
roots = ["README.md", "package.json", ".gitignore", "Dockerfile", "compose.yaml", ".dockerignore", ".env.example", "dist", "server", "tests", "scripts"]
with ZipFile(output, "w", ZIP_DEFLATED) as archive:
    for name in roots:
        source = root / name
        files = [source] if source.is_file() else sorted(source.rglob("*"))
        for file in files:
            relative = file.relative_to(root)
            if not file.is_file() or file == output or excluded.intersection(relative.parts):
                continue
            if file.suffix in {".log", ".tmp"}:
                continue
            archive.write(file, "CineVault/" + relative.as_posix())
with ZipFile(output) as archive:
    assert archive.testzip() is None
    assert "CineVault/README.md" in archive.namelist()
print(f"{output} ({output.stat().st_size} bytes)")
