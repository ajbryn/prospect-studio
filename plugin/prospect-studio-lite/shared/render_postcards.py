#!/usr/bin/env python3
"""Prospect Studio Lite: postcard renderer and tracking-code helper.

Commands
  codes    --n 15 [--existing codes.csv]           Print N new unique 6-character tracking codes (one per line).
  preview  --cards cards.json --out DIR [--sides front,back]            
                                                    Render PNG previews of each card (for design review).
  produce  --cards cards.json --out DIR            Render print PDFs (9.25x6.25in, 2 pages), email PNGs (front, trimmed),
                                                    proofs.pdf (all cards) and qa-report.json into DIR.
  html2pdf --in page.html --out page.pdf [--size letter|W,H]
                                                    Convert a local HTML page (e.g., a dealer lead packet) to PDF.

cards.json
  {
    "brand":  {"brandPrimary": "#1c3550", "brandAccent": "#e8a317", "logoSrc": "path/to/logo.svg",
               "returnAddress": "...", "legal": "..."},          # optional; merged into every card's fields
    "cards": [
      {"id": "L0001", "fileStem": "L0001_Bayou-Fulfillment",
       "layout": "hero-bold-left", "palette": "brand-dark",
       "scene": {"timeOfDay": "day", "liftPosition": "right", "buildingType": "warehouse",
                 "signText": "Bayou Fulfillment Co.", "productSrc": "", "photoSrc": "", "photoCredit": ""},
       "fields": {"headline": "...", "tagline": "...", "cta": "...", "spec1": "...", "spec2": "...", "spec3": "...",
                  "greeting": "...", "personal": "...", "benefit1": "...", "benefit2": "...",
                  "offerCode": "LIFT-K7Q3MX", "offerText": "...", "url": "https://...", "urlDisplay": "...",
                  "dealerName": "...", "dealerLine": "...", "attn": "Attn: Facilities Manager", "company": "...",
                  "address1": "...", "cityStateZip": "..."}}
    ]
  }
  Any field ending in "Src" that points to a local file is embedded as a data URI.

Requirements: Python 3.9+, `pip install playwright` and `python -m playwright install chromium`
(if Chromium is already available, set PLAYWRIGHT_BROWSERS_PATH or pass --chromium PATH).
"""
import argparse, base64, csv, html, json, mimetypes, os, re, secrets, sys, tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
TEMPLATE = HERE / "postcard-template.html"
QR_JS = HERE / "qrcode.bundle.js"
ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"  # no 0/O/1/I
PAGE_W_IN, PAGE_H_IN, BLEED_PX = 9.25, 6.25, 12  # 0.125in at 96 px/in

DEFAULT_BRAND = {"brandPrimary": "#1c3550", "brandAccent": "#e8a317"}


def new_codes(n, existing=()):
    seen, out = set(existing), []
    while len(out) < n:
        c = "".join(secrets.choice(ALPHABET) for _ in range(6))
        if c not in seen:
            seen.add(c); out.append(c)
    return out


def data_uri(value):
    if not value or value.startswith(("data:", "http://", "https://", "file:")):
        return value or ""
    p = Path(value).expanduser()
    if not p.is_file():
        print(f"warning: asset not found: {value}", file=sys.stderr)
        return ""
    mime = mimetypes.guess_type(p.name)[0] or "application/octet-stream"
    return f"data:{mime};base64," + base64.b64encode(p.read_bytes()).decode()


def fill(template, values):
    qr = values.pop("qrScript")
    def sub(m):
        key = m.group(1)
        if key == "qrScript":
            return qr
        return html.escape(str(values.get(key, "")), quote=True)
    return re.sub(r"\{\{(\w+)\}\}", sub, template)


def card_values(card, brand):
    v = dict(DEFAULT_BRAND)
    v.update(brand or {})
    v.update(card.get("fields", {}))
    v.update(card.get("scene", {}))
    v["id"] = card.get("id", "")
    v["layout"] = card.get("layout", "hero-bold-left")
    v["palette"] = card.get("palette", "brand-dark")
    for k in list(v):
        if k.endswith("Src"):
            v[k] = data_uri(v[k])
    if not v.get("urlDisplay") and v.get("url"):
        v["urlDisplay"] = re.sub(r"^https?://", "", v["url"])
    v["qrScript"] = QR_JS.read_text(encoding="utf-8")
    return v


QA_JS = """() => {
  const out = [];
  document.querySelectorAll('[data-slot]').forEach(el => {
    const st = getComputedStyle(el);
    if (st.display === 'none' || el.offsetParent === null) return;
    const box = el.getBoundingClientRect();
    if (el.scrollHeight > el.clientHeight + 6 || el.scrollWidth > el.clientWidth + 2)
      out.push({check: 'overflow', slot: el.dataset.slot, severity: 'error'});
    if (!el.textContent.trim())
      out.push({check: 'empty', slot: el.dataset.slot, severity: 'error'});
    const page = el.closest('.page').getBoundingClientRect();
    if (box.left - page.left < 24 || page.right - box.right < 24 || box.top - page.top < 24 || page.bottom - box.bottom < 20)
      out.push({check: 'safe-zone', slot: el.dataset.slot, severity: 'warning'});
  });
  const qr = document.querySelector('[data-qr] svg');
  if (!qr) out.push({check: 'qr-missing', slot: 'qr', severity: 'error'});
  return out;
}"""


def launch(args):
    from playwright.sync_api import sync_playwright
    pw = sync_playwright().start()
    kw = {"executable_path": args.chromium} if args.chromium else {}
    browser = pw.chromium.launch(**kw)
    return pw, browser


def render_page(browser, html_text, tmpdir, name):
    path = Path(tmpdir) / f"{name}.html"
    path.write_text(html_text, encoding="utf-8")
    page = browser.new_page(viewport={"width": 888, "height": 600}, device_scale_factor=2)
    page.route(re.compile(r"^https?://"), lambda route: route.abort())  # never fetch from the network
    page.goto(path.as_uri())
    page.wait_for_function("window.__ready === true", timeout=10000)
    return page


def cmd_codes(args):
    existing = []
    if args.existing and Path(args.existing).exists():
        with open(args.existing, newline="", encoding="utf-8") as f:
            existing = [row[0].split("-")[-1] for row in csv.reader(f) if row]
    print("\n".join(new_codes(args.n, existing)))


def cmd_render(args, produce):
    spec = json.loads(Path(args.cards).read_text(encoding="utf-8"))
    template = TEMPLATE.read_text(encoding="utf-8")
    out = Path(args.out); out.mkdir(parents=True, exist_ok=True)
    sides = [s.strip() for s in (args.sides or "front,back").split(",")]
    report, all_html_bodies = {}, []
    pw, browser = launch(args)
    try:
        with tempfile.TemporaryDirectory() as tmp:
            for card in spec["cards"]:
                stem = card.get("fileStem") or card.get("id") or "card"
                doc = fill(template, card_values(card, spec.get("brand")))
                page = render_page(browser, doc, tmp, stem)
                report[stem] = page.evaluate(QA_JS)
                fronts = page.locator(".page.front"); backs = page.locator(".page.back")
                if produce:
                    (out / "print").mkdir(exist_ok=True); (out / "email").mkdir(exist_ok=True)
                    page.pdf(path=str(out / "print" / f"{stem}.pdf"), width=f"{PAGE_W_IN}in", height=f"{PAGE_H_IN}in",
                             print_background=True, margin={"top": "0", "right": "0", "bottom": "0", "left": "0"})
                    box = fronts.bounding_box()
                    page.screenshot(path=str(out / "email" / f"{stem}.png"),
                                    clip={"x": box["x"] + BLEED_PX, "y": box["y"] + BLEED_PX,
                                          "width": box["width"] - 2 * BLEED_PX, "height": box["height"] - 2 * BLEED_PX})
                    all_html_bodies.append(doc)
                else:
                    if "front" in sides: fronts.screenshot(path=str(out / f"{stem}_front.png"))
                    if "back" in sides: backs.screenshot(path=str(out / f"{stem}_back.png"))
                page.close()
                errs = [f for f in report[stem] if f["severity"] == "error"]
                print(f"{stem}: {'OK' if not errs else str(len(errs)) + ' QA error(s)'}")
            if produce and all_html_bodies:
                # proofs.pdf: concatenate every card's two pages into one document
                bodies = [re.search(r"<body>(.*)</body>", d, re.S).group(1) for d in all_html_bodies]
                head = re.search(r"^(.*?<body>)", all_html_bodies[0], re.S).group(1)
                # keep only one copy of the scene/QR bootstrap script (the last one in each body runs per section)
                proof = head + "\n".join(bodies) + "</body></html>"
                page = render_page(browser, proof, tmp, "proofs")
                page.pdf(path=str(out / "proofs.pdf"), width=f"{PAGE_W_IN}in", height=f"{PAGE_H_IN}in",
                         print_background=True, margin={"top": "0", "right": "0", "bottom": "0", "left": "0"})
                page.close()
    finally:
        browser.close(); pw.stop()
    (out / "qa-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    flagged = sum(1 for v in report.values() if any(f["severity"] == "error" for f in v))
    print(f"Done: {len(report)} card(s), {flagged} with QA errors. Report: {out / 'qa-report.json'}")


def cmd_html2pdf(args):
    """Convert any local HTML file (e.g., a dealer lead packet) to PDF. Page size: letter (default) or 'W,H' in inches."""
    size = (8.5, 11) if args.size == "letter" else tuple(float(x) for x in args.size.split(","))
    pw, browser = launch(args)
    try:
        page = browser.new_page()
        page.route(re.compile(r"^https?://"), lambda route: route.abort())
        page.goto(Path(args.inp).resolve().as_uri())
        page.pdf(path=args.out, width=f"{size[0]}in", height=f"{size[1]}in", print_background=True,
                 margin={"top": "0.5in", "right": "0.5in", "bottom": "0.5in", "left": "0.5in"})
    finally:
        browser.close(); pw.stop()
    print(f"Wrote {args.out}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--chromium", help="Path to a Chromium executable (optional)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    c = sub.add_parser("codes"); c.add_argument("--n", type=int, required=True); c.add_argument("--existing")
    for name in ("preview", "produce"):
        p = sub.add_parser(name); p.add_argument("--cards", required=True); p.add_argument("--out", required=True)
        p.add_argument("--sides")
    h = sub.add_parser("html2pdf"); h.add_argument("--in", dest="inp", required=True); h.add_argument("--out", required=True)
    h.add_argument("--size", default="letter")
    args = ap.parse_args()
    if args.cmd == "codes":
        cmd_codes(args)
    elif args.cmd == "html2pdf":
        cmd_html2pdf(args)
    else:
        cmd_render(args, produce=(args.cmd == "produce"))


if __name__ == "__main__":
    main()
