# Gesichtserkennung & Verfolgung (WPF, .NET 10)

Echtzeit-Gesichtserkennung mit Mehrfach-Tracking für Windows 10/11. Kameras werden direkt aus dem
Geräte-Manager gelesen (intern, extern/USB, virtuell), An- und Abstecken wird live erkannt.

## Voraussetzungen

- Windows 10 (1809+) oder Windows 11, **x64**
- Visual Studio 2026 (Workload „.NET-Desktopentwicklung“) **oder** .NET 10 SDK
- Webcam (intern oder USB)

## Bauen & starten

```powershell
cd D:\workspace\wikischwemler-github\face-recognition-webcam
dotnet build -c Release
dotnet run -c Release
```

Oder in Visual Studio `FaceTrackingApp.sln` öffnen → F5.

Eigenständige EXE (ohne installiertes .NET): `build.cmd` ausführen, Ergebnis liegt in `publish\`.

Beim ersten Build wird zusätzlich das **YuNet**-Modell (~230 KB, OpenCV Zoo) nach `Assets\` geladen.
Ohne Internet wird automatisch das mitgelieferte ResNet-SSD-Modell verwendet (Abschalten: `-p:DownloadYuNet=false`).

> Nach dem Umstieg von .NET 6/8: einmal **Projektmappe bereinigen** bzw. die Ordner `bin\` und `obj\` löschen.

## Funktionen

| Bereich | Umsetzung |
|---|---|
| Kameraauswahl | Alle aktivierten Kameras laut Geräte-Manager, mit echtem Namen und Kennzeichnung **Intern / Extern (USB) / Virtuell**; Liste aktualisiert sich beim An-/Abstecken automatisch |
| Erkennung | YuNet (DNN) → ResNet-SSD (DNN) → Haar-Cascade als Rückfallebene, mit Konfidenzwert |
| Tracking | Stabile IDs, Glättung gegen Zittern, kurzes Nachhalten bei Aussetzern; größtes Gesicht = Fokus (orange, Fadenkreuz) |
| Anzeige | `WriteableBitmap` wird im Bildschirmtakt aktualisiert, Overlays als WPF-Vektorgrafik |
| Optionen | Auflösung 640×480 / 1280×720 / 1920×1080, Spiegeln, Rahmen ein/aus |

## Architektur – warum es flüssig läuft

```
Kamera-Thread ──► neuestes Frame (Double-Buffer, kein Stau)
                        │                     │
        UI (CompositionTarget.Rendering)   Erkennungs-Thread
        kopiert Frame → WriteableBitmap     verkleinert → DNN → Tracker
                        │                     │
                        └──── Overlay-Rahmen ◄┘
```

- Kein `Dispatcher.Invoke` aus Hintergrund-Threads → keine Blockaden/Deadlocks beim Stoppen
- Erkennung läuft entkoppelt: ist sie langsamer als die Kamera, bleibt das Video trotzdem bei voller FPS
- Kamera über DirectShow mit MJPG geöffnet → 720p/1080p mit 30 FPS auch bei USB-2-Kameras

## Projektstruktur

```
App.xaml(.cs)                     Fluent-Dark-Theme, globale Fehlerbehandlung
MainWindow.xaml(.cs)              Oberfläche
Models/Models.cs                  CameraInfo, BoxF, Erkennungs-/Tracking-Daten
Services/CameraEnumerator.cs      Kameraliste (DirectShow + Windows.Devices.Enumeration), Hot-Plug
Services/CameraService.cs         Capture-Thread, Double-Buffer
Services/FaceDetectionService.cs  Detektoren (YuNet/SSD/Haar) + Erkennungs-Thread
Services/FaceTracker.cs           Multi-Face-Tracking mit IDs und Glättung
ViewModels/                       MVVM (MainWindowViewModel, FaceOverlay)
Assets/                           Modelle (werden ins Ausgabeverzeichnis kopiert)
```

## Fehlerbehebung

- **Kamera lässt sich nicht öffnen:** andere App (Teams, Zoom, Kamera-App) schließen; Windows-Einstellungen › Datenschutz › Kamera › „Desktop-Apps Zugriff erlauben“.
- **Kamera fehlt in der Liste:** im Geräte-Manager unter „Kameras“ bzw. „Bildverarbeitungsgeräte“ prüfen, ob sie aktiviert ist; dann ⟳ klicken.
- **Detektor zeigt „ResNet-SSD“ statt „YuNet“:** Build hatte kein Internet – einfach erneut bauen.

## Lizenzen

- OpenCvSharp / OpenCV: Apache 2.0
- YuNet (OpenCV Zoo): MIT · ResNet-SSD-Gesichtsmodell und Haar-Cascade: OpenCV-Lizenz (BSD/Apache)
