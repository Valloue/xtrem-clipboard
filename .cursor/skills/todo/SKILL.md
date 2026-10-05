---
name: todo
description: >-
  Lit et met a jour la liste de taches live Xtrem Clipboard (docs/TODO.md).
  A utiliser au /todo, todo, todolist, ce qui reste, ce qui est fait,
  on en est ou, marquer fait, ajouter une tache, ou pour reprendre
  le travail. Inclut les Questions ouvertes sous chaque tache.
---

# Todo — lire / mettre à jour (Xtrem Clipboard)

Fichier unique : **`docs/TODO.md`**. Pas de secrets.

Complète **`PROJECT_CONTEXT.md`** : TODO = checklist ID actionable.

## Quand

- Utilisateur : `/todo`, « todo », « ce qui reste », « ce qui est fait », « on en est où »
- Toute **écriture disque** (règle `todo.mdc`) : maj TODO **dans le même tour**

## Lire (`/todo` seul)

1. **Read** `docs/TODO.md`.
2. Répondre en français :

```
**Focus :** …
**Bloqueur :** … ou « aucun »
**P0 :** IDs ouverts (+ 1 Question clé si présente)
**P1 / P2 :** une ligne chacun s’il reste des items
**Questions ouvertes :** 2–5 prioritaires
**Ne pas refaire :** IDs FAIT pertinents
```

## Écrire

1. Relire `docs/TODO.md`.
2. Maj en-tête (date, Dernière action, Focus, Bloqueur, Version).
3. Cases `- [ ]` / `- [x]` ; IDs `T-CLIP-`, `T-UI-`, `T-HOTKEY-`, `T-PRIV-`, `T-STORE-`, `T-SETUP-`, `T-H-`.
4. FAIT → `[x]` + retirer Questions.
5. Pas de tâches Rec / FFmpeg / Discord ; pas de modif ToolBox/Rec (**D-ISO-SUITE**).
6. Si état produit change → aussi `PROJECT_CONTEXT.md`.

## Questions

Chaque item ouvert doit avoir un sous-bloc **Questions**. Réponse utilisateur → enregistrer et retirer.
