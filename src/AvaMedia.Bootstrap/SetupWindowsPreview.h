typedef struct { HBITMAP bitmap; UINT width, height; } setup_preview_image;
static setup_preview_image setup_images[AM_SETUP_SKIN_COUNT];
static HWND setup_viewer, setup_viewer_selector;

static void setup_load_images(void) {
    IWICImagingFactory *factory = NULL;
    if (FAILED(CoCreateInstance(&CLSID_WICImagingFactory, NULL, CLSCTX_INPROC_SERVER, &IID_IWICImagingFactory, (void **)&factory))) return;
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) {
        wchar_t folder[AM_PATH], path[AM_PATH], name[64];
        _snwprintf(name, 64, L"%hs.png", am_setup_skins[i].key);
        am_join(folder, base, L"setup-previews"); am_join(path, folder, name);
        IWICBitmapDecoder *decoder = NULL; IWICBitmapFrameDecode *frame = NULL; IWICFormatConverter *converter = NULL;
        HRESULT status = IWICImagingFactory_CreateDecoderFromFilename(factory, path, NULL, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &decoder);
        if (SUCCEEDED(status)) status = IWICBitmapDecoder_GetFrame(decoder, 0, &frame);
        if (SUCCEEDED(status)) status = IWICImagingFactory_CreateFormatConverter(factory, &converter);
        if (SUCCEEDED(status)) status = IWICFormatConverter_Initialize(converter, (IWICBitmapSource *)frame, &GUID_WICPixelFormat32bppBGR,
            WICBitmapDitherTypeNone, NULL, 0, WICBitmapPaletteTypeCustom);
        UINT width = 0, height = 0;
        if (SUCCEEDED(status)) status = IWICFormatConverter_GetSize(converter, &width, &height);
        if (SUCCEEDED(status) && width > 0 && height > 0 && width <= 8192 && height <= 8192) {
            BITMAPINFO info = {0}; info.bmiHeader.biSize = sizeof(info.bmiHeader);
            info.bmiHeader.biWidth = (LONG)width; info.bmiHeader.biHeight = -(LONG)height;
            info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
            void *pixels = NULL;
            HBITMAP bitmap = CreateDIBSection(NULL, &info, DIB_RGB_COLORS, &pixels, NULL, 0);
            if (bitmap) {
                if (SUCCEEDED(IWICFormatConverter_CopyPixels(converter, NULL, width * 4, width * height * 4, pixels)))
                    setup_images[i] = (setup_preview_image){bitmap, width, height};
                else DeleteObject(bitmap);
            }
        }
        if (converter) IWICFormatConverter_Release(converter);
        if (frame) IWICBitmapFrameDecode_Release(frame);
        if (decoder) IWICBitmapDecoder_Release(decoder);
    }
    IWICImagingFactory_Release(factory);
}
static void setup_free_images(void) {
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) { if (setup_images[i].bitmap) DeleteObject(setup_images[i].bitmap); setup_images[i].bitmap = NULL; }
}
static void setup_draw_image(HDC dc, int index, RECT area) {
    const setup_preview_image *image = &setup_images[index];
    if (!image->bitmap) { setup_text(dc, L"预览不可用", area, 0x737B84, DT_CENTER); return; }
    int width = area.right - area.left, height = area.bottom - area.top;
    int fit_width = width, fit_height = (int)((long long)width * image->height / image->width);
    if (fit_height > height) { fit_height = height; fit_width = (int)((long long)height * image->width / image->height); }
    int x = area.left + (width - fit_width) / 2, y = area.top + (height - fit_height) / 2;
    HDC source = CreateCompatibleDC(dc); HGDIOBJ original = SelectObject(source, image->bitmap);
    int mode = SetStretchBltMode(dc, HALFTONE); POINT origin; SetBrushOrgEx(dc, 0, 0, &origin);
    StretchBlt(dc, x, y, fit_width, fit_height, source, 0, 0, (int)image->width, (int)image->height, SRCCOPY);
    SetBrushOrgEx(dc, origin.x, origin.y, NULL); SetStretchBltMode(dc, mode);
    SelectObject(source, original); DeleteDC(source);
}
static void setup_select_skin(int index) {
    setup_skin = index;
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) {
        wchar_t name[80]; _snwprintf(name, 80, L"%s%s", i == setup_skin ? L"已选择：" : L"", setup_skin_names[i]);
        SetWindowTextW(GetDlgItem(setup_window, SETUP_SKIN + i), name);
        InvalidateRect(GetDlgItem(setup_window, SETUP_SKIN + i), NULL, TRUE);
    }
    EnableWindow(GetDlgItem(setup_window, SETUP_ZOOM), setup_images[index].bitmap != NULL);
}
static LRESULT CALLBACK setup_card_proc(HWND window, UINT message, WPARAM first, LPARAM second, UINT_PTR subclass, DWORD_PTR data) {
    (void)data;
    if (message == WM_MOUSEMOVE && !GetPropW(window, L"AvaMedia.Hover")) {
        SetPropW(window, L"AvaMedia.Hover", (HANDLE)(INT_PTR)1);
        TRACKMOUSEEVENT track = {sizeof(track), TME_LEAVE, window, 0}; TrackMouseEvent(&track);
        InvalidateRect(window, NULL, TRUE);
    } else if (message == WM_MOUSELEAVE) { RemovePropW(window, L"AvaMedia.Hover"); InvalidateRect(window, NULL, TRUE); }
    else if (message == WM_NCDESTROY) { RemovePropW(window, L"AvaMedia.Hover"); RemoveWindowSubclass(window, setup_card_proc, subclass); }
    return DefSubclassProc(window, message, first, second);
}
static LRESULT CALLBACK setup_viewer_proc(HWND window, UINT message, WPARAM first, LPARAM second) {
    if (message == WM_GETMINMAXINFO) {
        MINMAXINFO *limits = (MINMAXINFO *)second;
        limits->ptMinTrackSize.x = setup_px(640); limits->ptMinTrackSize.y = setup_px(440);
        return 0;
    }
    if (message == WM_COMMAND) {
        if (LOWORD(first) == 300 && HIWORD(first) == CBN_SELCHANGE) {
            int index = (int)SendMessageW(setup_viewer_selector, CB_GETCURSEL, 0, 0);
            if (index >= 0 && index < AM_SETUP_SKIN_COUNT) { setup_select_skin(index); InvalidateRect(window, NULL, TRUE); }
        } else if (LOWORD(first) == IDCANCEL || LOWORD(first) == IDOK) DestroyWindow(window);
        return 0;
    }
    if (message == WM_SIZE) {
        RECT client; GetClientRect(window, &client);
        MoveWindow(GetDlgItem(window, IDOK), client.right - setup_px(110), setup_px(14), setup_px(90), setup_px(30), TRUE);
        InvalidateRect(window, NULL, TRUE); return 0;
    }
    if (message == WM_PAINT) {
        PAINTSTRUCT paint; HDC dc = BeginPaint(window, &paint); RECT client; GetClientRect(window, &client);
        HGDIOBJ font = SelectObject(dc, setup_font); SetBkMode(dc, TRANSPARENT);
        FillRect(dc, &client, GetSysColorBrush(COLOR_WINDOW));
        RECT image = {setup_px(20), setup_px(64), client.right - setup_px(20), client.bottom - setup_px(20)};
        if (image.right > image.left && image.bottom > image.top) setup_draw_image(dc, setup_skin, image);
        SelectObject(dc, font); EndPaint(window, &paint); return 0;
    }
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { setup_viewer = NULL; return 0; }
    return DefWindowProcW(window, message, first, second);
}
static void setup_zoom(void) {
    if (!setup_images[setup_skin].bitmap || setup_viewer) return;
    HINSTANCE instance = GetModuleHandleW(NULL);
    WNDCLASSW type = {0}; type.lpfnWndProc = setup_viewer_proc; type.hInstance = instance;
    type.hCursor = LoadCursorW(NULL, IDC_ARROW); type.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    type.lpszClassName = L"AvaMedia.SkinPreview"; RegisterClassW(&type);
    RECT work; SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    int width = min(setup_px(1120), work.right - work.left - setup_px(48));
    int height = min(setup_px(760), work.bottom - work.top - setup_px(48));
    setup_viewer = CreateWindowExW(WS_EX_CONTROLPARENT, type.lpszClassName, L"皮肤预览", WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN,
        work.left + (work.right - work.left - width) / 2, work.top + (work.bottom - work.top - height) / 2,
        width, height, setup_window, NULL, instance, NULL);
    if (!setup_viewer) return;
    setup_viewer_selector = CreateWindowExW(0, L"COMBOBOX", L"", WS_CHILD | WS_VISIBLE | WS_TABSTOP | CBS_DROPDOWNLIST,
        setup_px(20), setup_px(14), setup_px(300), setup_px(160), setup_viewer, (HMENU)(INT_PTR)300, instance, NULL);
    SendMessageW(setup_viewer_selector, WM_SETFONT, (WPARAM)setup_font, TRUE);
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) SendMessageW(setup_viewer_selector, CB_ADDSTRING, 0, (LPARAM)setup_skin_names[i]);
    SendMessageW(setup_viewer_selector, CB_SETCURSEL, (WPARAM)setup_skin, 0);
    RECT client; GetClientRect(setup_viewer, &client);
    HWND close = CreateWindowExW(0, L"BUTTON", L"完成", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
        client.right - setup_px(110), setup_px(14), setup_px(90), setup_px(30), setup_viewer, (HMENU)(INT_PTR)IDOK, instance, NULL);
    SendMessageW(close, WM_SETFONT, (WPARAM)setup_font, TRUE);
    EnableWindow(setup_window, FALSE); ShowWindow(setup_viewer, SW_SHOW); SetFocus(setup_viewer_selector);
    MSG message;
    while (setup_viewer) {
        int status = GetMessageW(&message, NULL, 0, 0);
        if (status <= 0) { if (status == 0) PostQuitMessage((int)message.wParam); break; }
        if (message.message == WM_KEYDOWN && message.wParam == VK_ESCAPE) { SendMessageW(setup_viewer, WM_CLOSE, 0, 0); continue; }
        if (!IsDialogMessageW(setup_viewer, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    EnableWindow(setup_window, TRUE); SetActiveWindow(setup_window); SetFocus(GetDlgItem(setup_window, SETUP_ZOOM));
}
