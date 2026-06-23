using RailReader.Core;
using RailReader.Core.Commands;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailReader.Renderer.Skia;

/// <summary>
/// Composes all visual layers (PDF page, colour effects, line focus blur,
/// rail overlay, search highlights, annotations, debug overlay) onto a
/// single SKBitmap. Pure SkiaSharp — no Avalonia dependency.
///
/// When SimulateViewport is set, the output is cropped to show exactly what
/// the user would see on screen at the current camera position and zoom.
/// </summary>
public static class ScreenshotCompositor
{
    private static readonly SKSamplingOptions s_sampling = new(SKCubicResampler.Mitchell);

    /// <summary>
    /// Renders a page of a document with all requested overlays. By default it composites the
    /// document's primary view; pass <paramref name="viewport"/> to composite a specific (e.g.
    /// detached/secondary) pane so its page, camera, rail, annotations and search highlights all
    /// come from that view rather than the primary facade.
    /// </summary>
    public static SKBitmap RenderPage(
        DocumentModel doc,
        DocumentController controller,
        ColourEffectShaders colourEffects,
        ScreenshotOptions options,
        Viewport? viewport = null)
    {
        // All page/camera/rail/annotation reads go through the target view (primary by default), so a
        // single-viewport caller is byte-identical and a detached pane composites its own state.
        var vp = viewport ?? doc.Primary;

        // Render PDF page at requested DPI
        int dpi = Math.Clamp(options.Dpi, 72, 600);
        using var renderedPage = doc.Pdf.RenderPage(vp.CurrentPage, dpi);
        var pageBitmap = ((SkiaRenderedPage)renderedPage).Bitmap;

        int bitmapW = pageBitmap.Width;
        int bitmapH = pageBitmap.Height;
        float pageW = (float)vp.PageWidth;
        float pageH = (float)vp.PageHeight;

        if (pageW <= 0 || pageH <= 0)
            return pageBitmap.Copy();

        float scaleX = bitmapW / pageW;
        float scaleY = bitmapH / pageH;

        // Create a surface at the full page bitmap size, draw everything in
        // bitmap coordinates, then crop to the viewport at the end.
        var info = new SKImageInfo(bitmapW, bitmapH);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException($"Failed to create surface ({bitmapW}x{bitmapH})");
        var canvas = surface.Canvas;

        using var pageImage = SKImage.FromBitmap(pageBitmap);
        var destRect = SKRect.Create(0, 0, bitmapW, bitmapH);
        var activeEffect = controller.ActiveColourEffect;
        var activeIntensity = controller.ActiveColourIntensity;

        // --- Layer 1: Page bitmap with optional line focus blur ---
        // Line focus blur and colour effect are composed together:
        // colour effect wraps the page draw (including blur passes).
        using var effectPaint = colourEffects.HasActiveEffect(activeEffect)
            ? colourEffects.CreatePaint(activeEffect, activeIntensity) : null;
        bool needsColourLayer = effectPaint is not null;

        if (needsColourLayer)
            canvas.SaveLayer(effectPaint);

        bool didLineFocusBlur = false;
        if (options.LineFocusBlur && options.LineFocusBlurIntensity > 0
            && vp.Rail is { Active: true, NavigableCount: > 0 })
        {
            var line = vp.Rail.CurrentLineInfo;
            float pad = line.Height * (float)options.LinePadding;
            // Line rect in page-point space — use the line's own horizontal extent
            // (not full page width) so only the active line's column stays sharp.
            float lineTop = line.Y - line.Height / 2f - pad;
            float lineHeight = line.Height + pad * 2;
            // Symmetric point-space inset: reuse the same LinePadding·height value
            // for the horizontal cutout so the sharp region has an even margin in
            // page points on all sides (not a width-relative pad).
            float xPad = line.Height * (float)options.LinePadding;
            float lineLeft = line.X - xPad;
            float lineWidth = line.Width + xPad * 2;
            // Convert to bitmap coordinates
            var lineRect = SKRect.Create(lineLeft * scaleX, lineTop * scaleY, lineWidth * scaleX, lineHeight * scaleY);

            float sigma = (float)(4.0 * options.LineFocusBlurIntensity) * ((scaleX + scaleY) / 2f);
            if (sigma >= 0.5f)
            {
                didLineFocusBlur = true;

                // Pass 1: Draw entire page blurred, clipping out the active line
                canvas.Save();
                canvas.ClipRect(lineRect, SKClipOperation.Difference);
                using var focusBlur = SKImageFilter.CreateBlur(sigma, sigma);
                using var focusPaint = new SKPaint { ImageFilter = focusBlur };
                canvas.SaveLayer(focusPaint);
                canvas.DrawImage(pageImage, destRect, s_sampling);
                canvas.Restore(); // layer
                canvas.Restore(); // clip

                // Pass 2: Draw just the active line sharp
                canvas.Save();
                canvas.ClipRect(lineRect);
                canvas.DrawImage(pageImage, destRect, s_sampling);
                canvas.Restore(); // clip
            }
        }

        if (!didLineFocusBlur)
            canvas.DrawImage(pageImage, destRect, s_sampling);

        if (needsColourLayer)
            canvas.Restore();

        // --- Switch to page-point coordinate space for overlays ---
        canvas.Save();
        canvas.Scale(scaleX, scaleY);

        // --- Layer 2: Rail overlay ---
        if (options.RailOverlay && vp.Rail.Active && vp.Rail.HasAnalysis)
        {
            DrawRailOverlay(canvas, vp, activeEffect.GetOverlayPalette(), options.LineFocusBlur,
                options.LineHighlightEnabled, options.LinePadding, options.LineHighlightTint, options.LineHighlightOpacity);
        }

        // --- Layer 3: Search highlights ---
        if (options.SearchHighlights)
            DrawSearchHighlights(canvas, controller, vp);

        // --- Layer 4: Annotations ---
        if (options.Annotations)
        {
            var pageAnnotations = doc.Annotations.Pages.TryGetValue(vp.CurrentPage, out var list) ? list : null;
            if (pageAnnotations is not null && pageAnnotations.Count > 0)
                AnnotationRenderer.DrawAnnotations(canvas, pageAnnotations, null);
        }

        // --- Layer 5: Debug overlay ---
        // Use THIS view's analysis variant (vp.AnalysisParams) so the debug boxes match the rail
        // overlay above (which is seated under the same variant); fall back to the canonical variant
        // if the view's exact variant isn't cached.
        if (options.DebugOverlay
            && (doc.TryGetAnalysis(vp.CurrentPage, vp.AnalysisParams, out var analysis)
                || doc.TryGetAnalysis(vp.CurrentPage, out analysis)))
            DrawDebugOverlay(canvas, analysis);

        canvas.Restore(); // undo scale

        // --- Viewport cropping ---
        if (options.SimulateViewport)
            return CropToViewport(surface, vp, options, scaleX, scaleY);

        using var snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    /// <summary>
    /// Crops the rendered full-page surface to the camera viewport.
    /// </summary>
    private static SKBitmap CropToViewport(
        SKSurface fullPageSurface,
        Viewport vp,
        ScreenshotOptions options,
        float scaleX, float scaleY)
    {
        double zoom = vp.Camera.Zoom;
        double offsetX = vp.Camera.OffsetX;
        double offsetY = vp.Camera.OffsetY;
        int vpW = options.ViewportWidth;
        int vpH = options.ViewportHeight;

        // The camera maps page coordinates to screen: screenX = pageX * zoom + offsetX
        // So visible page rect starts at: pageX = -offsetX / zoom
        // The visible page rect in page-point space:
        double visiblePageX = -offsetX / zoom;
        double visiblePageY = -offsetY / zoom;
        double visiblePageW = vpW / zoom;
        double visiblePageH = vpH / zoom;

        // Convert to bitmap coordinates
        float srcX = (float)(visiblePageX * scaleX);
        float srcY = (float)(visiblePageY * scaleY);
        float srcW = (float)(visiblePageW * scaleX);
        float srcH = (float)(visiblePageH * scaleY);
        var srcRect = SKRect.Create(srcX, srcY, srcW, srcH);

        // Output at viewport resolution (or scale proportionally)
        int outW = vpW;
        int outH = vpH;
        var outInfo = new SKImageInfo(outW, outH);
        using var outSurface = SKSurface.Create(outInfo)
            ?? throw new InvalidOperationException($"Failed to create viewport surface ({outW}x{outH})");

        using var fullImage = fullPageSurface.Snapshot();
        var dstRect = SKRect.Create(0, 0, outW, outH);
        outSurface.Canvas.DrawImage(fullImage, srcRect, dstRect, s_sampling);

        using var outImage = outSurface.Snapshot();
        return SKBitmap.FromImage(outImage);
    }

    /// <summary>
    /// Saves a bitmap as a PNG file.
    /// </summary>
    public static void SavePng(SKBitmap bitmap, string outputPath, int quality = 90)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, quality);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private static void DrawRailOverlay(SKCanvas canvas, Viewport vp, OverlayPalette palette, bool lineFocusBlur,
        bool lineHighlightEnabled = true, double linePadding = 0.2, LineHighlightTint tint = LineHighlightTint.Auto, double tintOpacity = 0.25)
    {
        if (vp.Rail.NavigableCount == 0) return;
        OverlayRenderer.DrawRailOverlays(canvas, vp.Rail.CurrentNavigableBlock, vp.Rail.CurrentLineInfo,
            (float)vp.PageWidth, (float)vp.PageHeight, palette, lineFocusBlur, lineHighlightEnabled,
            linePadding, tint, tintOpacity,
            OverlayRenderer.GetDimPaint(), OverlayRenderer.GetRevealPaint(),
            OverlayRenderer.GetOutlinePaint(), OverlayRenderer.GetLinePaint());
    }

    private static void DrawDebugOverlay(SKCanvas canvas, PageAnalysis analysis)
    {
        OverlayRenderer.DrawDebugOverlay(canvas, analysis,
            OverlayRenderer.GetDebugFont(), OverlayRenderer.GetDebugFillPaint(),
            OverlayRenderer.GetDebugStrokePaint(), OverlayRenderer.GetDebugBgPaint(),
            OverlayRenderer.GetDebugTextPaint());
    }

    private static void DrawSearchHighlights(SKCanvas canvas, DocumentController controller, Viewport vp)
    {
        // Key highlights to the SAME page being composited (issue #74) rather than a re-resolved
        // ActiveDocument or the focused view's CurrentPageSearchMatches, so a screenshot of any
        // view/page draws exactly that page's matches.
        var matches = controller.Search.MatchesForPage(vp.CurrentPage);
        if (matches is null || matches.Count == 0) return;

        int activeLocalIndex = OverlayRenderer.ComputeActiveLocalIndex(
            controller.Search.SearchMatches, matches, controller.Search.ActiveMatchIndex, vp.CurrentPage);
        OverlayRenderer.DrawSearchHighlights(canvas, matches, activeLocalIndex,
            OverlayRenderer.GetHighlightPaint(), OverlayRenderer.GetActivePaint());
    }
}
