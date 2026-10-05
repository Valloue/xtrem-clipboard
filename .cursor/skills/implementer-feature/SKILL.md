---
name: implementer-feature
description: >-
  Implemente une fonctionnalite Xtrem Clipboard (WinUI 3, MVVM, presse-papiers)
  en respectant DECISIONS, Fluent Design, et la sync docs obligatoire.
---

# Implémenter une feature — Xtrem Clipboard

## Quand

Ajouter / modifier un écran, service clipboard, hotkey, stockage, option UX Copy.

## Prérequis

1. Lire CONTEXT + **TODO** + DECISIONS + HANDOFF + `docs/modules/XtremClipboard.md`.
2. Respecter **D-APP-CLIPBOARD**, **D-PLAT**, **D-UI-SHELL**, **D-UI-ACCENT**, **D-CLIP-HISTORY**, **D-CLIP-V1**, **D-CLIP-HOTKEY**, **D-CLIP-LOCAL**, **D-CLIP-PRIVACY**, **D-ISO-SUITE**.
3. Ne pas modifier ToolBox ni Xtrem Rec.
4. Ne jamais logger le contenu clipboard en clair.

## Pattern

- Logique métier dans **Services**, pas dans le code-behind.
- UI → ViewModel (`[ObservableProperty]`, `[RelayCommand]`).
- Clipboard : snapshot à l’événement ; privacy filter avant stockage.
- Coller : restaurer focus app précédente puis Ctrl+V.
- Logs via `ILogger<T>` ; erreurs via InfoBar / statut clair.

## Après code

1. Maj docs (`documentation.mdc`) + CONTEXT + **`docs/TODO.md`**.
2. Build / vérifs du périmètre touché.
3. Commit local seulement si demandé.
