# CullByte

CullByte is a Windows disk-space explorer built with C#, .NET 10, and Avalonia. It scans a folder or drive and draws files and folders in proportion to the space they use.

## Open in Visual Studio

Open **CullByte.sln** in Visual Studio with the .NET 10 SDK installed. Set **CullByte** as the startup project for Windows, or **CullByte.Android** for Android.

```powershell
dotnet build CullByte.csproj
dotnet run --project CullByte.csproj
```


## Explore a scan

- Choose **New scan** to select a drive or browse for folders. You can enter multiple paths separated by semicolons, or drop folders onto the window.
- Click a folder tile to reveal or collapse its contents. Double-click to zoom in. The top row takes you back, forward, up one level, or to the scan root.
- Enter a query such as *.zip;*.7z;>500mb and choose **Apply filter**. The **View** menu opens another tab or window from the same scan so you can compare filters without reading the disk again.
- Choose **Inspector** to see the selected item and the largest children in the focused folder. It hides automatically on narrow windows and can be toggled at any width.
- Press **Ctrl+1** through **Ctrl+4** over an item to toggle a color tag. Tags last for the scan session and can be included or excluded in a filter.
- Save a .cullscan snapshot from **File** for offline review, or export a CSV or customizable text report from the focused folder.
- Switch among dark, light, and system themes in **Settings → Look**.

## Portable Windows build

```powershell
dotnet publish CullByte.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```


Run CullByte.exe from the published folder. The executable uses the libraries alongside it. Settings are stored in %LOCALAPPDATA%\CullByte by default. To carry settings with the executable, place a copy of the generated CullByte.settings.xml next to it.

## Android

The Android project needs the .NET 10 Android workload, Android SDK Platform 36, and JDK 21. It runs on Android 12 or newer. If the build finds an older JDK, point `JavaSdkDirectory` at JDK 21.

```powershell
dotnet build CullByte.Android/CullByte.Android.csproj -c Debug -r android-arm64 -p:EmbedAssembliesIntoApk=true
```

For Visual Studio debugging, set **CullByte.Android** as the startup project and select your connected phone. The phone layout stacks the map and largest items, while wider screens show them side by side. Theme options include **System**, **Light**, and **Dark**.

Choose a folder through Android’s picker to scan it. The treemap and largest-items list fill in during the scan. Tap a tile or item name to open its location in Android Files. Use **Explore** next to a folder to drill down inside CullByte or **Info** to inspect a file. **Actions** can rescan or export CSV, and the filter accepts names, wildcards and sizes (for example, `*.zip;>50mb`). Android reports logical sizes for the files it shares. Windows drive totals, NTFS features, and desktop snapshots are specific to the desktop project.

## Headless usage

```powershell
dotnet run --project CullByte.csproj -- --headless --scan "D:\Projects" --ads --save "projects.cullscan"
dotnet run --project CullByte.csproj -- --headless --load "projects.cullscan" --filter "*.zip;>500mb" --report "archives.csv"
```


See [the SpaceSniffer feature map](FEATURE-MAP.md) for behavior details and compatibility differences.
