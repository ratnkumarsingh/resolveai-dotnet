"""Prints architecture-and-design.html to ../ResolveAI-Architecture-and-Design.pdf with an installed Chrome.

Two passes: the first finds the page each section starts on, the second fills in the contents page.

    python docs/design/build_pdf.py
"""
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

from pypdf import PdfReader

HERE = Path(__file__).resolve().parent
SOURCE = HERE / "architecture-and-design.html"
OUTPUT = HERE.parent / "ResolveAI-Architecture-and-Design.pdf"
CHROME = next(
    p for p in [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        shutil.which("google-chrome") or "",
        shutil.which("chromium") or "",
    ] if p and Path(p).exists()
)
HEADINGS = {
    "@P1@": "1. Executive summary", "@P2@": "2. Architecture", "@P3@": "3. Workflows", "@P4@": "4. Components",
    "@P5@": "5. MCP servers", "@P6@": "6. Data model and API", "@P7@": "7. Design decisions",
    "@P8@": "8. Security and threat model", "@P9@": "9. Deployment and operations", "@P10@": "10. Testing",
    "@P11@": "11. Risks, limitations and gaps", "@P12@": "12. Roadmap", "@P13@": "13. Target architecture",
    "@PA@": "Appendix A.",
}


def render(html: str, pdf: Path, work: Path) -> None:
    page = work / "doc.html"
    page.write_text(html, encoding="utf-8")
    # A separate profile, so this never touches a Chrome the person has open.
    subprocess.run(
        [CHROME, "--headless=new", "--disable-gpu", f"--user-data-dir={work / 'profile'}", "--no-pdf-header-footer",
         "--generate-pdf-document-outline", f"--print-to-pdf={pdf}", page.as_uri()],
        check=True, capture_output=True, timeout=120,
    )


def main() -> None:
    html = SOURCE.read_text(encoding="utf-8")
    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp)
        first = work / "first.pdf"
        render(html, first, work)
        pages = [p.extract_text() or "" for p in PdfReader(str(first)).pages]
        for marker, heading in HEADINGS.items():
            # The contents page itself is page 2, so search after it.
            number = next(i + 1 for i, text in enumerate(pages) if i >= 2 and any(line.strip().startswith(heading) for line in text.splitlines()))
            html = html.replace(marker, str(number))
        if re.search(r"@P\w+@", html):
            raise SystemExit("A contents placeholder was not filled in.")
        render(html, OUTPUT, work)
    print(f"{OUTPUT}: {len(PdfReader(str(OUTPUT)).pages)} pages")


if __name__ == "__main__":
    main()
