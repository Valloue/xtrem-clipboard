# HANDOFF — Xtrem Copy

## Pièges connus

- Ce dépôt a démarré avec des **rules clonées de Xtrem Rec** : elles ont été recentrées le 05/10/2026. Ne pas réintroduire FFmpeg / Discord / capture.
- Presse-papiers Windows : l’écoute doit tourner sur le thread UI / message pump correct, sinon miss d’événements.
- Coller vers l’app précédente : restaurer le focus avant Ctrl+V, sinon collage dans Xtrem Copy.
- Ne **jamais** écrire le contenu clipboard dans les logs (**D-CLIP-PRIVACY**).
- Isolation : ne pas toucher Xtrem Rec / ToolBox depuis ici (**D-ISO-SUITE**).

## Checklist reprise session

1. Lire `PROJECT_CONTEXT.md` + `docs/TODO.md` + `docs/DECISIONS.md`
2. Skill `start-session` si nouvelle session
3. Focus actuel = scaffold WinUI puis écoute clipboard
