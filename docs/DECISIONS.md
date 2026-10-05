# DECISIONS — Xtrem Clipboard

Décisions durables. Ne pas contredire sans demande explicite de révision.

## Produit / isolation

| ID | Décision |
|----|----------|
| **D-APP-CLIPBOARD** | Ce dépôt = app **Xtrem Clipboard** (historique presse-papiers), autonome. Remplace l’ancien id `D-APP-COPY` / nom « Xtrem Copy ». |
| **D-ISO-SUITE** | Ne pas modifier ToolBox ni **Xtrem Rec** depuis ce workspace sans demande explicite. |
| **D-FR** / **D-CODE** | UI + assistant en français ; code (idents) en anglais ; commentaires en français. |
| **D-OPENSOURCE** | Projet **open source** sur GitHub (dépôt public). |
| **D-LICENSE-MIT** | Licence **MIT** (`LICENSE` à la racine). |

## Plateforme

| ID | Décision |
|----|----------|
| **D-PLAT** | C# · WinUI 3 · .NET 8 · Windows App SDK ; OS min Windows 10 1809. |
| **D-UNPACK** | Unpackaged. |
| **D-PUB-NOTRIM** | Publish self-contained x64, `PublishTrimmed=false`. |
| **D-LOG-LOCAL** | Logs Serilog sous LocalAppData. |
| **D-SETTINGS** | `settings.json` persisté en temps réel. |
| **D-VERSION** | SemVer ; bump à chaque écriture IA ; source = `PROJECT_CONTEXT.md`. |

## UI

| ID | Décision |
|----|----------|
| **D-UI-SHELL** | Fluent WinUI : Historique (principal) + Réglages. |
| **D-UI-ACCENT** | Accent Pale Red Windows 11 (`#D13438`), indépendant de l’OS. |

## Presse-papiers

| ID | Décision |
|----|----------|
| **D-CLIP-HISTORY** | Remplacer l’historique Windows (Win+V) par Xtrem Clipboard. |
| **D-CLIP-V1** | V1 = texte Unicode + images ; épingler ; recherche. |
| **D-CLIP-HOTKEY** | Raccourci global configurable pour ouvrir l’historique. |
| **D-CLIP-LOCAL** | Stockage LocalAppData uniquement ; pas de cloud V1. |
| **D-CLIP-PRIVACY** | Exclusions apps ; ne jamais logger le contenu clipboard en clair. |

## Reporté (pas encore décidé)

- Format HTML/RTF / fichiers dans l’historique
- Remplacement forcé de Win+V vs raccourci dédié
- Compte / auto-update (réutiliser patterns Rec plus tard)