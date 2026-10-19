# Xtrem Clipboard

Historique de presse-papiers pour Windows — remplace le presse-papiers Windows (Win+V) : popup rapide, recherche, épinglage, rétention, exclusions de confidentialité.

## Fonctions

- Overlay topmost (style Win+V) : texte (aperçu 4 lignes) et images (miniature)
- Raccourci global au choix (Win+V inclus), recherche dans le texte complet, navigation clavier
- Historique 100 % local sous `%LocalAppData%\XtremSuite\XtremClipboard\` — aucun cloud
- Rétention au choix : durée / taille disque / nombre d'entrées ; déduplication consécutive ; épinglage
- Confidentialité : les gestionnaires de mots de passe (1Password, Bitwarden, KeePass, Proton Pass…) sont toujours exclus, avant même la lecture du presse-papiers ; exclusions par application au choix
- Désactive l'historique presse-papiers Windows et intercepte Win+V
- Icône zone de notification, démarrage avec Windows, instance unique
- Mise à jour automatique (manifeste signé + SHA-256, installation silencieuse)

## Prérequis

- Windows 10 1809+ (x64)
- .NET SDK 8+ (SDK 9 recommandé, voir `global.json`)
- Windows App SDK / WinUI 3 (restauré via NuGet) + Windows SDK

## Compiler et lancer (développement)

```
dotnet build src\XtremClipboard\XtremClipboard.csproj -c Debug -p:Platform=x64
```

Exécutable produit : `src\XtremClipboard\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\XtremClipboard.exe`

Au premier lancement, un assistant configure le raccourci et la rétention, puis l'icône tray et l'overlay prennent le relais.

## Installateur

Les installateurs officiels sont publiés par le mainteneur sur `xtrem.valloue.fr` ; l'application se met ensuite à jour automatiquement. Ce dépôt ne contient pas de script d'installation.

## Confidentialité

- Historique stocké uniquement en local, jamais envoyé
- Le contenu du presse-papiers n'est jamais journalisé
- Les coffres-forts de mots de passe sont ignorés avant la lecture du presse-papiers

## Licence

Distribué sous licence MIT.
