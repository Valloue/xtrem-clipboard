# PROJECT_CONTEXT — Xtrem Copy

**Version :** 0.1.2  
**Date :** 05/10/2026  
**État :** OK — GitHub public branché ; pas encore de code app

## Où on en est

Nouveau produit **Xtrem Copy** : remplacement de l’historique presse-papiers Windows (Win+V), Fluent Design, C# WinUI 3 / .NET 8.

Rules / docs Copy. Open source : **https://github.com/Valloue/xtrem-copy** (public, MIT — **D-OPENSOURCE**, **D-LICENSE-MIT**). Remote `origin` = `main`.

## Décisions clés

- **D-APP-COPY** : app autonome suite Xtrem (isolée de Rec / ToolBox)
- **D-OPENSOURCE** / **D-LICENSE-MIT** : dépôt public GitHub, licence MIT
- Stack : WinUI 3 + .NET 8, unpackaged x64, Win10 1809+
- V1 : texte + images, épingler, recherche, hotkey, stockage local, privacy
- Accent Pale Red (suite)

## Prochaine étape

Scaffold WinUI 3 (`XtremCopy`) + écoute clipboard + UI historique.

## Hors périmètre (pour l’instant)

- Code Rec / FFmpeg / Discord
- Cloud sync d’historique
- Site marketing
