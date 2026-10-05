---
name: securite
description: >-
  Audit et durcissement de securite Xtrem Copy — app WinUI 3 / C#, historique
  presse-papiers local, hotkey, stockage LocalAppData, logs. A declencher pour
  audit securite, privacy clipboard, revue stockage, hooks, ou chaine de build.
---

# Sécurité — client Xtrem Copy

## Règles non négociables

1. **Privacy clipboard (**D-CLIP-PRIVACY**)** : ne jamais logger le contenu du presse-papiers en clair ; exclusions apps respectées.
2. **Stockage local (**D-CLIP-LOCAL**)** : pas d’upload / cloud / télémétrie du contenu sans accord explicite.
3. **Hotkey** : uniquement le raccourci déclaré ; pas de keylogger / hook clavier large.
4. **Pas de secrets en clair** dans le code managé ou les logs.
5. **Client ≠ frontière absolue** : relever la barre, signaler les limites.

## Checklist audit

- Écoute clipboard : permissions, thread, fuites mémoire (images)
- Fichiers LocalAppData : ACL, chiffrement optionnel (DPAPI) si décidé
- Popup / focus : ne pas coller dans la mauvaise fenêtre
- Logs : métadonnées OK (taille, type, process source), contenu interdit
- Dépendances NuGet / publish unpackaged

## Hors périmètre

Serveur Keygen / site / Discord Rec — autre dépôt.
