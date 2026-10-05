# HANDOFF — Xtrem Clipboard

## Remote Git

- GitHub : **https://github.com/Valloue/xtrem-clipboard** (`origin` / `main`, public, MIT) — ancien nom `xtrem-copy` redirigé
- Compte CLI : `Valloue` (`gh auth`)
- Dossier local : `…/Xtrem suite/Xtrem Clipboard`

## Pièges connus

- Nom produit figé **Xtrem Clipboard** (pas « Xtrem Copy ») — **D-APP-CLIPBOARD**.
- Ce dépôt a démarré avec des **rules clonées de Xtrem Rec** : elles ont été recentrées le 05/10/2026. Ne pas réintroduire FFmpeg / Discord / capture.
- Presse-papiers Windows : l’écoute doit tourner sur le thread UI / message pump correct, sinon miss d’événements.
- Coller vers l’app précédente : restaurer le focus avant Ctrl+V, sinon collage dans Xtrem Clipboard.
- Ne **jamais** écrire le contenu clipboard dans les logs (**D-CLIP-PRIVACY**).
- Isolation : ne pas toucher Xtrem Rec / ToolBox depuis ici (**D-ISO-SUITE**).

## Checklist reprise session

1. Lire `PROJECT_CONTEXT.md` + `docs/TODO.md` + `docs/DECISIONS.md`
2. Skill `start-session` si nouvelle session
3. Focus actuel = scaffold WinUI puis écoute clipboard
