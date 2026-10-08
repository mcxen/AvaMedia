#include "Setup.h"

enum { SETUP_NEXT = 200, SETUP_BACK, SETUP_EXIT, SETUP_INSTALL, SETUP_FOLDER, SETUP_ZOOM, SETUP_SKIN = 220 };
static HWND setup_window, setup_next, setup_back, setup_exit, setup_heading, setup_steps[2];
static HWND setup_pages[3][24], setup_status, setup_progress, setup_install;
static HWND setup_output, setup_language, setup_source, setup_notify, setup_motion, setup_updates, setup_gpu;
static int setup_page_counts[3], setup_step, setup_skin, setup_available, setup_busy, setup_done, setup_dpi;
static HFONT setup_font, setup_title_font;
static wchar_t setup_root[AM_PATH], setup_pending[AM_PATH], setup_complete[AM_PATH];
static const wchar_t *setup_skin_names[] = {L"浅色", L"深色", L"Mac OS 9 · Platinum", L"Windows XP · Luna"};

static int setup_px(int value) { return MulDiv(value, setup_dpi, 96); }
static HWND setup_control(int page, const wchar_t *type, const wchar_t *text, DWORD style,
    int x, int y, int width, int height, int id) {
    HWND item = CreateWindowExW(!wcscmp(type, L"EDIT") ? WS_EX_CLIENTEDGE : 0, type, text,
        WS_CHILD | WS_VISIBLE | style, setup_px(x), setup_px(y), setup_px(width), setup_px(height),
        setup_window, (HMENU)(INT_PTR)id, GetModuleHandleW(NULL), NULL);
    SendMessageW(item, WM_SETFONT, (WPARAM)setup_font, TRUE);
    if (page >= 0) setup_pages[page][setup_page_counts[page]++] = item;
    return item;
}
static HWND setup_label(int page, const wchar_t *text, int x, int y, int width, int height) {
    return setup_control(page, L"STATIC", text, SS_LEFT, x, y, width, height, 0);
}
static HWND setup_check(const wchar_t *text, int y, int checked) {
    HWND item = setup_control(2, L"BUTTON", text, BS_AUTOCHECKBOX | WS_TABSTOP, 212, y, 500, 26, 0);
    SendMessageW(item, BM_SETCHECK, checked ? BST_CHECKED : BST_UNCHECKED, 0);
    return item;
}
static void setup_text(HDC dc, const wchar_t *text, RECT rect, unsigned int hex, UINT flags) {
    SetTextColor(dc, RGB((hex >> 16) & 255, (hex >> 8) & 255, hex & 255));
    DrawTextW(dc, text, -1, &rect, flags | DT_SINGLELINE | DT_VCENTER);
}
#include "SetupWindowsPreview.h"
static void setup_draw_skin(const DRAWITEMSTRUCT *draw) {
    int index = (int)draw->CtlID - SETUP_SKIN;
    if (index < 0 || index >= AM_SETUP_SKIN_COUNT) return;
    HDC dc = draw->hDC; RECT bounds = draw->rcItem;
    int width = bounds.right - bounds.left, height = bounds.bottom - bounds.top;
    HGDIOBJ font = SelectObject(dc, setup_font); SetBkMode(dc, TRANSPARENT);
    FillRect(dc, &bounds, GetSysColorBrush(COLOR_WINDOW));
    int selected = index == setup_skin, focused = (draw->itemState & ODS_FOCUS) != 0;
    int hovered = GetPropW(draw->hwndItem, L"AvaMedia.Hover") != NULL;
    HPEN pen = CreatePen(PS_SOLID, setup_px(selected || focused ? 2 : 1), selected || focused ? RGB(0, 120, 212) : hovered ? RGB(165, 171, 180) : RGB(215, 220, 225));
    HBRUSH brush = CreateSolidBrush(draw->itemState & ODS_SELECTED ? RGB(247, 249, 251) : RGB(255, 255, 255));
    HGDIOBJ old_pen = SelectObject(dc, pen), old_brush = SelectObject(dc, brush);
    RoundRect(dc, setup_px(2), setup_px(2), width - setup_px(2), height - setup_px(2), setup_px(16), setup_px(16));
    SelectObject(dc, old_pen); SelectObject(dc, old_brush); DeleteObject(pen); DeleteObject(brush);
    RECT image = {setup_px(12), setup_px(12), width - setup_px(12), height - setup_px(40)};
    setup_draw_image(dc, index, image);
    RECT label = {setup_px(14), height - setup_px(33), width - setup_px(38), height - setup_px(8)};
    setup_text(dc, setup_skin_names[index], label, 0x202428, DT_LEFT | DT_END_ELLIPSIS);
    if (selected) {
        RECT badge = {width - setup_px(31), height - setup_px(31), width - setup_px(14), height - setup_px(14)};
        HBRUSH fill = CreateSolidBrush(RGB(0, 120, 212));
        old_brush = SelectObject(dc, fill); old_pen = SelectObject(dc, GetStockObject(NULL_PEN));
        Ellipse(dc, badge.left, badge.top, badge.right, badge.bottom);
        SelectObject(dc, old_brush); SelectObject(dc, old_pen); DeleteObject(fill);
        setup_text(dc, L"✓", badge, 0xFFFFFF, DT_CENTER);
    }
    if (focused) { RECT focus = bounds; InflateRect(&focus, -setup_px(6), -setup_px(6)); DrawFocusRect(dc, &focus); }
    SelectObject(dc, font);
}
static void setup_show_step(void) {
    const wchar_t *headings[] = {L"准备启动", L"选择界面风格", L"设置使用偏好"};
    for (int page = 0; page < 3; page++) {
        for (int i = 0; i < setup_page_counts[page]; i++) ShowWindow(setup_pages[page][i], page == setup_step ? SW_SHOW : SW_HIDE);
        if (page > 0) {
            wchar_t text[64]; _snwprintf(text, 64, L"%s %d  %s", page == setup_step ? L"●" : L"○", page,
                page == 1 ? L"界面风格" : L"使用偏好");
            SetWindowTextW(setup_steps[page - 1], text);
        }
    }
    ShowWindow(setup_progress, setup_step == 0 && setup_busy ? SW_SHOW : SW_HIDE);
    ShowWindow(setup_install, setup_step == 0 && !setup_busy ? SW_SHOW : SW_HIDE);
    ShowWindow(setup_next, setup_step > 0 ? SW_SHOW : SW_HIDE);
    ShowWindow(setup_back, setup_step > 1 ? SW_SHOW : SW_HIDE);
    SetWindowTextW(setup_heading, headings[setup_step]);
    SetWindowTextW(setup_next, setup_step == 2 ? L"完成并进入软件" : L"下一步");
    EnableWindow(setup_next, !setup_busy && (setup_step != 0 || setup_available));
    EnableWindow(setup_back, !setup_busy && setup_step > 1);
    EnableWindow(setup_exit, !setup_busy);
    EnableWindow(setup_install, !setup_busy && !setup_available);
    if (!setup_busy) SetFocus(setup_step == 0 ? setup_install : setup_next);
}
static int setup_json_string(FILE *file, const wchar_t *text) {
    int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, NULL, 0, NULL, NULL);
    char *bytes = count ? malloc((size_t)count) : NULL;
    if (!bytes) return 0;
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, -1, bytes, count, NULL, NULL)) { free(bytes); return 0; }
    fputc('"', file);
    for (int i = 0; i < count - 1; i++) {
        unsigned char byte = (unsigned char)bytes[i];
        if (byte == '"' || byte == '\\') { fputc('\\', file); fputc(byte, file); }
        else if (byte < 32) fprintf(file, "\\u%04x", (unsigned int)byte);
        else fputc(byte, file);
    }
    fputc('"', file); free(bytes); return !ferror(file);
}
static const char *setup_bool(HWND check) { return SendMessageW(check, BM_GETCHECK, 0, 0) == BST_CHECKED ? "true" : "false"; }
static int setup_save(void) {
    wchar_t output[AM_PATH], temporary[AM_PATH]; GetWindowTextW(setup_output, output, AM_PATH);
    if (!output[0] || PathIsRelativeW(output)) return 0;
    int status = SHCreateDirectoryExW(setup_window, output, NULL);
    if (status != ERROR_SUCCESS && status != ERROR_ALREADY_EXISTS && status != ERROR_FILE_EXISTS) return 0;
    DWORD attributes = GetFileAttributesW(output);
    if (attributes == INVALID_FILE_ATTRIBUTES || !(attributes & FILE_ATTRIBUTE_DIRECTORY)) return 0;
    status = SHCreateDirectoryExW(setup_window, setup_root, NULL);
    if (status != ERROR_SUCCESS && status != ERROR_ALREADY_EXISTS && status != ERROR_FILE_EXISTS) return 0;
    if (_snwprintf(temporary, AM_PATH, L"%s.%lu.tmp", setup_pending, GetCurrentProcessId()) < 0) return 0;
    FILE *file = _wfopen(temporary, L"wb"); if (!file) return 0;
    LRESULT language = SendMessageW(setup_language, CB_GETCURSEL, 0, 0);
    fprintf(file, "{\"Theme\":\"%s\",\"Language\":\"%s\",\"OutputFolder\":", am_setup_skins[setup_skin].key,
        language == 1 ? "zh-CN" : language == 2 ? "en-US" : "system");
    int written = setup_json_string(file, output);
    fprintf(file, ",\"OutputToSource\":%s,\"NotifyComplete\":%s,\"ReduceMotion\":%s,\"CheckForUpdates\":%s,\"AutoDetectGpu\":%s}",
        setup_bool(setup_source), setup_bool(setup_notify), setup_bool(setup_motion), setup_bool(setup_updates), setup_bool(setup_gpu));
    written = written && !ferror(file); if (fclose(file) != 0) written = 0;
    if (!written || !MoveFileExW(temporary, setup_pending, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temporary); return 0; }
    // The marker is committed last; a cancelled wizard never completes setup.
    if (_snwprintf(temporary, AM_PATH, L"%s.%lu.tmp", setup_complete, GetCurrentProcessId()) < 0) return 0;
    HANDLE marker = CreateFileW(temporary, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, 0, NULL);
    if (marker == INVALID_HANDLE_VALUE) return 0;
    CloseHandle(marker);
    if (!MoveFileExW(temporary, setup_complete, MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { DeleteFileW(temporary); return 0; }
    return 1;
}
static void setup_pick_folder(void) {
    BROWSEINFOW browse = {0}; browse.hwndOwner = setup_window;
    browse.lpszTitle = L"选择默认输出目录"; browse.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
    PIDLIST_ABSOLUTE folder = SHBrowseForFolderW(&browse);
    if (folder) { wchar_t path[AM_PATH]; if (SHGetPathFromIDListW(folder, path)) SetWindowTextW(setup_output, path); CoTaskMemFree(folder); }
}
static void setup_poll_install(void) {
    wchar_t message[4096] = {0}; read_text_file(progress_path, message, 4096);
    if (wcsncmp(message, L"正在下载 ", 5) == 0) SetWindowTextW(setup_status, L"正在下载启动所需文件…");
    else if (wcsncmp(message, L"正在校验 ", 5) == 0) SetWindowTextW(setup_status, L"正在校验文件…");
    else if (wcsncmp(message, L"正在安装 ", 5) == 0) SetWindowTextW(setup_status, L"正在准备启动…");
    if (!installer.hProcess || WaitForSingleObject(installer.hProcess, 0) != WAIT_OBJECT_0) return;
    GetExitCodeProcess(installer.hProcess, &install_status);
    CloseHandle(installer.hProcess); CloseHandle(installer.hThread); installer.hProcess = NULL;
    KillTimer(setup_window, 1); setup_busy = 0;
    setup_available = install_status == 0 && read_recorded_runtime();
    SendMessageW(setup_progress, PBM_SETMARQUEE, FALSE, 0);
    if (setup_available) {
        setup_step = 1;
    } else {
        SetWindowTextW(setup_status, L"启动准备未完成，请重试。");
    }
    setup_show_step();
}
static void setup_begin_install(void) {
    if (setup_busy || setup_available) return;
    DeleteFileW(progress_path); setup_busy = 1; install_status = 1;
    SetWindowTextW(setup_status, L"正在准备首次启动，请稍候…"); setup_show_step();
    SendMessageW(setup_progress, PBM_SETMARQUEE, TRUE, 30);
    if (start_installer()) SetTimer(setup_window, 1, 200, NULL);
    else { setup_busy = 0; SetWindowTextW(setup_status, L"启动准备未完成，请重试。"); setup_show_step(); }
}
static LRESULT CALLBACK setup_proc(HWND window, UINT message, WPARAM first, LPARAM second) {
    if (message == DM_GETDEFID) return MAKELONG(setup_step == 0 && !setup_available ? SETUP_INSTALL : SETUP_NEXT, DC_HASDEFID);
    if (message == WM_DRAWITEM) { setup_draw_skin((const DRAWITEMSTRUCT *)second); return TRUE; }
    if (message == WM_CTLCOLORSTATIC) { SetBkMode((HDC)first, TRANSPARENT); return (LRESULT)GetSysColorBrush(COLOR_WINDOW); }
    if (message == WM_TIMER) { setup_poll_install(); return 0; }
    if (message == WM_CLOSE) { if (!setup_busy) DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { PostQuitMessage(0); return 0; }
    if (message == WM_COMMAND) {
        int id = LOWORD(first);
        if (id >= SETUP_SKIN && id < SETUP_SKIN + AM_SETUP_SKIN_COUNT) {
            setup_select_skin(id - SETUP_SKIN);
            if (HIWORD(first) == BN_DBLCLK) setup_zoom();
        } else if (id == SETUP_EXIT) SendMessageW(window, WM_CLOSE, 0, 0);
        else if (id == SETUP_BACK && !setup_busy && setup_step > 1) { setup_step--; setup_show_step(); }
        else if (id == SETUP_FOLDER) setup_pick_folder();
        else if (id == SETUP_ZOOM) setup_zoom();
        else if (id == SETUP_NEXT && !setup_busy && (setup_step != 0 || setup_available)) {
            if (setup_step < 2) { setup_step++; setup_show_step(); }
            else if (setup_save()) { setup_done = 1; DestroyWindow(window); }
            else MessageBoxW(window, L"无法保存配置，请检查输出目录和用户目录的写入权限。", L"首次使用配置", MB_OK | MB_ICONERROR);
        } else if (id == SETUP_INSTALL) setup_begin_install();
        return 0;
    }
    return DefWindowProcW(window, message, first, second);
}
static int run_setup(HINSTANCE instance, int available) {
    setup_available = available; setup_done = 0; setup_step = available ? 1 : 0; setup_skin = 0;
    DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
    setup_dpi = (int)GetDpiForSystem();
    HRESULT com = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    setup_font = CreateFontW(-setup_px(13), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, 0, 0, L"Segoe UI");
    setup_title_font = CreateFontW(-setup_px(22), 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, 0, 0, 0, 0, L"Segoe UI");
    WNDCLASSW type = {0}; type.lpfnWndProc = setup_proc; type.hInstance = instance;
    type.hCursor = LoadCursorW(NULL, IDC_ARROW); type.hbrBackground = GetSysColorBrush(COLOR_WINDOW);
    type.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1)); type.lpszClassName = L"AvaMedia.FirstRun";
    RegisterClassW(&type);
    DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
    RECT bounds = {0, 0, setup_px(760), setup_px(550)}; AdjustWindowRect(&bounds, style, FALSE);
    RECT work; SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
    setup_window = CreateWindowExW(WS_EX_CONTROLPARENT, type.lpszClassName, L"天池万象转换 · 首次使用配置", style,
        work.left + (work.right - work.left - bounds.right + bounds.left) / 2,
        work.top + (work.bottom - work.top - bounds.bottom + bounds.top) / 2,
        bounds.right - bounds.left, bounds.bottom - bounds.top, NULL, NULL, instance, NULL);
    if (!setup_window) { DeleteObject(setup_font); DeleteObject(setup_title_font); SetThreadDpiAwarenessContext(previous); if (SUCCEEDED(com)) CoUninitialize(); return 0; }
    setup_load_images();
    setup_label(-1, L"天池万象转换", 24, 36, 164, 28);
    setup_label(-1, L"首次使用配置", 24, 70, 164, 24);
    for (int i = 0; i < 2; i++) setup_steps[i] = setup_label(-1, L"", 24, 132 + i * 52, 168, 30);
    setup_heading = setup_label(-1, L"", 212, 30, 516, 38);
    SendMessageW(setup_heading, WM_SETFONT, (WPARAM)setup_title_font, TRUE);
    setup_back = setup_control(-1, L"BUTTON", L"上一步", BS_PUSHBUTTON | WS_TABSTOP, 212, 502, 94, 32, SETUP_BACK);
    setup_exit = setup_control(-1, L"BUTTON", L"退出", BS_PUSHBUTTON | WS_TABSTOP, 24, 502, 90, 32, SETUP_EXIT);
    setup_next = setup_control(-1, L"BUTTON", L"下一步", BS_DEFPUSHBUTTON | WS_TABSTOP, 554, 502, 174, 32, SETUP_NEXT);
    setup_status = setup_label(0, L"正在准备首次启动，请稍候…", 212, 144, 500, 66);
    setup_install = setup_control(0, L"BUTTON", L"重试", BS_PUSHBUTTON | WS_TABSTOP, 212, 240, 144, 34, SETUP_INSTALL);
    setup_progress = setup_control(0, PROGRESS_CLASSW, L"", PBS_MARQUEE, 212, 218, 500, 10, 0);
    setup_label(1, L"选择皮肤，放大查看界面细节。", 212, 80, 390, 28);
    setup_control(1, L"BUTTON", L"放大预览", BS_PUSHBUTTON | WS_TABSTOP, 616, 76, 112, 32, SETUP_ZOOM);
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) {
        HWND card = setup_control(1, L"BUTTON", setup_skin_names[i], BS_OWNERDRAW | BS_NOTIFY | WS_TABSTOP,
            212 + (i % 2) * 264, 118 + (i / 2) * 186, 252, 174, SETUP_SKIN + i);
        SetWindowSubclass(card, setup_card_proc, 1, 0);
    }
    setup_select_skin(setup_skin);
    setup_label(2, L"默认输出目录", 212, 84, 500, 24);
    wchar_t videos[AM_PATH] = {0}, output[AM_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_MYVIDEO, NULL, 0, videos))) {
        wchar_t profile[AM_PATH] = {0}; SHGetFolderPathW(NULL, CSIDL_PROFILE, NULL, 0, profile);
        am_join(videos, profile, L"Videos");
    }
    am_join(output, videos, L"AvaMedia");
    setup_output = setup_control(2, L"EDIT", output, ES_AUTOHSCROLL | WS_TABSTOP, 212, 116, 400, 30, 0);
    SendMessageW(setup_output, EM_SETLIMITTEXT, AM_PATH - 1, 0);
    setup_control(2, L"BUTTON", L"选择…", BS_PUSHBUTTON | WS_TABSTOP, 624, 115, 104, 32, SETUP_FOLDER);
    setup_source = setup_check(L"优先输出到源文件目录", 160, 0);
    setup_label(2, L"软件语言", 212, 202, 130, 26);
    setup_language = setup_control(2, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, 360, 200, 260, 130, 0);
    for (int i = 0; i < 3; i++) SendMessageW(setup_language, CB_ADDSTRING, 0, (LPARAM)(i == 0 ? L"跟随系统" : i == 1 ? L"简体中文" : L"English"));
    SendMessageW(setup_language, CB_SETCURSEL, 0, 0);
    setup_notify = setup_check(L"任务完成通知", 252, 1);
    setup_gpu = setup_check(L"自动检测 GPU 加速", 292, 1);
    setup_updates = setup_check(L"启动时检查更新", 332, 1);
    setup_motion = setup_check(L"减少界面动画", 372, 0);
    setup_label(2, L"这些选项也可以在软件设置中调整。", 212, 438, 500, 28);
    setup_show_step(); ShowWindow(setup_window, SW_SHOW); UpdateWindow(setup_window);
    if (!setup_available) setup_begin_install();
    else SetFocus(setup_next);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) {
        if (message.message == WM_KEYDOWN && message.wParam == VK_ESCAPE) { SendMessageW(setup_window, WM_CLOSE, 0, 0); continue; }
        if (!IsDialogMessageW(setup_window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    setup_free_images();
    if (SUCCEEDED(com)) CoUninitialize();
    DeleteObject(setup_font); DeleteObject(setup_title_font); SetThreadDpiAwarenessContext(previous);
    return setup_done;
}
