"""Extract static assets from New ID Card.psd.

Outputs into ../assets:
  background.png  - composite with the 'Edit' group hidden (variable fields removed)
  preview_full.png- composite as-is, for visual verification
  logo.png        - the visible logo smart object
  signature.png   - signature layers
"""
import sys
from pathlib import Path

from psd_tools import PSDImage

PSD_PATH = Path(r"F:\Max Files\Other\New ID Card.psd")
OUT = Path(__file__).resolve().parent.parent / "assets"
OUT.mkdir(parents=True, exist_ok=True)


def find(psd, name):
    for layer in psd.descendants():
        if layer.name == name:
            return layer
    return None


def main():
    psd = PSDImage.open(PSD_PATH)

    edit = find(psd, "Edit")
    logo = find(psd, "image-removebg-preview")
    dont_edit = find(psd, "Don't Edit")
    sig_smart = None
    if dont_edit is not None:
        for layer in dont_edit:
            if layer.name == "Signature":
                sig_smart = layer
                break

    # Full composite for reference
    img = psd.composite()
    img.save(OUT / "preview_full.png")
    print("preview_full.png", img.size)

    # Background: hide Edit group, composite
    edit.visible = False
    bg = psd.composite()
    bg.save(OUT / "background.png")
    print("background.png", bg.size)
    edit.visible = True

    # NOTE: the PSD 'Contact' text layer still contains the old number
    # '07103-790-950'. After re-exporting background.png, run
    #   python tools/fix_contact.py --apply
    # to patch it to '01703-790-950' (glyph-swap, pixel-exact).

    # Logo layer
    if logo is not None:
        limg = logo.composite()
        limg.save(OUT / "logo.png")
        print("logo.png", limg.size, "bbox", logo.bbox)

    # Signature: composite the Don't Edit signature smart object
    if sig_smart is not None:
        simg = sig_smart.composite()
        simg.save(OUT / "signature.png")
        print("signature.png", simg.size, "bbox", sig_smart.bbox)

    # Also dump the hidden 'Signature' pixel layer inside Edit/Unused
    if edit is not None:
        for g in edit:
            if g.name == "Unused":
                for l in g:
                    if l.name == "Signature" and l.kind == "pixel":
                        pimg = l.composite()
                        pimg.save(OUT / "signature_edit_layer.png")
                        print("signature_edit_layer.png", pimg.size, "bbox", l.bbox)


if __name__ == "__main__":
    sys.exit(main())
