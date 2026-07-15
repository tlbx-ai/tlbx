# tlbx Release Size Audit - 2026-05-08

## Ziel

tlbx war auf dem Windows-Downloadpfad einmal bei etwa 10 MiB gezippter
Downloadgroesse und liegt aktuell bei etwa 19 MiB. Die Downloadgroesse ist hier
aber nur ein Signal. Das primaere Ziel ist, Dead Weight zu finden, das Menschen
und AI-Agenten bei Edits verwirrt, Kontextfenster aufblaeht, falsche Faehrten
legt oder Wartung erschwert. Diese Analyse trennt deshalb Downloadtreiber von
Repo-/Agent-Hygiene-Themen und baut eine auditierbare Keep/Prune/Delete-Tabelle.

## Methodik

- GitHub-Release-Asset-Historie fuer `mt-win-x64.zip` ausgewertet: 1.563
  Releases.
- Repraesentative Release-Zips heruntergeladen und per ZipArchive analysiert:
  `v7.0.0-dev`, `v8.6.23-dev`, `v8.6.24-dev`, `v8.7.28-dev`,
  `v8.7.29-dev`, `v8.9.88-dev`, `v8.9.89-dev`, `v9.8.28-dev`.
- Aktuelle Quellen in `Q:\repos\MidTermReleaseHotfix-987-csiu` analysiert.
- Frontend-Bundle mit esbuild-Metafile analysiert.
- Statische Assets aus `wwwroot` nach Raw- und Brotli-Groesse geschaetzt.
- CSS-Erreichbarkeit konservativ ueber statische HTML/JS/TS-Suche geprueft.
  Ergebnis ist kein automatischer Delete-Beweis, sondern ein Audit-Hinweis.

Artefakte liegen lokal unter:
`C:\Users\johan\.codex\artifacts\midterm-size-analysis\`

## Hauptbefund

Die aktuelle Windows-Zip ist kein Sammelsurium aus vielen doppelten Dateien.
Sie besteht praktisch nur aus drei Binaries plus Metadaten:

| Release | Datei | Zip-compressed | Uncompressed |
|---|---:|---:|---:|
| v9.8.28-dev | `mt.exe` | 13.92 MiB | 35.46 MiB |
| v9.8.28-dev | `mtagenthost.exe` | 3.64 MiB | 9.11 MiB |
| v9.8.28-dev | `mthost.exe` | 1.63 MiB | 3.81 MiB |
| v9.8.28-dev | Metadata | <0.01 MiB | <0.01 MiB |
| **Summe** | `mt-win-x64.zip` | **19.19 MiB** | **48.38 MiB** |

Damit ist die Groessenfrage technisch in zwei Hauptachsen zu behandeln:

1. **Packaging/Update-Kanal:** Web-only Updates laden aktuell faktisch weiter
   ein Vollpaket mit Host-Binaries.
2. **`mt.exe`:** Native-AOT-Binary plus eingebettete Frontend-Assets,
   insbesondere Dev-Sourcemaps, Swagger UI, OpenAPI-Schema und der gewachsene
   App-Code.

Fuer das eigentliche Ziel - weniger Dead Weight im Arbeitskontext - ist der
Packaging-Hebel aber bewusst nachrangig. Ein separater Web-only Download wuerde
zwar viel Downloadgroesse sparen, erhoeht aber die Update-Prozess-Komplexitaet.
Wenn das Ziel nicht Downloadkosten, sondern klarere Codebasis und bessere
Agentenarbeit ist, sollten zuerst Quellbaum-, Build- und Dokumentationsaltlasten
entfernt werden.

## Release-Groessenverlauf

| Vergleich | Zip vorher | Zip nachher | Delta | Primaere Deutung |
|---|---:|---:|---:|---|
| `v7.0.0-dev` -> `v9.8.28-dev` | 10.40 MiB | 19.19 MiB | +8.79 MiB | `mt.exe` +4.83 MiB, neuer `mtagenthost.exe` +3.64 MiB, `mthost.exe` +0.31 MiB |
| `v8.6.23-dev` -> `v8.6.24-dev` | 10.96 MiB | 12.46 MiB | +1.50 MiB | `mthost.exe` kam wieder ins Windows-Zip |
| `v8.7.28-dev` -> `v8.7.29-dev` | 12.89 MiB | 15.04 MiB | +2.15 MiB | AgentHost/App Server Controller-Runtime wurde eingefuehrt |
| `v8.9.88-dev` -> `v8.9.89-dev` | 15.63 MiB | 17.25 MiB | +1.61 MiB | Code-Diff klein; Groesse kommt durch wieder enthaltenes `mthost.exe` |
| `v9.8.27-dev` -> `v9.8.28-dev` | 19.19 MiB | 19.19 MiB | ca. 0 MiB | Kein neuer Groessenregressionssprung in 9.8.28 |

## Eingebettete Web-Assets

Die Publish-Pipeline erzeugt `wwwroot` und bettet komprimierte statische Assets
in `mt.exe` ein. Das generierte `wwwroot` ist in Git ignoriert, also keine
Quellcode-Duplikation, aber es ist relevant fuer `mt.exe`.

| Asset | Raw | Brotli-Schaetzung | Bewertung |
|---|---:|---:|---|
| `js/terminal.min.js.map` | 6,468.1 KiB | 1,038.8 KiB | Groesster einzelne Prune-Kandidat in Dev-Releases |
| `swagger/swagger-ui-bundle.js` | 1,489.5 KiB | 325.9 KiB | Prune-Kandidat, wenn Swagger nicht im normalen Runtime-Paket sein muss |
| `js/terminal.min.js` | 1,390.0 KiB | 297.7 KiB | Kern-App, nicht einfach loeschbar |
| `swagger/swagger-ui-standalone-preset.js` | 245.8 KiB | 63.4 KiB | Teil von Swagger UI |
| `js/html2canvas.min.js` | 194.0 KiB | 36.7 KiB | Wird fuer Web-Preview-Screenshots lazy-loaded |
| `css/app.css` | 300.1 KiB | 36.6 KiB | Stale-Style-Audit sinnvoll, aber Downloadgewinn begrenzt |
| `swagger/swagger-ui.css` | 174.4 KiB | 18.6 KiB | Teil von Swagger UI |
| `index.html` | 150.7 KiB | 16.6 KiB | App-Shell |
| `locales/de.json` | 60.1 KiB | 14.7 KiB | i18n, behalten ausser Produktentscheidung |
| `locales/en.json` | 60.9 KiB | 13.7 KiB | i18n, behalten ausser Produktentscheidung |
| `openapi/openapi.json` | 165.6 KiB | 9.7 KiB | API-Vertrag; wichtig fuer TS-Type-Generation und spaetere Automatisierbarkeit |

## Frontend-Bundle-Treiber

esbuild-Metafile fuer den aktuellen `src/ts/main.ts`-Bundle:

| Input | Bytes im minifizierten Output | Bewertung |
|---|---:|---|
| `@xterm/xterm/lib/xterm.mjs` | 337.3 KiB | Terminal-Kern, Keep |
| `@xterm/addon-webgl/lib/addon-webgl.mjs` | 126.5 KiB | Performance/Rendering, Prune nur nach Benchmark |
| `@xterm/addon-search/lib/addon-search.mjs` | 37.6 KiB | Feature-Kern |
| `@xterm/addon-unicode11/lib/addon-unicode11.mjs` | 29.9 KiB | Terminal-Korrektheit |
| `modules/agentView/index.ts` | 28.5 KiB | App Server Controller/Agent UI, weiter auditieren |
| `modules/sidebar/spacesTreeSidebar.ts` | 25.4 KiB | Aktive UI |
| `modules/managerBar/managerBar.ts` | 21.4 KiB | Aktive UI |
| `modules/smartInput/smartInput.ts` | 21.2 KiB | Aktive UI |
| `modules/agentView/historyRender.ts` | 20.8 KiB | App Server Controller/History UI, weiter auditieren |

## Keep / Prune / Delete Tabelle

| Entscheidung | Bereich | Kandidat | Evidenz | Impact | Risiko | Empfehlung |
|---|---|---|---|---:|---|---|
| **Keep** | Packaging | Ein einfaches Vollpaket trotz `webOnly`-Manifest | Aktuelles `v9.8.28-dev` Zip: Hosts = 5.27 MiB compressed; Release-Skripte markieren `webOnly`, Archive enthalten Hosts trotzdem | 0 MiB | Niedrig | Nicht optimieren, solange Downloadgroesse nicht das Ziel ist. Ein zweiter Asset-Kanal wuerde den Update-Prozess komplexer machen |
| **Keep** | Packaging | Vollstaendiges Windows-Zip mit `mt.exe`, `mthost.exe`, `mtagenthost.exe` | Erstinstallation und Full-runtime-refresh brauchen Host-Binaries | 0 MiB | Niedrig | Als Full/Installer-Artefakt behalten |
| **Prune** | Dev-Release Assets | `terminal.min.js.map` in oeffentlichen Dev-Releases | `frontend-build.ps1 -DevRelease` aktiviert linked sourcemap; Brotli-Schaetzung 1,038.8 KiB | **ca. 1.0 MiB** | Niedrig, wenn separate Debug-Artefakte bleiben | Sourcemap aus normalem Download entfernen, separat als Debug/Symbol-Asset hochladen |
| **Keep** | API Contract | `openapi/openapi.json` im Build und in der Runtime | C# DTOs -> OpenAPI -> TypeScript Types -> typed API client; Runtime-Schema kann spaeter Automatisierbarkeit und Agent-Inspektion verbessern | 0 MiB | Niedrig | Behalten. Nicht als Dead Weight behandeln, sondern als maschinenlesbaren Vertrag dokumentieren |
| **Keep** | Runtime-Doku | Swagger UI im normalen Runtime-Binary | `frontend-build.ps1` kopiert Swagger UI immer; Swagger UI Brotli grob 408 KiB ohne OpenAPI-Schema | 0 MiB | Niedrig | Behalten. API-Inspektion und spaetere Agent-Automatisierbarkeit sind wertvoller als die kleine Groessenersparnis |
| **Prune** | Compile Settings | `OptimizationPreference=Speed`, `IlcOptimizationPreference=Speed` | `Ai.Tlbx.MidTerm.csproj` optimiert Native AOT auf Speed, nicht Size | Potenziell MiB, ungemessen | Mittel/Hoch: Startzeit/CPU kann schlechter werden | Size-Build-Benchmark einfuehren, nicht blind umstellen |
| **Prune** | Compile Settings | `UseSizeOptimizedLinq=false` | Explizit auf nicht-size-optimiertes LINQ gesetzt | Unbekannt | Mittel | A/B Publish-Messung + Smoke-Test; bei Gewinn ohne Laufzeitverlust umstellen |
| **Prune** | Compile Settings | `StackTraceSupport=true` | Stacktraces bleiben aktiv, andere Diagnosefeatures sind schon reduziert | Unbekannt | Hoch: Fehlerdiagnostik leidet | Nur fuer Stable/Dev differenziert pruefen; nicht pauschal abschalten |
| **Keep** | Compile Settings | `PublishAot=true`, `PublishSingleFile=true`, `SelfContained=true`, `TrimMode=full` | Kern der schnellen, standalone installierbaren MidTerm-Auslieferung | 0 MiB | Niedrig | Behalten; Groessenoptimierung innerhalb dieses Modells machen |
| **Keep** | Terminal Bundle | `@xterm/xterm` | Groesster Bundle-Input, aber Kern des Produkts | 0 MiB | Niedrig | Nicht loeschen; nur Upstream-/Tree-shaking beobachten |
| **Keep** | Terminal Bundle | `@xterm/addon-webgl` | 126.5 KiB minified Output; WebGL-Renderer ist Performance-relevant | 0 MiB | Niedrig | Behalten |
| **Keep** | Runtime Asset | `html2canvas.min.js` | Wird serverseitig fuer Web-Preview-Screenshots lazy-loaded (`WebPreviewProxyMiddleware`) | 0 MiB | Niedrig | Behalten; mit 36.7 KiB Brotli kein prioritaerer Hebel |
| **Keep** | Fonts | Cascadia, JetBrains, Terminus, midFont | In Settings auswählbar, vorladbar, Terminal-Messung nutzt Font-Verfuegbarkeit | 0 MiB | Mittel | Assets behalten; optional nur ausgewaehlte Nicht-Default-Fonts lazy-preloaden |
| **Prune** | Fonts/Initial Load | Alle Terminal-Fonts im `index.html` preloaden | `index.html` preloaded Cascadia, SemiBold, JetBrains, Terminus | Kein Zip-Gewinn, aber Load/Memory | Niedrig/Mittel | Fuer Performance: nur Default-Font preloaden, weitere Fonts bei Auswahl laden |
| **Keep** | Locales | `locales/*.json` | i18n-Modul und Settings-Sprachauswahl nutzen Locale-Dateien | 0 MiB | Mittel | Behalten; gesamter Locale-Raw-Satz ca. 653 KiB, Brotli deutlich kleiner |
| **Keep** | PWA/Branding | `android-chrome-*.png`, `site.webmanifest`, `favicon.ico` | Referenziert von App/Login/Trust-HTML und Browser-Install-Kontext | 0 MiB | Niedrig | Behalten; nur Bildoptimierung pruefen |
| **Done** | Package Hygiene | `dependencies.midterm = "file:"` | Package heisst selbst `midterm`; kein Import von Package `midterm`; Lockfile legte `node_modules/midterm` als Link an | Kein Zip-Gewinn | Niedrig | Entfernt; Lockfile neu generiert |
| **Done** | Dependency Hygiene | xterm-Pakete in `devDependencies` trotz Runtime-Bundle | `src/ts` importiert `@xterm/*`, Build nutzt aber Runtime-Code aus diesen Paketen | Kein Zip-Gewinn | Niedrig | Nach `dependencies` verschoben; Lockfile neu generiert |
| **Prune** | Dead-Code Tooling | `knip --production` ohne Repo-Konfig | Knip markiert sogar `main.ts`/genutzte Module falsch als unused | Kein direkter Gewinn | Niedrig | Knip-Konfig fuer esbuild Entry/Generated Files bauen, erst dann als Delete-Gate nutzen |
| **Detailed Audit Later** | Styles | CSS-Klassen ohne statische Referenz, z.B. `md-content`, `agent-history-reasoning`, `agent-history-plan`, `chat-msg-tool-call`, `host-status`, `session-resize`, `git-diff-overlay*` | Statische Suche findet diese Klassen nur in CSS; diese Suche erkennt dynamische DOM-Klassen nicht sicher | Max. 36.6 KiB Brotli fuer gesamte CSS | Mittel/Hoch | Nicht als Loeschliste behandeln. Spaeter im Detail mit Runtime-/DOM-Checks pruefen; Annahme bis dahin: wahrscheinlich in Benutzung |
| **Detailed Audit Later** | Styles | Alter Markdown-Block `.md-content` | 27 CSS-Selector-Treffer, keine statische Referenz ausser CSS gefunden | Klein | Mittel/Hoch | Nicht blind loeschen; spaeter gegen echte Markdown-/Agent-Rendering-Pfade pruefen |
| **Detailed Audit Later** | Styles | Git-Diff-Overlay-Styles | Nur CSS-Treffer fuer mehrere `git-diff-*` Klassen | Klein | Mittel/Hoch | Nicht blind loeschen; spaeter gegen echte Git-/Diff-UI-Pfade pruefen |
| **Keep** | Generated Assets | `src/Ai.Tlbx.MidTerm/wwwroot` | Von `.gitignore` abgedeckt; keine eingecheckte Duplikation | 0 MiB | Niedrig | Nicht als Repo-Duplicate behandeln; Publish-Payload trotzdem messen |
| **Keep** | Docs/Marketing Assets | `docs/marketing/**` Bilder/Skripte | Nicht im Release-Zip enthalten | 0 MiB Download | Niedrig | Behalten |
| **Keep** | OpenAPI TS Types | `api.generated.ts` | Type-only und Client nutzt `openapi-fetch`; grosser Quelltext ist nicht 1:1 Bundle-Groesse | 0 MiB direkt | Niedrig | Behalten; Bundle-Impact liegt im Client, nicht im ganzen generated file |

## Priorisierte Massnahmen

1. **Package-Hygiene fixen:** `dependencies.midterm = "file:"` entfernen und
   Lockfile neu generieren. **Status: erledigt.** Das spart kaum Bytes, entfernt
   aber eine klare falsche Faehrte fuer AI-Agenten und Dependency-Tools.
2. **Dead-Code-Gate belastbar machen:** Knip oder ein eigenes Reachability-Script
   so konfigurieren, dass es esbuild-Entry, generierte Dateien und dynamische
   Browserpfade korrekt versteht. Erst danach Delete-Kandidaten automatisch
   ableiten.
3. **Stale CSS gezielt auditieren:** `.md-content`, `git-diff-overlay*`,
   alte Chat-/Host-/Session-Klassen und Agent-History-Varianten mit echten
   DOM-/Feature-Pfaden pruefen. Ziel ist weniger Bearbeitungsrauschen, nicht
   primär Downloadersparnis.
4. **Dev-Sourcemaps aus dem normalen Download auslagern.** Spart ca. 1 MiB
   und ist fuer Nutzer der Dev-Downloads normalerweise nicht noetig.
5. **Swagger UI behalten:** OpenAPI selbst bleibt, weil es C# und TS
   type-technisch zusammenhaelt und spaeter MidTerm-Automatisierung erleichtern
   kann. Die Swagger UI bleibt ebenfalls, weil API-Inspektion fuer Menschen und
   Agents wertvoller ist als die kleine Groessenersparnis.
6. **AOT-Size-Benchmark als CI/Script hinzufuegen.** Speed-vs-Size-Settings
   koennen relevant sein, sollten aber nicht ohne Startzeit- und Smoke-Test
   geaendert werden.

Explizit nicht priorisiert: ein separater Web-only Release-Asset-Kanal. Er
spart zwar ca. 5.27 MiB Download, aber er macht den Update-Prozess komplexer
und adressiert nicht das eigentliche Dead-Weight-/Agent-Kontext-Problem.

## Was nicht der Haupttreiber ist

- Kein Hinweis auf doppelt eingechecktes `wwwroot`: es ist generiert und
  gitignored.
- Keine vielen Einzelassets im Zip: das Zip enthaelt fast nur Binaries.
- CSS-Aufraeumen ist sinnvoll, aber selbst komplette `app.css`-Eliminierung
  waere nur ca. 36.6 KiB Brotli. Das ist Hygiene, nicht der 10->19-MiB-Sprung.
- `html2canvas` ist belegt genutzt und klein im komprimierten Payload.
- Das Weglassen von `mthost.exe`/`mtagenthost.exe` aus bestimmten Update-Zips
  waere Downloadoptimierung, aber keine Codebasis- oder Agent-Hygiene. Es ist
  deshalb kein guter erster Schritt fuer dieses Ziel.
- Swagger UI, WebGL, Fonts, Locales, `html2canvas` und `docs/marketing/**`
  sind nach Audit-Entscheidung keine Prune-Kandidaten.

## Audit-Checkliste

| Anforderung | Evidenz | Status |
|---|---|---|
| Gezippte Downloadgroesse historisch analysiert | 1.563 GitHub-Release-Assets ausgewertet | Erfuellt |
| Aktuelle 19-MiB-Zip auf Bestandteile analysiert | `v9.8.28-dev` Zip-Eintraege: `mt.exe`, `mthost.exe`, `mtagenthost.exe` | Erfuellt |
| Stale Code betrachtet | Bundle-Metafile, Knip-Grenzen, Package-Hygiene-Kandidaten | Erfuellt |
| Stale Styles betrachtet | CSS-Klassenreachability mit 101 Kandidaten ohne statische Referenz | Erfuellt |
| Unused Assets betrachtet | Static assets + Brotli-Schaetzungen + Nutzungssuche | Erfuellt |
| Compile Settings betrachtet | Native-AOT-Properties im `.csproj` bewertet | Erfuellt |
| Duplicate Assets betrachtet | `wwwroot` gitignored; Zip enthaelt keine Asset-Duplikate | Erfuellt |
| Keep/Prune/Delete-Tabelle gebaut | Tabelle oben | Erfuellt |
