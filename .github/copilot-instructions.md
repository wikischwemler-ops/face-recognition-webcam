# Gesichtsverfolgung WPF - Copilot Anleitung

- [x] Verify that the copilot-instructions.md file in the .github directory is created.

## Projektübersicht

Dies ist ein vollständiges **WPF-Projekt in C#** mit folgenden Funktionen:

### Kernfeatures
- ✅ Mehrfach-Kamera-Unterstützung mit Dropdown-Auswahl
- ✅ Echtzeit-Gesichtserkennung mittels OpenCV
- ✅ Responsive, moderne GUI (dunkles Theme)
- ✅ Automatische Verfolgung erkannter Gesichter
- ✅ Echtzeit-FPS-Anzeige und Gesichts-Informationen
- ✅ Kamera-Verwaltung (Start/Stopp)

### Projektstruktur

```
FaceTrackingApp/
├── App.xaml / App.xaml.cs              # WPF Application Entry Point
├── MainWindow.xaml / MainWindow.xaml.cs # Hauptfenster mit UI
├── Models/Models.cs                     # Datenmodelle
├── Services/
│   ├── CameraService.cs                 # Kamera und Frame-Erfassung
│   └── FaceDetectionService.cs          # OpenCV Gesichtserkennung
├── ViewModels/MainWindowViewModel.cs    # MVVM ViewModel
├── RelayCommand.cs                      # WPF Command Helper
├── FaceTrackingApp.csproj               # Projekt-Konfiguration
└── README.md                            # Dokumentation
```

## Verwendete Technologien

- **WPF**: Benutzeroberfläche
- **MVVM**: Architektur-Pattern
- **OpenCvSharp4**: OpenCV Integration
- **.NET 6.0**: Framework
- **DirectShowLib**: Windows Kamera-API

## Kompilierung

```bash
# Alle Dependencies installieren
dotnet restore

# Projekt kompilieren
dotnet build

# Projekt ausführen
dotnet run
```

## Nächste Schritte nach erfolgreicher Kompilierung

1. Führe das Projekt aus: `dotnet run`
2. Wähle eine Kamera aus dem Dropdown-Menü
3. Klicke auf "Kamera starten"
4. Die Anwendung erkennt automatisch Gesichter und verfolgt sie

## Weitere Entwicklungsmöglichkeiten

- [ ] Verbesserte Gesichts-Verfolgung mit Kalman-Filtern
- [ ] Mehrfach-Gesichter-Tracking mit IDs
- [ ] Video-Aufnahme mit erkannten Gesichtern
- [ ] Alters-/Geschlecht-Klassifikation
- [ ] Emotionserkennung
- [ ] GPU-Beschleunigung
- [ ] Konfigurierbare Parameter (Sensibilität, etc.)

## Support

Die Anwendung ist produktionsreif und kann erweitert werden. Alle Komponenten folgen Best Practices für WPF und C#.
