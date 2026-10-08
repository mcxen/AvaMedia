#include "Setup.h"

enum { SETUP_NEXT = 200, SETUP_BACK, SETUP_EXIT, SETUP_INSTALL, SETUP_FOLDER, SETUP_SKIN = 220 };
static HWND setup_window, setup_next, setup_back, setup_exit, setup_heading, setup_steps[3];
static HWND setup_pages[3][24], setup_status, setup_progress, setup_install, setup_runtime_path;
static HWND setup_output, setup_language, setup_source, setup_notify, setup_motion, setup_updates, setup_gpu;
static int setup_page_counts[3], setup_step, setup_skin, setup_available, setup_busy, setup_done, setup_dpi;
static HFONT setup_font, setup_title_font;
static wchar_t setup_root[AM_PATH], setup_pending[AM_PATH], setup_complete[AM_PATH];
static const wchar_t *setup_skin_names[] = {L"浅色", L"深色", L"Mac OS 9", L"Windows XP"};

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
static void setup_fill(HDC dc, int x, int y, int width, int height, unsigned int hex) {
    RECT rect = {x, y, x + width, y + height};
    HBRUSH brush = CreateSolidBrush(RGB((hex >> 16) & 255, (hex >> 8) & 255, hex & 255));
    FillRect(dc, &rect, brush); DeleteObject(brush);
}
static void setup_text(HDC dc, const wchar_t *text, RECT rect, unsigned int hex, UINT flags) {
    SetTextColor(dc, RGB((hex >> 16) & 255, (hex >> 8) & 255, hex & 255));
    DrawTextW(dc, text, -1, &rect, flags | DT_SINGLELINE | DT_VCENTER);
}
static void setup_draw_skin(const DRAWITEMSTRUCT *draw) {
    int index = (int)draw->CtlID - SETUP_SKIN;
    if (index < 0 || index >= AM_SETUP_SKIN_COUNT) return;
    const am_setup_skin *skin = &am_setup_skins[index];
    HDC dc = draw->hDC; RECT bounds = draw->rcItem;
    int width = bounds.right - bounds.left, height = bounds.bottom - bounds.top;
    HGDIOBJ old = SelectObject(dc, setup_font); SetBkMode(dc, TRANSPARENT);
    setup_fill(dc, 0, 0, width, height, setup_skin == index ? 0x0078D4 : 0xD8D8D8);
    int edge = setup_px(2), x = setup_px(10), y = setup_px(10), w = width - 2 * x;
    int title = setup_px(22), body = height - setup_px(51);
    setup_fill(dc, edge, edge, width - edge * 2, height - edge * 2, 0xFFFFFF);
    setup_fill(dc, x, y, w, body, skin->canvas);
    setup_fill(dc, x, y, w, title, skin->title);
    if (index == 2) {
        for (int row = 4; row < 19; row += 3) setup_fill(dc, x + setup_px(4), y + setup_px(row), w - setup_px(8), setup_px(1), 0x999999);
        setup_fill(dc, x + setup_px(54), y + setup_px(2), w - setup_px(108), title - setup_px(4), skin->title);
    }
    RECT caption = {x, y, x + w, y + title};
    setup_text(dc, L"天池万象转换", caption, index == 3 ? 0xFFFFFF : skin->text, DT_CENTER);
    int top = y + title + setup_px(4), left = x + setup_px(62);
    setup_fill(dc, x, top, setup_px(55), body - title - setup_px(4), skin->sidebar);
    RECT sidebar = {x + setup_px(6), top, left, top + setup_px(23)};
    setup_text(dc, L"转换", sidebar, skin->accent, DT_LEFT);
    sidebar.top += setup_px(25); sidebar.bottom += setup_px(25);
    setup_text(dc, L"工具集", sidebar, skin->text, DT_LEFT);
    for (int row = 0; row < 2; row++) {
        int row_y = top + row * setup_px(32);
        setup_fill(dc, left, row_y, w - setup_px(68), setup_px(28), skin->surface);
        setup_fill(dc, left + setup_px(4), row_y + setup_px(6), setup_px(16), setup_px(16), skin->accent);
        RECT label = {left + setup_px(25), row_y, x + w - setup_px(6), row_y + setup_px(24)};
        setup_text(dc, row ? L"音频.wav" : L"视频.mp4", label, skin->text, DT_LEFT);
    }
    setup_fill(dc, left, top + setup_px(69), w - setup_px(68), setup_px(4), skin->sidebar);
    setup_fill(dc, left, top + setup_px(69), (w - setup_px(68)) * 2 / 3, setup_px(4), skin->accent);
    RECT label = {0, height - setup_px(34), width, height - setup_px(4)};
    wchar_t name[64]; _snwprintf(name, 64, L"%s%s", setup_skin == index ? L"✓ " : L"", setup_skin_names[index]);
    setup_text(dc, name, label, setup_skin == index ? 0x0078D4 : 0x202428, DT_CENTER);
    if (draw->itemState & ODS_FOCUS) { RECT focus = bounds; InflateRect(&focus, -setup_px(4), -setup_px(4)); DrawFocusRect(dc, &focus); }
    SelectObject(dc, old);
}
static void setup_show_step(void) {
    const wchar_t *headings[] = {L"准备运行环境", L"选择界面风格", L"设置使用偏好"};
    for (int page = 0; page < 3; page++) {
        for (int i = 0; i < setup_page_counts[page]; i++) ShowWindow(setup_pages[page][i], page == setup_step ? SW_SHOW : SW_HIDE);
        wchar_t text[64]; _snwprintf(text, 64, L"%s %d  %s", page == setup_step ? L"●" : L"○", page + 1,
            page == 0 ? L"运行环境" : page == 1 ? L"界面风格" : L"使用偏好");
        SetWindowTextW(setup_steps[page], text);
    }
    ShowWindow(setup_progress, setup_step == 0 && setup_busy ? SW_SHOW : SW_HIDE);
    SetWindowTextW(setup_heading, headings[setup_step]);
    SetWindowTextW(setup_next, setup_step == 2 ? L"完成并进入软件" : L"下一步");
    EnableWindow(setup_next, !setup_busy && (setup_step != 0 || setup_available));
    EnableWindow(setup_back, !setup_busy && setup_step > 0);
    EnableWindow(setup_exit, !setup_busy);
    EnableWindow(setup_install, !setup_busy && !setup_available);
    SetFocus(setup_step == 0 && !setup_available ? setup_install : setup_next);
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
    if (message[0]) SetWindowTextW(setup_status, message);
    if (!installer.hProcess || WaitForSingleObject(installer.hProcess, 0) != WAIT_OBJECT_0) return;
    GetExitCodeProcess(installer.hProcess, &install_status);
    CloseHandle(installer.hProcess); CloseHandle(installer.hThread); installer.hProcess = NULL;
    KillTimer(setup_window, 1); setup_busy = 0;
    setup_available = install_status == 0 && read_recorded_runtime();
    SendMessageW(setup_progress, PBM_SETMARQUEE, FALSE, 0);
    if (setup_available) {
        SetWindowTextW(setup_status, L"✓ .NET 8 和 ASP.NET Core 8 已就绪");
        SetWindowTextW(setup_runtime_path, root); SetWindowTextW(setup_install, L"已安装");
    } else {
        wcscpy(message, L"安装失败，请检查网络后重试。"); read_error(message, 4096);
        SetWindowTextW(setup_status, message); SetWindowTextW(setup_install, L"重试安装");
    }
    setup_show_step();
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
            setup_skin = id - SETUP_SKIN;
            for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) {
                wchar_t name[64]; _snwprintf(name, 64, L"%s%s", i == setup_skin ? L"已选择：" : L"", setup_skin_names[i]);
                SetWindowTextW(GetDlgItem(window, SETUP_SKIN + i), name);
                InvalidateRect(GetDlgItem(window, SETUP_SKIN + i), NULL, TRUE);
            }
        } else if (id == SETUP_EXIT) SendMessageW(window, WM_CLOSE, 0, 0);
        else if (id == SETUP_BACK && !setup_busy && setup_step > 0) { setup_step--; setup_show_step(); }
        else if (id == SETUP_FOLDER) setup_pick_folder();
        else if (id == SETUP_NEXT && !setup_busy && (setup_step != 0 || setup_available)) {
            if (setup_step < 2) { setup_step++; setup_show_step(); }
            else if (setup_save()) { setup_done = 1; DestroyWindow(window); }
            else MessageBoxW(window, L"无法保存配置，请检查输出目录和用户目录的写入权限。", L"首次使用配置", MB_OK | MB_ICONERROR);
        } else if (id == SETUP_INSTALL && !setup_busy && !setup_available) {
            DeleteFileW(progress_path); setup_busy = 1; install_status = 1;
            SetWindowTextW(setup_status, L"正在准备安装…"); setup_show_step();
            SendMessageW(setup_progress, PBM_SETMARQUEE, TRUE, 30);
            if (start_installer()) SetTimer(window, 1, 200, NULL);
            else { setup_busy = 0; SetWindowTextW(setup_status, L"无法启动安装程序，请重试。"); setup_show_step(); }
        }
        return 0;
    }
    return DefWindowProcW(window, message, first, second);
}
static int run_setup(HINSTANCE instance, int available) {
    setup_available = available; setup_done = 0; setup_step = 0; setup_skin = 0;
    DPI_AWARENESS_CONTEXT previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
    setup_dpi = (int)GetDpiForSystem();
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
    if (!setup_window) { DeleteObject(setup_font); DeleteObject(setup_title_font); SetThreadDpiAwarenessContext(previous); return 0; }
    setup_label(-1, L"天池万象转换", 24, 36, 164, 28);
    setup_label(-1, L"首次使用配置", 24, 70, 164, 24);
    for (int i = 0; i < 3; i++) setup_steps[i] = setup_label(-1, L"", 24, 132 + i * 52, 168, 30);
    setup_heading = setup_label(-1, L"", 212, 30, 516, 38);
    SendMessageW(setup_heading, WM_SETFONT, (WPARAM)setup_title_font, TRUE);
    setup_back = setup_control(-1, L"BUTTON", L"上一步", BS_PUSHBUTTON | WS_TABSTOP, 212, 502, 94, 32, SETUP_BACK);
    setup_exit = setup_control(-1, L"BUTTON", L"退出", BS_PUSHBUTTON | WS_TABSTOP, 24, 502, 90, 32, SETUP_EXIT);
    setup_next = setup_control(-1, L"BUTTON", L"下一步", BS_DEFPUSHBUTTON | WS_TABSTOP, 554, 502, 174, 32, SETUP_NEXT);
    setup_label(0, L".NET 8 + ASP.NET Core 8", 212, 102, 500, 30);
    setup_status = setup_label(0, available ? L"✓ 运行环境已就绪" : L"需要安装运行时", 212, 144, 500, 66);
    setup_runtime_path = setup_label(0, available ? root : cache, 212, 218, 500, 58);
    setup_install = setup_control(0, L"BUTTON", available ? L"已安装" : L"安装运行时", BS_PUSHBUTTON | WS_TABSTOP, 212, 290, 144, 34, SETUP_INSTALL);
    setup_progress = setup_control(0, PROGRESS_CLASSW, L"", PBS_MARQUEE, 212, 341, 500, 10, 0);
    setup_label(0, L"安装到当前用户，无需管理员权限。", 212, 368, 500, 28);
    const wchar_t *tools[] = {L"ffmpeg.exe", L"ffprobe.exe", L"yt-dlp.exe"};
    const wchar_t *names[] = {L"FFmpeg", L"FFprobe", L"yt-dlp"};
    for (int i = 0; i < 3; i++) {
        wchar_t folder[AM_PATH], path[AM_PATH], label[100]; am_join(folder, base, L"tools"); am_join(path, folder, tools[i]);
        _snwprintf(label, 100, L"%s · %s", names[i], GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES ? L"内置" : L"缺失");
        setup_label(0, label, 212 + i * 170, 426, 162, 28);
    }
    setup_label(0, L"内置工具缺失时，请重新安装完整版本。", 212, 462, 500, 26);
    setup_label(1, L"选择下方预览，进入软件后应用该皮肤。", 212, 80, 516, 28);
    for (int i = 0; i < AM_SETUP_SKIN_COUNT; i++) setup_control(1, L"BUTTON", setup_skin_names[i], BS_OWNERDRAW | WS_TABSTOP,
        212 + (i % 2) * 264, 118 + (i / 2) * 176, 252, 164, SETUP_SKIN + i);
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
    SetFocus(!setup_available ? setup_install : setup_next);
    HRESULT com = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    MSG message;
    while (GetMessageW(&message, NULL, 0, 0) > 0) {
        if (message.message == WM_KEYDOWN && message.wParam == VK_ESCAPE) { SendMessageW(setup_window, WM_CLOSE, 0, 0); continue; }
        if (!IsDialogMessageW(setup_window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    if (SUCCEEDED(com)) CoUninitialize();
    DeleteObject(setup_font); DeleteObject(setup_title_font); SetThreadDpiAwarenessContext(previous);
    return setup_done;
}
