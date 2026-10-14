"""One call that extracts the buildings and, optionally, the terrain and ground around them."""
from . import environment, geo, pipeline, terrain
from .common import Cancelled


class World:
    def __init__(self, export, preview, env):
        self.export = export    # geo-buildings dict
        self.preview = preview  # GeoJSON features of the buildings
        self.env = env          # environment.Result or None


def extract(polygon, sources, cache_dir, origin=None, min_area=6.0, with_elevation=True, env_options=None,
            estimate_roofs=True, fix_conflicts=True, log=print, progress=lambda f, msg=None: None,
            is_cancelled=lambda: False):
    """Buildings from `sources`, plus an environment package when `env_options` is a dict.

    env_options: heightmap (bool), ground (bool: material map, roads, water, fields, trees),
    heightCell, groundCell (metres per cell). Terrain problems never sink the buildings.
    """
    ring = [(float(lat), float(lon)) for lat, lon in polygon]
    if len(ring) > 1 and ring[0] == ring[-1]:
        ring.pop()
    want_env = bool(env_options) and (env_options.get("heightmap", True) or env_options.get("ground", True))
    want_dem = with_elevation or bool(env_options and env_options.get("heightmap", True))

    warnings = []
    dem = None
    if want_dem and len(ring) >= 3:
        lats, lons = [p[0] for p in ring], [p[1] for p in ring]
        margin = 0.003
        try:
            progress(0.0, "Terrain")
            dem = terrain.Dem(min(lats) - margin, min(lons) - margin, max(lats) + margin, max(lons) + margin,
                              cache_dir, log, lambda f, m=None: progress(None if f is None else 0.1 * f, m), is_cancelled)
        except Cancelled:
            raise
        except Exception as e:
            warnings.append("Terrain data could not be loaded, so there is no heightmap. Try again later.")
            log(f"WARNING: terrain failed: {e}")

    b_lo, b_hi = (0.1, 0.45) if want_env else (0.1, 1.0)
    export, preview = pipeline.run(
        polygon, sources, cache_dir, origin=origin, min_area=min_area, with_elevation=with_elevation,
        estimate_roofs=estimate_roofs, fix_conflicts=fix_conflicts,
        dem_sampler=dem.sampler() if dem is not None else None, log=log,
        progress=lambda f, m=None: progress(None if f is None else b_lo + (b_hi - b_lo) * f, m),
        is_cancelled=is_cancelled)
    export["warnings"].extend(warnings)

    env = None
    if want_env:
        o = export["origin"]
        proj = geo.EnuProjection(o["lat"], o["lon"])
        env = environment.run(
            export["buildings"], ring, proj, (o["lat"], o["lon"]), dem, env_options, cache_dir, log,
            lambda f, m=None: progress(None if f is None else 0.45 + 0.55 * f, m), is_cancelled)
    return World(export, preview, env)
