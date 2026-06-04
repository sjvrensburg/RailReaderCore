# Native PDF Annotations as the Canonical Store — Implementation Plan

**Goal:** Make the PDF's own `/Annots` the single source of truth for annotations,
so RailReader interoperates natively with Acrobat Pro review workflows (the common
case in university environments). This is a **breaking change** to the annotation
persistence model.

**Decisions locked (2026-06-04):**
- **Sidecar:** narrow fallback only — PDF is canonical/default; JSON sidecar retained
  solely for non-writable PDFs (read-only / signed / no write-permission) and as the
  migration source for existing users' sidecars. Never two parallel stores for a
  writable PDF.
- **Bookmarks:** RailReader named bookmarks move into the PDF **name tree / outline**
  (named destinations) so they travel with the file and appear in Acrobat.
- **Fidelity scope — academic markup set:** Highlight, Underline, StrikeOut, Squiggly,
  Caret, Text (sticky), FreeText, Ink, Square — plus reply threads (`/IRT`) and review
  states (Accepted/Rejected/Cancelled/Completed via `/State`+`/StateModel`). Any other
  subtype is read + preserved untouched on save, just not user-creatable.

**Driving artifact:** `Day-ahead-photovoltaic-power-forecasting---Short.pdf` — Acrobat
26.1 review copy (reviewer `cclohessy`): 22 `/Highlight` (each with a `/Contents`
comment), 17 `/Text`, 1 `/Caret`, 40 `/Popup`, 1 reply (`/IRT`), 215 `/Link` (GoTo,
out of scope — `IPdfLinkService` owns those).

---

## Architecture

The seam already exists: `IAnnotationStore` (Core) is the persistence boundary; the
JSON `AnnotationService` is just one impl living in `Core.Pdfium`. We keep the
interface and swap the default impl. **Core stays portable** (zero native deps): the
model records live in Core, the PDF-binding store lives in platform siblings.

```
IAnnotationStore (Core)          ← unchanged seam, possibly widened
  ├─ PdfAnnotationStore          ← NEW default. Desktop: PDFium. Web/mobile: PdfPig.
  └─ JsonAnnotationStore         ← former AnnotationService, demoted to fallback
       (used only when the target PDF is not writable, or to migrate old sidecars)
```

A `CompositeAnnotationStore` picks per-PDF: if the PDF is writable → `PdfAnnotationStore`;
else → `JsonAnnotationStore`. One source of truth per document.

Two correctness pillars run through every phase:
- **`/AP` regeneration** — every create/edit must (re)generate the appearance stream so
  the annotation renders natively in Acrobat and other viewers, not just our overlay.
- **Incremental save** (`FPDF_SaveAsCopy` + `FPDF_INCREMENTAL`) — never the
  new-document/`FPDF_ImportPages` path (it drops source `/Annots` + AcroForm and breaks
  signatures). The existing `AnnotationExportService` stays, but only for "export a
  flattened copy," never for in-place edits.

---

## PR 1 — Read native annotations (view-only)

Outcome: opening the file surfaces all 40 comments in the existing viewer/rail
pipeline. No write-back. Lowest risk.

1. **`PdfiumNative.cs` — bind read APIs** (all under `lock (PdfiumGate.Lock)`):
   `FPDFPage_GetAnnotCount`, `FPDFPage_GetAnnot`, `FPDFAnnot_GetSubtype`,
   `FPDFAnnot_GetRect`, `FPDFAnnot_GetColor`, `FPDFAnnot_CountAttachmentPoints`,
   `FPDFAnnot_GetAttachmentPoints`, `FPDFAnnot_GetStringValue` (Contents/T/M/NM/Subj/RC),
   `FPDFAnnot_GetNumberValue` (CA), `FPDFAnnot_HasKey`,
   `FPDFAnnot_GetInkListCount`/`FPDFAnnot_GetInkListPath`,
   `FPDFAnnot_GetLinkedAnnot` (for `/IRT`/`/Popup`). Add subtype constants
   `UNDERLINE=10 STRIKEOUT=11 SQUIGGLY=12 CARET=14 INK=15 POPUP=16 LINK=2 FREETEXT=3`.

2. **Model extension (`Models/Annotations.cs`)** — round-trip metadata on base
   `Annotation` (so reads never drop authorship):
   ```csharp
   public string? Author { get; set; }        // /T
   public string? Contents { get; set; }       // /Contents
   public string? Subject { get; set; }        // /Subj
   public string? NativeId { get; set; }       // /NM (UUID) — identity/dedupe key
   public DateTimeOffset? CreatedUtc { get; set; }   // /CreationDate
   public DateTimeOffset? ModifiedUtc { get; set; }  // /M
   public ReviewState State { get; set; }      // /State+/StateModel (None default)
   public string? InReplyTo { get; set; }      // /IRT → parent NativeId
   public AnnotationSource Source { get; set; } // InPdf | Sidecar
   ```
   - New `TextMarkupAnnotation` base (QuadPoints) → `HighlightAnnotation`,
     `UnderlineAnnotation`, `StrikeOutAnnotation`, `SquigglyAnnotation`.
   - New `CaretAnnotation` (+`/RD`), `FreeTextAnnotation`. Register all in the
     `[JsonDerivedType]` block (sidecar fallback must still serialize them).
   - `HighlightAnnotation`'s comment now lives in base `Contents` (covers the 22).
   - **Color fidelity:** store the original float `/C` + `/CA` alongside the hex
     convenience accessor — the hex round-trip is lossy (`.0235291`).
   - **Pre-existing bug fixed here:** `AnnotationFile.Pages`/`Bookmarks` were get-only
     collections; the source-gen serializer does not repopulate get-only collections on
     deserialize, so `AnnotationService.Load` returned them **empty** — saved annotations
     and bookmarks silently vanished on reload. Added setters (canonical fix). Worth a
     CHANGELOG bug-fix entry at release time.

3. **`PdfAnnotationReader` (new, `Core.Pdfium`)** — reads a page's `/Annots`, maps to
   Core types. Geometry is the inverse of `AnnotationExportService`: reuse
   `GetCropBoxTransform`, add `PdfPointToPage` (inverse of `PagePointToPdf`); honor
   `/Rotate`. QuadPoints → `List<HighlightRect>`. Resolve `/Popup`→parent and
   `/IRT`→`InReplyTo`. Skip `/Link`.
   - **Color caveat (verified on the driving PDF):** `FPDFAnnot_GetColor` returns
     `false` (all-zeros) when an annotation carries a baked `/AP` appearance stream —
     true for all 40 Acrobat annots here. The reader must fall back: per-subtype default
     color, or parse `/C` directly. Do **not** trust GetColor alone.

4. **Wire into load** — ✅ `CompositeAnnotationStore` (Core.Pdfium) reads in-PDF annots
   via `PdfAnnotationReader` and merges the sidecar on top, deduped by `/NM` (native
   wins); bookmarks carried from the sidecar. `Save` persists **only** RailReader-authored
   annotations (`Source != InPdf`) + bookmarks to the sidecar — native annots are
   read-only in PR 1 and re-read from the PDF each load. `CompositeAnnotationStore.Default`
   wraps `AnnotationService.Default`.
   - **Cross-repo wiring to light it up:** railreader2 currently injects
     `AnnotationService.Default` as its `IAnnotationStore`. Switch that to
     `CompositeAnnotationStore.Default` and native comments appear in the viewer
     (line-detection / rail-mode consume `AnnotationFile` unchanged). No Core API change
     needed — it's a one-line wiring swap in the desktop app.

5. **Tests** — `TestFixtures` emits a synthetic PDF with Highlight+Text+Caret via the
   write path; reopen and assert geometry/author/contents/state recovered. Crop-box +
   rotation round-trip tolerance test.

## PR 2 — Write into the PDF (canonical store)

Outcome: create/edit/delete persists into the PDF, preserving all other annots,
AcroForm, and signatures.

1. ✅ **`PdfAnnotationWriter`** (commit 1bfdd72) — `AddAuthoredAnnotations` loads the
   existing doc, appends authored annots, and saves via `FPDF_SaveAsCopy(FPDF_INCREMENTAL)`,
   preserving existing `/Annots` + AcroForm + signatures. Shared per-annotation writers
   extracted from `AnnotationExportService` (which now delegates). Stamps
   `/NM /Contents /T /Subj /CreationDate /M`. **Finding:** PDFium can't *create* Caret
   annots (not in its creatable whitelist) — Caret is read-only; editing an existing
   caret must go through in-place dict modification, not recreate.
2. ✅ **Reconciling write-back + `PdfAnnotationStore`** (commit ce7ec3f) —
   `WriteReconciled` keyed by `/NM`: idempotent add (mint `/NM`, write back to model),
   delete-by-`/NM`, value-based edit (unchanged → untouched/lossless; changed rect-based →
   in-place; changed markup/ink → delete+recreate same `/NM`; caret → in-place only).
   `PdfAnnotationStore : IAnnotationStore` (atomic temp-file replace). Proven on the real
   Acrobat PDF: a no-op save preserves all 40 comments losslessly and idempotently.
3. ✅ **Routing + signed-PDF guard** (commit d904f16) — `CompositeAnnotationStore.Save`
   routes by writability: writable+unsigned → `PdfAnnotationStore` (annots into the PDF,
   bookmarks to a thin sidecar until PR 4); read-only/signed/no-permission → authored annots
   + bookmarks to the sidecar with a one-time `OnSidecarFallback` signal. Signatures detected
   via `FPDF_GetSignatureCount` (cached); write-permission via a non-mutating open probe.
4. ✅ **`/AP` fidelity verification** (commit a58bed4) — written annots carry no `/AP`
   stream, but `AnnotationApFidelityTests` renders each subtype with Poppler + MuPDF
   (independent of PDFium) and confirms highlight/underline/strikeout/squiggly/ink/square
   all render correctly in both. No Acrobat needed; see [[reference-ap-fidelity-testing]].
   FreeText (needs `/DA`+`/AP`) is the known exception, not authored by RailReader.

**PR 2 is complete** (read + write + reconcile + route + fidelity-verified).

### PR 2 write-side integration test (railreader2 fork, 2026-06-04)

Validated against a temp fork of railreader2 wired to local-packed Core
(`CompositeAnnotationStore.Default`):
- **Full solution builds** (GUI + CLI + Export + tests) — the model restructure,
  `AnnotationFile` setters, and new types did **not** break railreader2's compilation.
- **End-to-end write** through the packaged Core: load the real Acrobat PDF (40 annots) →
  delete a reviewer's "accuracy" highlight → add a new comment → save → reload: count back
  to 40, our comment persisted, "accuracy" gone, other reviewer comments + author intact,
  routed to the PDF (no sidecar fallback).
- **Bug found & fixed (commit d604bf4):** the incremental save produced a qpdf-"damaged"
  xref on this linearised source. Switched to a full `FPDF_SaveAsCopy` rewrite → qpdf clean
  ("No syntax or stream encoding errors"), AcroForm preserved, and the written comment
  renders in Poppler.

2. **Edit/delete semantics keyed on `/NM`** — update rewrites the matching annot in
   place; delete via `FPDFPage_RemoveAnnot`. New annots get a fresh UUID `/NM`.

3. **`CompositeAnnotationStore` routing** — writable PDF → PdfAnnotationStore; else fall
   back to JsonAnnotationStore with a UI signal ("annotations stored separately — PDF is
   read-only/signed"). Make `PdfAnnotationStore` the default wired in `DocumentController`
   /`SkiaPdfServiceFactory`.

4. **Signed-PDF guard** — detect existing signatures; force incremental update, never
   full rewrite; warn if the user edits a signed doc.

5. **Tests** — open → edit highlight text → incremental save → reopen: change persisted
   **and** other native annots + AcroForm survived (regression guard against the
   `ImportPages` data loss). Read-only-PDF path falls back to sidecar.

## PR 3 — Authoring parity: markup set, replies, review states

Outcome: users can create the full academic markup set and participate in Acrobat
review threads.

1. **Renderer (`AnnotationRenderer`, Skia)** — draw Underline/StrikeOut/Squiggly/Caret/
   FreeText; render reply threads and review-state badges in the comment panel.
2. **Authoring + tools** (`AnnotationTool`, `AnnotationInteractionHandler`) — creation
   gestures for the new types; reply-to-comment; set review state.
3. **`/RC` rich text** — preserve on read; minimal write (plain `/Contents` is enough
   for Acrobat to display; `/RC` regenerated from text).

## PR 4 — Bookmarks → PDF named destinations

Outcome: named bookmarks travel with the PDF and show in Acrobat; removes the last
reason for a parallel sidecar on writable PDFs.

1. Bind `FPDF_GetNamedDest*` (read) + a name-tree/outline writer (PDFium's bookmark
   write surface is thin — may require manipulating the catalog `/Names`/`/Outlines`).
2. Map `BookmarkEntry` ↔ named destination; migrate sidecar bookmarks on first open.
3. Fallback: if the PDF isn't writable, bookmarks stay in the (now bookmarks-only)
   sidecar.

## PR 5 — Migration + cleanup

1. One-time migration: existing `~/.config/railreader2/annotations/*.json` → write into
   the PDF on next open (when writable), then mark the sidecar migrated.
2. Keep `JsonAnnotationStore` only as the non-writable fallback + import/export-JSON
   feature; delete the parallel-store assumptions from `DocumentController`/
   `AnnotationFileManager`.
3. Update CLAUDE.md architecture notes + CHANGELOG (breaking-change entry).

---

## Cross-platform (web/mobile)

`PdfAnnotationStore` is PDFium-backed for desktop. The web/mobile build supplies a
**PdfPig**-backed `IAnnotationStore` (PdfPig reads and writes annotations in pure
managed code — fits the planned Lite/mobile path). Core is untouched; only the wired
impl differs. Confirm PdfPig's annotation-write + `/AP` generation coverage before
committing the mobile side.

## PR 1 integration test (railreader2 fork, 2026-06-04)

Validated PR 1 end-to-end against a temp fork of railreader2 wired to a local-packed
Core (`CompositeAnnotationStore.Default` swapped in at the `DocumentController` ctor and
the CLI/export `.Load` sites). Results:

- **Before:** CLI `annotations` command reported "No annotations found" (sidecar-only).
- **After:** reads **all 40** native Acrobat annotations (22 highlight + 17 text + 1 caret)
  with correct page-space geometry, `/CA` opacity, and outline-heading association.
- **Core bug found & fixed (commit 4faaf72):** `CompositeAnnotationStore.Load` ran before
  any `SkiaPdfService` was constructed, so PDFium (which Core.Pdfium relies on PDFtoImage
  to initialise) was uninitialised → **segfault**. Fixed by `FPDF_InitLibrary` +
  `PdfiumResolver.EnsureLibraryInitialized()`.
- **railreader2-side gaps confirmed (feed PR 3 cross-repo work):** the desktop CLI's
  `SerializeAnnotation` / `AnnotationOutput` DTO predates the new model — it doesn't carry
  `Contents` (reviewer comment text is read by Core but dropped by railreader2's serializer)
  and has no `Caret` case (serialised as `"unknown"`). The viewer/exporter need the new
  subtypes + author/contents/date fields.

## Cross-repo (railreader2)

Breaking: the default store changes and new model types/fields appear. The desktop
comment panel needs UI for the new subtypes, author/contents/date, reply threads, and
review states. Rebuild + test railreader2 before any NuGet bump; CHANGELOG breaking
entry is mandatory (release actions only on explicit instruction).

## Risk register

- **`/AP` fidelity** is the largest hidden cost — budget time to verify RailReader edits
  render correctly when reopened in Acrobat Pro.
- **Signed PDFs** — incremental-only; never rewrite.
- **Write permission / read-only media** — must degrade to sidecar, not fail.
- All PDFium calls under `PdfiumGate.Lock`; never mutate `DocumentState` off the UI
  thread (`IThreadMarshaller.Post`).
- PdfPig annotation-write parity for web/mobile is unverified — gate the mobile rollout
  on a spike.
