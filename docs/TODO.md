# TODO — Xtrem Copy

**Version :** 0.1.2  
**Date :** 05/10/2026  
**Focus :** Scaffold WinUI 3  
**Bloqueur :** aucun  
**Dernière action :** Repo GitHub public `Valloue/xtrem-copy` créé + push `main`

## P0

- [ ] `T-CLIP-SCAFFOLD` — Créer solution WinUI 3 unpackaged `XtremCopy` (.NET 8, x64)
  - **Questions**
    - Confirmer le nom d’assembly / exe `XtremCopy` ?
    - Faut-il déjà un projet Installer Inno dès le scaffold ?
- [ ] `T-CLIP-LISTEN` — Service d’écoute des changements presse-papiers (texte + image)
  - **Questions**
    - API préférée : WinRT `Clipboard` + message pump WinUI, ou hook Win32 `AddClipboardFormatListener` ?
- [ ] `T-UI-HISTORY` — Fenêtre / popup Fluent liste historique (aperçu, coller, épingler, supprimer)
  - **Questions**
    - Popup overlay topmost ou fenêtre normale avec tray ?
- [ ] `T-HOTKEY-GLOBAL` — Raccourci global configurable pour ouvrir l’historique
  - **Questions**
    - Défaut exact (ex. `Ctrl+Shift+V`) pour éviter le conflit Win+V shell ?

## P1

- [ ] `T-STORE-LOCAL` — Persistance LocalAppData (textes + blobs images, limite rétention)
  - **Questions**
    - Limite par défaut : nombre d’entrées (ex. 100) ou taille disque ?
- [ ] `T-PRIV-EXCLUDE` — Exclusions process / mode pause historisation
  - **Questions**
    - Liste d’exclusions par défaut (1Password, Bitwarden, etc.) ?
- [ ] `T-UI-SETTINGS` — Page Réglages (hotkey, rétention, exclusions, démarrage auto)
  - **Questions**
    - Démarrage auto Windows dès V1 ?

## Fait

- [x] `T-RULES-COPY` — Rules / skills / docs skeleton recentrés sur Xtrem Copy (05/10/2026)
- [x] `T-OSS-PREP` — LICENSE MIT + README + .gitignore + **D-OPENSOURCE** (05/10/2026)
- [x] `T-GH-REMOTE` — Dépôt public https://github.com/Valloue/xtrem-copy + push `main` (05/10/2026)

## Hors

- Recapture / FFmpeg / Discord / sync Rec
- Sync cloud clipboard
- Modifier Xtrem Rec ou ToolBox depuis ce dépôt
