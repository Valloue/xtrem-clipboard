# Module — Xtrem Copy

## Rôle

Remplacer l’historique du presse-papiers Windows par une app Fluent (WinUI 3) : écoute, historique local, popup, collage.

## Shell UI (**D-UI-SHELL**)

- **Historique** : liste entrées (texte tronqué / miniature), Coller / Copier / Épingler / Supprimer, recherche
- **Réglages** : hotkey, rétention, exclusions, démarrage auto (si V1)
- Accent Pale Red (**D-UI-ACCENT**)

## Services (cibles)

| Service | Rôle |
|---------|------|
| `ClipboardListener` | Détecte les changements clipboard |
| `ClipboardHistoryStore` | Persiste / charge l’historique |
| `PasteService` | Remet le contenu + collage vers l’app précédente |
| `HotkeyService` | Raccourci global |
| `SettingsService` | `settings.json` temps réel |
| `PrivacyFilter` | Exclusions process / pause |

## Décisions liées

**D-APP-COPY**, **D-CLIP-HISTORY**, **D-CLIP-V1**, **D-CLIP-HOTKEY**, **D-CLIP-LOCAL**, **D-CLIP-PRIVACY**, **D-UI-SHELL**, **D-UI-ACCENT**, **D-PLAT**.
