# Plan vivant — Nomenclature Markdown + Assistant IA

> Document de suivi mis à jour à **chaque phase**. Décisions figées :
> math inline `$…$`, bloc `$$…$$` ; mode markdown-first introduit comme `#md full`
> (distinct du legacy `#md on` = emphase dans la nomenclature à quotes).

## État des phases

| # | Phase | État | Tests |
|---|---|---|---|
| A1 | Core — classifieur markdown-first (`#md full`) + rendu | ✅ fait | 13 ciblés + suite, 0 régression |
| A2 | Core — blocs multi-lignes (listes/tables/blockquote/code fence/paragraphes) | ✅ fait | 10 tests (`MarkdownBlockTests`) |
| A3 | Core — réglage `Settings.Markdown` (Off/Emphasis/Full) ; directive prioritaire | ✅ fait (option) | 4 tests ; bascule défaut *expédiée* reste gated (A4) |
| A4 | Convertisseur classique → `#md full` + commande WPF « Convert to Markdown » | ✅ fait (outil) | 9 tests ; migration des 369 Examples **non faite** (gated CONTRIBUTING) |
| A5 | Sorties — DOCX niveaux h1–h6 | ✅ déjà couvert | styles h1–h6 dans `StyleDefinitionsWriter` + mapping `AddParagraph` ; 6 tests HTML |
| A6 | Éditeur WPF — AutoComplete snippets ✅ ; `#md full` reconnu (highlighter+liste) ✅ ; coloration prose/`$…$` ⬜ | 🟡 partiel | build WPF 0 warn ; coloration prose à vérifier dans l'app |
| B1 | `Calcpad.AI` (SDK Anthropic C#) + `IAiClient` + mock | ✅ fait | 4 tests, build OK |
| B2 | Doc IA (system prompt) réécrite markdown + exemples | 🟡 réf. à jour | prompt + `CALCPAD_LANGUAGE_REFERENCE` maj |
| B3 | Panneau WPF « Assistant IA » (chat streaming, clé DPAPI, opt-in) | ✅ fait | build WPF 0 warn |
| B4 | Assistant phase 2 — outils (get_worksheet/insert/evaluate) | 🟡 cœur fait | 7 tests (`AgenticConversationTests`) ; câblage panneau WPF à faire (app ouverte) |
| B5 | Docs utilisateur + VSCode grammar + resx (3 langues) | 🟡 partiel | grammar + quick-ref + `docs/ai-assistant.md` faits ; resx/menu i18n à faire (app ouverte) |
| C1 | Snippets markdown (catalogue partagé éditeur + IA) | ✅ fait | 13 tests |
| C2 | Éditeur WPF modernisé : complétion snippets inline + insertion | ✅ fait | build WPF 0 warn ; insertion menu + complétion inline |

Légende : ⬜ à faire · 🟡 en cours · ✅ fait & testé

## Décisions de conception

- **Math inline** : `$ expr $`. **Math bloc** : `$$ expr $$`. `\$` = dollar littéral.
- **Titres** : `#`…`######` + espace (ATX). Désambiguïsation avec les mots-clés :
  un mot-clé (`#if`, `#repeat`, `#md`…) est `#` **collé** à un nom connu ; `# ` (espace)
  n'est jamais un mot-clé (`GetKeyword` renvoie None) → titre.
- **Plot** : `$plot`/`$map` en début de ligne restent prioritaires (interceptés avant le
  classifieur). Un `$…$` inline en début de ligne n'est pas un `$plot`.
- **Compatibilité** : `#md full` est opt-in ; `#md on` (legacy, quotes) et le mode classique
  (math par défaut, `'texte'`, `"titre"`) restent intacts. La bascule par défaut est la phase A3,
  précédée du convertisseur (A4) pour migrer les 369 `.cpd` sans rupture visible.

## Journal

### A2 — blocs markdown multi-lignes (`#md full`)
- Nouveau `TryRenderMarkdownBlock` dans la boucle `Parse` : accumule les lignes markdown
  consécutives (listes, tables, blockquotes, code fences, paragraphes) et les rend en **un
  seul bloc** via un pipeline Markdig `UseAdvancedExtensions` (tables pipe/grid, task-lists).
  N'engage que si le bloc fait >1 ligne de contenu ; une ligne isolée garde le chemin per-ligne.
- Lignes à math inline (`$…$`), `$plot`, mots-clés, HTML `<` → `Stop` (gérées per-ligne).
- **Conflit résolu** : `|` est un caractère d'extension de ligne Calcpad (`;|&@:({[`) → cassait
  les lignes de table. En mode `_mdFull`, l'extension implicite par opérateur est désactivée
  (seul `" _"` explicite continue). Garde `_loops.Count == 0` (pas d'accumulation dans une boucle).
- Risque de régression nul : `#md full` n'est utilisé par aucun exemple/test existant.
- Tests : `MarkdownBlockTests` (listes ul/ol fusionnées, table, code fence, blockquote,
  multi-paragraphes, math après bloc, item isolé, bloc caché sous `#if 0`, titre+prose).

### A3 — `Settings.Markdown` (Off / Emphasis / Full)
- Enum `MarkdownMode` + `Settings.Markdown` (défaut **Off** = classique, zéro rupture).
- `Initialize` seede `_isMarkdownOn`/`_mdFull` depuis Settings ; une directive `#md on|off|full`
  dans la source **prime** à partir de sa ligne.
- La bascule du défaut *expédié* (Full par défaut à la livraison) reste volontairement **non faite** :
  c'est une rupture de compat `.cpd` → doit passer par A4 (migration) + accord mainteneurs (CONTRIBUTING).
- Tests : prose sans directive, math inline évalué, défaut classique, directive prioritaire.

### A4 — convertisseur classique → `#md full`
- `ClassicToMarkdownConverter.Convert(source, addDirective)` (Core) : `'texte'` → prose,
  `"titre"` seul → `### titre` (niveau h3 classique), math nu → `$ … $`, mixte reconstruit inline,
  lignes mots-clés/plot/HTML inchangées, échappe `$`/`` ` ``/`\` dans la prose, dé-double `''`/`""`.
- WPF : menu **« Convert to Markdown »** (confirmation + annulable Ctrl+Z) via `LoadTextIntoDocument`
  qui réutilise le chemin de chargement fichier éprouvé.
- Migration des **369 `.cpd` Examples** + régénération des `.html.stub` : **non faite** (gros diff de
  rendu CI + rupture compat → gated CONTRIBUTING). Le convertisseur est l'outil d'amorçage.
- Tests : `MarkdownConverterTests` (texte, titre, math, mixte, mot-clé, `$` littéral, directive
  préfixée/non dupliquée, round-trip rendu).

### A5 — niveaux de titres DOCX h1–h6
- **Déjà couvert** par l'existant : `StyleDefinitionsWriter` définit 6 styles de paragraphe
  `h1`..`h6` (tailles 48/40/36/32/28/26, gras) ; `OpenXmlWriter.AddParagraph` mappe le nom de
  nœud HTML → `ParagraphStyleId`. Les titres markdown-first produisent `<h1>`..`<h6>` (Markdig)
  → styles DOCX distincts automatiquement. Le titre classique (`"titre"`) reste `<h3>`.
- Tests : 6 cas HTML (`# A`..`###### F` → `<h1`..`<h6`).

### B4 — assistant agentique (cœur testé)
- `AiTool` (nom + description + schéma JSON `properties`/`required` + handler), `CalcpadAgentTools`
  (3 outils depuis callbacks hôte : `get_worksheet`, `insert_calcpad`, `evaluate_calcpad`).
- `AgenticConversation` : **boucle d'outils transport-agnostique** (appelle `IModelCaller`, exécute les
  outils, réinjecte les résultats, s'arrête sans outil ou au plafond `MaxIterations`). Événements
  `ToolInvoking`/`ToolInvoked`/`AssistantText`.
- `AnthropicModelCaller` : couche fil **isolée**, mappe le modèle neutre ↔ SDK (`Tool`/`ToolUnion`,
  `ToolUseBlockParam`/`ToolResultBlockParam`, `ContentBlock.TryPickText/TryPickToolUse`). Compile contre
  le vrai SDK 13.6.0 ; **test live (clé API) à faire**.
- Tests : `AgenticConversationTests` (7) avec un `IModelCaller` factice — aucun réseau : outils exécutés,
  résultats réinjectés, outil inconnu → erreur, plafond d'itérations, schémas.
- **Reste** : câblage du panneau WPF (mode agent : construire les outils depuis `GetWorksheetText`/
  `InsertRequested` + un `evaluate` via `ExpressionParser`, lancer `AgenticConversation`). Nécessite un
  rebuild WPF → app fermée.

### B5 (suite) — doc utilisateur assistant
- `docs/ai-assistant.md` (clé API + DPAPI, confidentialité/opt-in, markdown-first, snippets,
  « Convert to Markdown », **mode agent** et ses 3 outils). Ajouté à la nav `mkdocs.yml`.
- **Reste** : resx en/bg/zh pour les libellés UI nouveaux (menus Snippets / Convert to Markdown /
  AI Assistant, libellés du panneau) + i18n des `MenuItem`. Nécessite un rebuild WPF → app fermée.

### C2 / A6 — complétion snippets inline + reconnaissance `#md full` (éditeur WPF)
- `AutoCompleteManager` : les snippets du catalogue partagé (`Calcpad.AI.CalcpadSnippets.All`)
  apparaissent dans la liste de complétion (italique, vert, libellé `clé ⟨Titre⟩`), le **corps
  multi-ligne** porté par `ListBoxItem.Tag`. À la validation, `EndAutoComplete` retire le
  déclencheur tapé et insère le corps via le chemin multi-ligne éprouvé (`InsertAssistantText` →
  highlight + recalcul). Fourni par `SnippetInserter` (Action<string>) câblé dans `MainWindow`.
- `#md full` ajouté à la liste de complétion et reconnu par le highlighter (coloré en mot-clé
  comme `#md on`/`#md off`, au lieu de variable/erreur).
- **Reste (A6)** : coloration dédiée prose vs `$…$` en mode `#md full` dans l'éditeur — demande un
  suivi d'état markdown au niveau *document* (le highlighter parse ligne-à-ligne, hors ordre à
  l'édition) + une **vérification visuelle dans l'app** ; non expédié à l'aveugle.

### B1/B3/C1 — Assistant IA + snippets (volet IA/UI)
- Nouveau projet `Calcpad.AI` (net10) : `IAiClient`, `AnthropicAiClient` (SDK `Anthropic` 13.6.0,
  streaming `claude-opus-5-5`), `AiOptions`, `CalcpadSystemPrompt` (prompt markdown embarqué),
  `CalcpadSnippets` (catalogue partagé), `ISecretStore`.
- WPF : `DpapiSecretStore` (clé chiffrée DPAPI CurrentUser), `AssistantPanel` (chat streaming,
  réglages clé/modèle, opt-in envoi feuille + avertissement confidentialité, snippets),
  `AssistantWindow`, menu « AI Assistant », insertion éditeur via `InsertLines` existant.
- Tests : 4 (contrat IA + prompt) + 13 (snippets rendus sans erreur via le vrai Core).
- Décision modèle : défaut `claude-opus-5-5` ; options Sonnet 5.5 / Haiku 5.5 dans l'UI.

### A1 — Core markdown-first (`#md full`)
- Ajout `GetTokensMarkdown` (classifieur prose-par-défaut, split sur `$`/`$$`, unescape `\$`).
- `#md full` dans `ParseKeywordMd` ; flag `_mdFull` ; câblage dans la boucle `Parse`.
- Réutilise `ParseMarkdown` (Markdig) existant pour emphase/titres/listes inline.
- Tests : `MarkdownFirstTests` (titres h1–h6, gras, math inline/bloc, échappement, plot non capturé).

### B5 (partiel) — grammaire VSCode + docs utilisateur
- `calcpad.tmLanguage.json` : `#md full`, titres `#…######`, code inline, scopes math `$…$`/`$$…$$`
  (priorité laissée à `$plot`/`$map` ; emphase `*` volontairement non ajoutée pour ne pas casser
  la coloration du produit en mode classique). JSON validé.
- `docs/quick-reference.md` : entrée `#md full` documentée.

### Bilan de session (gate vert)
- **Suite complète : 4561 réussis / 0 échec / 5 ignorés** (+29 : A2 ×10, A3/A4/A5 ×19).
  Solution WPF complète : 0 erreur, 0 warning.
- **A2–A5 livrés & testés.** Deux étapes volontairement tenues hors scope (rupture compat `.cpd` →
  gated CONTRIBUTING + énorme diff de rendu CI) : la **bascule du défaut expédié** vers `#md full`
  et la **migration des 369 Examples** + régénération des `.html.stub`. Le convertisseur (A4) est
  l'outil d'amorçage pour ces deux-là quand les mainteneurs donneront le feu vert.
- Reste : A6/C2 (complétion inline snippets dans l'éditeur WPF), B4 (outils agentiques
  get_worksheet/insert/evaluate), B5 (resx 3 langues + i18n menus + page doc utilisateur assistant).
