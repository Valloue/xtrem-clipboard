# Xtrem Clipboard

Historique de presse-papiers pour Windows — alternative Fluent à Win+V.

**Dépôt :** https://github.com/Valloue/xtrem-clipboard (public)  
**Version :** 0.10.8  
**Licence :** MIT

## Stack

- C# · WinUI 3 · .NET 8 · Windows App SDK
- Unpackaged, self-contained x64
- Windows 10 version 1809+

## Lancer (dev)

Prérequis : .NET SDK 9 (voir `global.json`, 9.0.319), Visual Studio 2022 ou Build Tools avec Windows App SDK / WinUI, Windows SDK. Si `Program Files\dotnet` n’a pas de dossier `sdk`, `run.bat` utilise `%LocalAppData%\Microsoft\dotnet\dotnet.exe`.

Double-clic sur **`run.bat`** : compile en Debug x64, signe `CN=valloue.fr`, lance l’exe depuis `%LocalAppData%\XtremClipboard\dev-run`.  
**`run.bat fresh`** efface `settings.json` dans le dossier actuel et dans l’ancien, puis rejoue la configuration initiale.

```powershell
dotnet build XtremClipboard.sln -c Debug -p:Platform=x64
```

## Usage

1. **Première fois** : choisir un raccourci, le refaire pour le tester (pas de bouton), puis la limite d’historique, puis Activer.
2. Ensuite l’app reste dans la **zone de notification**.
3. **Win+V** (ou le raccourci choisi) ouvre l’historique, fenêtre compacte. Le texte s’affiche sur quelques lignes, sans vignette. Une image s’affiche en miniature, sans libellé. ↑ ↓ puis Entrée colle dans l’application précédente. Coller, Copier, Épingler et Supprimer sont sous chaque entrée. L’engrenage ouvre les Réglages.
4. Menu de l’icône : Historique, Réglages, Quitter.
5. Fermer la fenêtre Réglages **ne quitte pas** l’application.

Données : `%LocalAppData%\XtremSuite\XtremClipboard\` (`settings.json`, `history\index.json`, `history\payloads\`, `logs\`).

## Déjà en place

- Historique texte (aperçu sur plusieurs lignes, sans vignette) et images (miniature seule)
- Overlay compact (~400×430) + tray ; engrenage vers les Réglages
- Barre de titre claire ou sombre, comme le contenu
- Coller / Copier / Épingler / Supprimer, recherche dans le texte complet
- Gestionnaires de mots de passe toujours exclus (Proton Pass, Dashlane, NordPass, Bitwarden, 1Password, KeePass, et les autres coffres)
- Réglages : raccourci, rétention, démarrage Windows, pause, autres exclusions, vider (épinglés conservés)
- Icône Xtrem CP (vert `#008A3F`) et boutons d’accent de la même couleur
- Une seule instance
- Historique Windows natif coupé ; Win+V intercepté

## Plus tard

- Installateur Inno
- HTML / RTF / fichiers
- Certificat Authenticode public
