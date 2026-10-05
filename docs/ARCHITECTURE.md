# ARCHITECTURE — Xtrem Clipboard

## Cible (V1)

```
[Clipboard Windows]
        │ écoute
        ▼
 ClipboardListener (Service)
        │
        ▼
 ClipboardHistoryStore (LocalAppData)
        │
        ▼
 HistoryViewModel ←→ Popup / MainWindow (WinUI Fluent)
        │
        ▼ hotkey / Coller
 PasteService → Clipboard + focus app précédente
```

## Couches

- **UI** : Pages / Window WinUI 3 (Historique, Réglages)
- **ViewModels** : CommunityToolkit.Mvvm
- **Services** : écoute clipboard, stockage, hotkey, collage, settings, logs
- **Models** : `ClipboardEntry` (texte / image, pin, timestamps)

## Hors V1

Cloud, HTML/RTF/fichiers, licence, auto-update.
