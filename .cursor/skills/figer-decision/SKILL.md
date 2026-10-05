---
name: figer-decision
description: >-
  Enregistre une decision produit ou technique durable dans docs/DECISIONS.md
  (id D-*), aligne les rules .mdc si besoin, met a jour PROJECT_CONTEXT.
---

# Figer une décision — Xtrem Copy

## Quand

L’utilisateur tranche un choix (UX, stack, clipboard, packaging, logs) ou dit « décidé / figé / on ne revient plus dessus ».

## Étapes

1. Lire `docs/DECISIONS.md` (éviter doublon / contradiction).
2. Attribuer un id `D-*` clair (ex. `D-CLIP-HOTKEY`, `D-LOG-PATH`).
3. Ajouter une ligne dans la bonne section de DECISIONS.
4. Si convention durable → aligner `.cursor/rules/*.mdc` concernée.
5. Maj `PROJECT_CONTEXT.md` + `docs/TODO.md` si l’état produit change.
6. Maj `docs/HANDOFF.md` si piège opérationnel.

## Interdit

- Inventer une décision non demandée.
- Contredire une `D-*` existante sans demande explicite de révision.
- Réintroduire du périmètre Rec (FFmpeg, Discord, capture) dans Copy.
- Contredire **D-CLIP-PRIVACY** / **D-CLIP-LOCAL** / **D-ISO-SUITE** sans révision explicite.
