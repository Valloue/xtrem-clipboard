# PROJECT_CONTEXT — Xtrem Clipboard

**Version :** 0.2.0  
**Date :** 05/10/2026  
**État :** OK — renommé Xtrem Clipboard ; GitHub public ; pas encore de code app

## Où on en est

Produit **Xtrem Clipboard** (ex-« Xtrem Copy ») : remplacement de l’historique presse-papiers Windows (Win+V), Fluent Design, C# WinUI 3 / .NET 8.

Open source : **https://github.com/Valloue/xtrem-clipboard** (public, MIT — **D-OPENSOURCE**, **D-LICENSE-MIT**). Remote `origin` = `main`. Dossier racine : `Xtrem Clipboard`.

## Décisions clés

- **D-APP-CLIPBOARD** : app autonome suite Xtrem (isolée de Rec / ToolBox) — remplace l’ancien id D-APP-COPY
- **D-OPENSOURCE** / **D-LICENSE-MIT** : dépôt public GitHub, licence MIT
- Stack : WinUI 3 + .NET 8, unpackaged x64, Win10 1809+
- V1 : texte + images, épingler, recherche, hotkey, stockage local, privacy
- Accent Pale Red (suite)

## Prochaine étape

Scaffold WinUI 3 (`XtremClipboard`) + écoute clipboard + UI historique.

## Hors périmètre (pour l’instant)

- Code Rec / FFmpeg / Discord
- Cloud sync d’historique
- Site marketing
