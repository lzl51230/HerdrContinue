# Region flags

Windows uses these embedded 80 × 60 PNGs at 20 × 15 logical pixels because its system emoji font does not render region flags. macOS retains native flag emoji. Unknown assets fall back to region code and name.

Source: https://github.com/lipis/flag-icons/tree/v7.3.2/flags/4x3
License: MIT; retained in LICENSE.txt and copied into published packages.

Regenerate from the v7.3.2 archive with Python and `resvg-py==0.5.0`:

```python
from pathlib import Path
import resvg_py
for source in Path('flag-icons-7.3.2/flags/4x3').glob('??.svg'):
    Path(source.stem + '.png').write_bytes(
        resvg_py.svg_to_bytes(svg_path=str(source), width=80, height=60))
```

Only two-letter region assets are included. Image decoding is cached for the application lifetime; no resource download occurs at runtime.
