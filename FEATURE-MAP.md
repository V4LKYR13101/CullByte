# SpaceSniffer feature map

CullByte is an independent implementation based on SpaceSniffer’s published behavior and the documentation shipped with version 2.2.0.27. The third-party page linked in the request describes version 2.1.0.21. [Uderzo feature overview](https://www.uderzo.it/main_products/space_sniffer/features.html) · [Release notes](https://www.uderzo.it/main_products/space_sniffer/release_notes.html) · [Version downloads](https://www.uderzo.it/main_products/space_sniffer/download_alt.html)

## Views

| Reference surface | CullByte |
| --- | --- |
| Startup and source selection | Overview with quick drive shortcuts, drive and path chooser, multiple folders, local or UNC paths, native folder picker, and drag-and-drop on the workspace. Snapshot loading has its own file picker. |
| Scan window | Compact scan tabs with a distinct selected state, optional separate comparison windows, a custom treemap, current path, navigation and rescan controls, filter field, inspector, and scan status. |
| Tile inspection | Hover details, click to reveal or collapse a folder, double-click to focus, back and forward history, parent and root navigation, detail depth controls, and a keyboard-operable largest-items list. |
| Tile actions | Open, reveal in Explorer, Windows Properties, confirmed Recycle Bin operation, copy path, and temporary color tags. Offline scans only expose inspection, copy, and tag actions. |
| Settings | Geometry, Animation, Behavior, Look, and File Classes. Settings include tile sizing, text sizing, update cadence, sort order, free and unknown space defaults, zoom animation, live monitoring, ADS, filtered-out coloring, theme, and file-class colors. |
| Export | CSV or per-file text template with preview. Output reflects the focused folder and the applied filter. |
| Log and help | Scan issue list with paths and messages, copy log, filter examples, shortcuts, and About dialog. |
| Snapshot and automation | Load and save CullByte snapshots and scan, filter, report, and save from the command line. |

## Behavior

- **Scanning** uses background directory enumeration and publishes batches during navigation. Duplicate views share a scan and tags while keeping independent focus, detail, and filters.
- **Refresh** supports a full rescan and a focused branch scan. A branch job can read while the master is active and publishes its replacement after the master releases the tree. File-system notifications coalesce into directory refreshes. Network monitoring depends on the server and OS.
- **Space** defaults to allocated bytes. Regular files are rounded to the reported cluster size. Compressed and sparse files use Windows allocation data where available. Optional ADS entries are collected on supported volumes. Reparse-point directories are displayed without traversal. The whole-volume map can show free and unaccounted space. Allocation is an estimate and hard links are counted per directory entry.
- **Filtering** supports wildcard file masks, folder ancestry masks, byte units and logical or allocated size, date age and date source, file attributes, classes, tags, and pipe exclusions. Positive file masks, class clauses, and tag clauses are ORed. Comparisons, folder masks, attributes, and exclusions are ANDed.
- **Tags** are red, yellow, green, or blue. A node has one explicit tag and can match ancestor tags. Tags are shared by views on the same session and omitted from snapshots.
- **Rendering** uses one Skia-backed Avalonia control, grouped smaller items when there is room, and separate light and dark palettes. Tile label contrast is derived from the tile fill. The inspector yields its space to the map at narrower widths.

## Compatibility differences

- CullByte uses tabs and independent comparison windows. The reference app’s cascade and tile window commands are omitted.
- The item menu exposes selected Windows shell actions. Third-party Explorer context-menu extensions are accessible through Explorer itself.
- CullByte uses its own .cullscan snapshot format, text placeholders, and command switches. SpaceSniffer snapshot formats, Sysinternals DU imports, and the SpaceSniffer export scripting language are outside current compatibility.
- Filter names accept common aliases. The reference’s fuzzy directive matching and some shorthand spellings differ. Geometry and animation settings use CullByte’s own layout and timing model.
- Windows x64 is the verified release target. macOS and Linux packages and OS integrations are unverified.
