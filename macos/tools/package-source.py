"""Create a minimal Mac handoff archive on Windows or macOS; no credentials."""
from pathlib import Path
import hashlib
import zipfile

repo = Path(__file__).resolve().parents[2]
version = (repo / "VERSION").read_text(encoding="utf-8").strip()
root = f"CodexUsageBar-v{version}-macOS-arm64-source"
out = repo / "dist" / "macos"
out.mkdir(parents=True, exist_ok=True)
archive = out / f"{root}.zip"
files = [p for p in (repo / "macos").rglob("*") if p.is_file()
         and not any(part in {".build", ".swiftpm", "__pycache__"} for part in p.relative_to(repo / "macos").parts)]
files += [repo / name for name in ["VERSION", "LICENSE", "THIRD-PARTY-NOTICES.md", "INSTALL-MACOS.md",
                                  "installer/package-quota.js", "assets/codex-usage-bar-logo.png",
                                  ".github/workflows/macos-arm64.yml", ".gitattributes"]]
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for path in sorted(files):
        relative = path.relative_to(repo).as_posix()
        info = zipfile.ZipInfo(f"{root}/{relative}")
        info.create_system = 3
        info.external_attr = ((0o100755 if path.suffix in {".sh", ".command"} else 0o100644) << 16)
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, path.read_bytes())
    z.writestr(f"{root}/README.md", (repo / "INSTALL-MACOS.md").read_bytes())
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
print(archive)
print("SHA256:", digest)
