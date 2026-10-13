# Geo World Generator

Unity worlds of any area of Ukraine from the exports of the Building Extractor app: terrain with satellite
imagery, roads with soft edges, rivers and lakes, and buildings drawn with ruin and home models according to the
building condition the app gives (damaged or not is decided in the app only). Large areas become tiled worlds
that stream 1 km tiles around the camera.

Unity 6000.3 with the Universal Render Pipeline 17.

## Install in a project

Package Manager > **+** > **Add package from git URL...** and enter the version you want:

```
https://github.com/tredokiller/geoworld-unity.git#v1.0.0
```

or add it to `Packages/manifest.json`:

```json
"com.tredokiller.geoworld": "https://github.com/tredokiller/geoworld-unity.git#v1.0.0"
```

The repository is private: git on the computer must be signed in to GitHub (the same login that can clone it).

## Update

Change the version after `#` in `Packages/manifest.json` (`#v1.0.0` to `#v1.1.0`) and go back to Unity: it
downloads that version. Each version is listed in [CHANGELOG.md](CHANGELOG.md). (Without a version after `#` Unity
keeps the commit it first downloaded, recorded in `Packages/packages-lock.json`, so a version is the dependable way.)

## First steps in a project

1. **Tools > Geo World > Set up rendering for large worlds** (GPU Resident Drawer, occlusion culling).
2. **Tools > Geo World > Import ruin models** and **Import home models**: they ask once for the `ruins/models` and
   `intact/models` folders of the Building Extractor repository and remember them on this computer. The models are
   imported into the project (`Assets/GeoWorld/Ruins`, `Assets/GeoWorld/Homes`), not into the package.
3. **Tools > Geo World > Generate world from export...** and choose an unzipped Terrain & ground package of the
   app (`exports/<area>_env`).

Everything the tools make goes into the project's `Assets/GeoWorld` (generated worlds, tiles, imported models);
the package itself is read-only.

## More

The manual is in [Documentation~/Manual.txt](Documentation~/Manual.txt): settings, tiled worlds and streaming,
satellite imagery, building condition, ruin and home models, editing roads and buildings.
