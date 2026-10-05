---
name: start-session
description: >-
  Phase d'impregnation lecture seule pour demarrer une session IA sur Xtrem Copy :
  cartographie, docs, rules, skills, code, dette. Aucune modif tant que non valide.
---

# Start session — Xtrem Copy

## Quand

Nouvelle session, imprégnation, prise de contexte, « lis le projet avant de coder ».

## Règles

1. **Lecture seule** jusqu’à validation utilisateur (pas d’écriture code/docs sauf si demandé).
2. **Ne pas** toucher ToolBox / `Xtrem agent` ni le dépôt **Xtrem Rec** (**D-ISO-SUITE**).
3. Répondre en **français**, synthèse courte + prochaine étape.

## Checklist lecture

1. `PROJECT_CONTEXT.md`
2. `docs/TODO.md` (Focus / P0 — skill **`todo`**)
3. `docs/INDEX.md` → DECISIONS → HANDOFF → ARCHITECTURE → FEATURES → DEPENDENCIES
4. `docs/modules/XtremCopy.md` + fiches `docs/files/` utiles
5. `.cursor/rules/*.mdc` + `.cursor/skills/*/SKILL.md`
6. Arborescence code (`Models/`, `Services/`, `ViewModels/`, `Pages/` — quand présents)

## Synthèse attendue

- Où on en est (OK / KO) d’après `PROJECT_CONTEXT.md` + Focus `docs/TODO.md`
- Produit : historique presse-papiers Fluent (**D-CLIP-HISTORY**), local (**D-CLIP-LOCAL**), privacy (**D-CLIP-PRIVACY**)
- Stack : WinUI 3 / .NET 8 / unpackaged x64 (**D-PLAT**)
- Prochaine étape actionnable depuis CONTEXT / Focus TODO
- Questions **uniquement** si une décision manque vraiment
