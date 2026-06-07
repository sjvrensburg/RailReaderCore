using Xunit;

// Run test collections serially. The PDFium-backed services (RailReader.Core.Pdfium)
// and the PdfPig services (PdfPigServiceTests) parse the same PDF bytes via different
// native/managed stacks; running them concurrently can deadlock the xUnit test host
// (see the note atop PdfPigServiceTests). Disabling cross-collection parallelism makes
// the suite deterministic; the real work is only a few seconds, so the cost is negligible.
//
// Why blanket rather than a scoped [Collection]: ~18 of ~20 test classes touch the
// PDFium stack (anything using TestFixtures.CreatePdfFactory / PdfTextService /
// CreateDocument), so a "conflicting" collection would contain almost the whole suite,
// and xUnit has no "run this collection in isolation from all others" primitive — a
// scoped grouping would silently re-introduce the deadlock the day a new PDFium-using
// class is added without the attribute. The blanket disable is the robust choice here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
