using System.Runtime.InteropServices;
using SkiaSharp;

namespace AvaMedia.Core;

public sealed record PdfPageInfo(int Number, double Width, double Height);

/// <summary>PDFium is process-global and not thread safe. All native access shares this lock.</summary>
public static class PdfRasterizer
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static IReadOnlyList<PdfPageInfo> ReadPages(string path, CancellationToken ct = default)
    {
        lock (Gate)
        {
            ct.ThrowIfCancellationRequested();
            var document = Open(path);
            try
            {
                var pages = new List<PdfPageInfo>();
                for (var index = 0; index < FPDF_GetPageCount(document); index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = FPDF_LoadPage(document, index);
                    if (page == 0) throw new InvalidDataException("无法读取 PDF 页面。");
                    try { pages.Add(new(index + 1, FPDF_GetPageWidth(page), FPDF_GetPageHeight(page))); }
                    finally { FPDF_ClosePage(page); }
                }
                if (pages.Count == 0) throw new InvalidDataException("PDF 没有页面。");
                return pages;
            }
            finally { FPDF_CloseDocument(document); }
        }
    }

    public static SKBitmap Render(string path, int pageNumber, int longestEdge, int rotation = 0, CancellationToken ct = default)
    {
        lock (Gate)
        {
            ct.ThrowIfCancellationRequested();
            var document = Open(path);
            try
            {
                if (pageNumber < 1 || pageNumber > FPDF_GetPageCount(document)) throw new ArgumentException("页码超出范围。");
                var page = FPDF_LoadPage(document, pageNumber - 1);
                if (page == 0) throw new InvalidDataException("无法读取 PDF 页面。");
                try
                {
                    var width = FPDF_GetPageWidth(page); var height = FPDF_GetPageHeight(page);
                    if (rotation is 90 or 270) (width, height) = (height, width);
                    if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
                        throw new InvalidDataException("PDF 页面尺寸无效。");
                    var scale = Math.Clamp(longestEdge, 64, 4096) / Math.Max(width, height);
                    var bitmap = new SKBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), SKColorType.Bgra8888, SKAlphaType.Premul);
                    var target = FPDFBitmap_CreateEx(bitmap.Width, bitmap.Height, 4, bitmap.GetPixels(), bitmap.RowBytes);
                    if (target == 0) { bitmap.Dispose(); throw new InvalidDataException("无法创建 PDF 预览。"); }
                    try
                    {
                        FPDFBitmap_FillRect(target, 0, 0, bitmap.Width, bitmap.Height, 0xffffffff);
                        FPDF_RenderPageBitmap(target, page, 0, 0, bitmap.Width, bitmap.Height, rotation / 90, 1);
                        ct.ThrowIfCancellationRequested();
                        return bitmap;
                    }
                    catch { bitmap.Dispose(); throw; }
                    finally { FPDFBitmap_Destroy(target); }
                }
                finally { FPDF_ClosePage(page); }
            }
            finally { FPDF_CloseDocument(document); }
        }
    }

    private static nint Open(string path)
    {
        if (!_initialized) { FPDF_InitLibrary(); _initialized = true; }
        var document = FPDF_LoadDocument(Path.GetFullPath(path), null);
        if (document == 0) throw new InvalidDataException(FPDF_GetLastError() == 4 ? "PDF 已加密，请先解除密码保护。" : "无法打开 PDF，请检查文件是否完整。");
        return document;
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern nint FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern uint FPDF_GetLastError();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_CloseDocument(nint document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern int FPDF_GetPageCount(nint document);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern nint FPDF_LoadPage(nint document, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_ClosePage(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern double FPDF_GetPageWidth(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern double FPDF_GetPageHeight(nint page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern nint FPDFBitmap_CreateEx(int width, int height, int format, nint pixels, int stride);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDF_RenderPageBitmap(nint bitmap, nint page, int left, int top, int width, int height, int rotation, int flags);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] private static extern void FPDFBitmap_Destroy(nint bitmap);
}
