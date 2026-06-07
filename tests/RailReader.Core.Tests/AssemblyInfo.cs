using Xunit;

// Run test collections serially. The PDFium-backed services (RailReader.Core.Pdfium,
// exercised by DocumentControllerTests / SmoothFrameTests / etc.) and the PdfPig
// services (PdfPigServiceTests) both parse the same PDF bytes via different native/
// managed stacks; running them concurrently can deadlock the xUnit test host
// (see the note atop PdfPigServiceTests). Disabling cross-collection parallelism
// makes the suite deterministic. The real work is only a few seconds, so the cost is
// negligible.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
