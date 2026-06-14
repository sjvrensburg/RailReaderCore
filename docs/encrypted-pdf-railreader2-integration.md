# Consuming the encrypted-PDF API (RailReaderCore 0.31.0) in railreader2

Most of this is a contained viewer change — one new dialog plus a retry loop at the
single open-document chokepoint. **But 0.31.0 also changes two export call sites: one
is a hard compile break in the CLI, and the "export annotated copy" feature now
refuses encrypted sources.** Read §5 before you build.

File/line references below are against railreader2 as of this writing; treat them as
signposts, not exact anchors.

## 1. The one viewer integration point

Every open path (file picker, recent files, command-line arg, duplicate tab) funnels
through `MainWindowViewModel.Documents.cs` → `OpenDocument(string path)` (lines 27–87),
and the only place a path becomes a document is line 37:

```csharp
var state = _controller.CreateDocument(path);   // inside await Task.Run(...)
```

So all four open paths inherit password support once this method handles it.

## 2. Bump the NuGet refs to 0.31.0

In `src/RailReader2/RailReader2.csproj` (lines 26–40), bump `RailReader.Core`,
`RailReader.Core.Pdfium`, `RailReader.Renderer.Skia`, `RailReader.Core.Analysis`,
`RailReader.Core.Vlm.OpenAI`, **and `RailReader.Export`** (they share one version) from
`0.30.0` → `0.31.0`. *(That version only exists once published — for local testing, use
local-pack + a temporary NuGet.config override per the usual cross-repo flow.)*

## 3. Add a `PasswordDialog`

A new `Views/PasswordDialog.axaml` + `.cs` modelled on `BookmarkNameDialog` (returns
`string?`, null on cancel, via the shared `DialogKeyboard.FocusOnOpen` +
`EnableEscEnterClose` helpers). Differences:

- the `TextBox` uses `PasswordChar="•"` (`RevealPassword` toggle optional),
- it accepts a flag/message to show "Incorrect password — try again" on a retry.

Returns the entered password, or `null` on cancel.

## 4. A prompt-and-retry loop around `CreateDocument`

The subtlety: `CreateDocument` runs inside `await Task.Run(...)` (background thread), but
**Avalonia dialogs must be shown on the UI thread**. So pull the password *resolution*
out of the `Task.Run` into a UI-thread loop that calls a background attempt and, on
`PdfPasswordRequiredException`, awaits the dialog before retrying:

- Try `CreateDocument(path, password)` (start with `password = null`).
- Catch `PdfPasswordRequiredException` (namespace `RailReader.Core.Services`).
  - On the UI thread, show `PasswordDialog`. Use `ex.WrongPassword`: `false` → first
    prompt ("This PDF is password-protected"); `true` → "Incorrect password."
  - Dialog returns `null` (cancel) → abort the open quietly (no error toast —
    cancellation isn't a failure).
  - Otherwise loop with the new password.
- Any other exception → the existing `catch` at lines 81–86 (log + `ShowStatusToast`)
  handles it unchanged.

The successful password flows into the rest of the method automatically —
`LoadAnnotations` (line 46), `LoadPageBitmap`, `AddDocument`, text/link extraction,
search, and annotation save all read it off the already-opened `IPdfService.Password`
inside Core. **You never pass the password again after `CreateDocument`.**

## 5. Export call sites — these will not compile / will throw until updated

Three places consume the export APIs, and all three are affected by 0.31.0:

**(a) HARD COMPILE BREAK — `RailReader2.Cli/Commands/ExportCommand.cs:73`**
`IMarkdownExportService.ExportAsync` gained a `password` parameter, inserted **before**
`progress`:

```csharp
ExportAsync(string pdfPath, TextWriter output, MarkdownExportOptions options,
            string? password = null, IProgress<ExportProgress>? progress = null,
            CancellationToken ct = default)
```

The current positional call
`service.ExportAsync(pdfPath, output, options, progress, CancellationToken.None)` now
binds `progress` to the `password` slot → **build error**. Fix by passing the password
(recommended: add a `--password` CLI option) or using named arguments:

```csharp
service.ExportAsync(pdfPath, output, options,
                    password: pwd, progress: progress, ct: CancellationToken.None);
```

This is also what enables **Markdown export of encrypted PDFs** — thread `--password`
through to here.

**(b) "Export annotated copy" now REFUSES encrypted sources —
`MainWindowViewModel.Annotations.cs:278`**
`AnnotationExportService.Export(tab.Pdf, …)` flattens into a brand-new PDF, which cannot
carry encryption. Core now throws `InvalidOperationException` when `tab.Pdf.Password` is
set, rather than silently writing a **plaintext copy of a confidential exam**.
`tab.Pdf.Password` is already set from the open document, so there's no new plumbing —
just catch it and tell the user, e.g.:

> "This PDF is password-protected. A flattened export would remove its password, so it's
> blocked. Your annotations are already saved inside the encrypted PDF."

(If you ever want an explicit "export decrypted copy anyway" path, that's a deliberate
UX decision — Core won't do it silently.)

**(c) CLI annotated export — `RailReader2.Cli/Commands/AnnotationsCommand.cs:42`**
Same `AnnotationExportService.Export` refusal applies. To even open an encrypted source
here you'll need a `--password` passed to `CreatePdfService`; then surface the refusal as
a clear CLI error rather than a stack trace.

## 6. Markdown export now includes all annotation types

Beyond passwords: Markdown export now surfaces **underline, strikeout, squiggly,
FreeText (typewriter), carets, and commented rect/freehand drawings** — in document
order — and includes the **reviewer's comment** attached to a text markup (previously
dropped). No railreader2 code change needed to benefit; it's automatic in the output.
Relevant for moderation / dissertation markup.

**If railreader2 has an export test project:** `PageMarkdownBuilder.PageAnnotations`
changed shape — it now wraps a single ordered `IReadOnlyList<Annotation>` instead of
separate `Highlights`/`Notes` lists. Direct constructors of that type need updating
(source-breaking, test-only). `IMarkdownExportService` consumers are unaffected.

## 7. Nothing else on the viewer side

- Recent-files / command-line / duplicate-tab paths need no changes (they all call
  `OpenDocument`).
- Annotation save-back into an encrypted exam **stays encrypted** automatically
  (verified Core-side) — the in-place moderation workflow needs no special handling.

## 8. Decisions for the railreader2 side

- **Don't persist the password.** Recent-files reopen and "duplicate tab" will re-prompt
  — the safe default for exam material. If you want to skip re-prompting a duplicated
  tab, cache it in-memory keyed by path for the session only (a deliberate choice, not
  required).
- **Cancellation UX:** dialog-cancel = "changed my mind," not an error — skip the
  failure toast.
- **Command-line/auto-open at startup** (`App.axaml.cs:77`): the prompt appears during
  `window.Opened`; confirm the main window is shown first so the modal has a parent (it
  already awaits via `FireAndForget`, so likely fine — worth a visual check).
- **Export password source:** the desktop annotated-copy export reads `tab.Pdf.Password`
  from the open tab, so no prompt needed there. The CLI has no open document, so it needs
  an explicit `--password` option for both export commands.
