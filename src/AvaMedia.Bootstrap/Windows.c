#define UNICODE
#define _UNICODE
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <stdio.h>
#include "Host.h"

static wchar_t host[AM_PATH], base[AM_PATH], config[AM_PATH], cache[AM_PATH];
static wchar_t root[AM_PATH], log_path[AM_PATH];
static PROCESS_INFORMATION installer;
static DWORD install_status;

static int find_runtime(void) {
    if (am_find_private_runtime(cache, config, root)) return 1;
    const wchar_t *variables[] = {L"DOTNET_ROOT_X64", L"DOTNET_ROOT"};
    for (int i = 0; i < 2; i++) {
        DWORD count = GetEnvironmentVariableW(variables[i], root, AM_PATH);
        if (count > 0 && count < AM_PATH && am_runtime_usable(root, config)) return 1;
        root[0] = 0;
    }
    DWORD size = sizeof(root);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64", L"InstallLocation",
        RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, root, &size) == ERROR_SUCCESS && am_runtime_usable(root, config)) return 1;
    wchar_t folder[AM_PATH];
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_PROGRAM_FILES, NULL, 0, folder)) &&
        am_join(root, folder, L"dotnet") && am_runtime_usable(root, config)) return 1;
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_PROFILE, NULL, 0, folder)) &&
        am_join(root, folder, L".dotnet") && am_runtime_usable(root, config)) return 1;
    return 0;
}

static int start_installer(void) {
    wchar_t windows[AM_PATH], powershell[AM_PATH], script[AM_PATH], command[AM_PATH * 4];
    if (!GetWindowsDirectoryW(windows, AM_PATH) ||
        !am_join(powershell, windows, L"System32/WindowsPowerShell/v1.0/powershell.exe") ||
        !am_join(script, base, L"Install-Runtime.ps1")) return 0;
    int length = _snwprintf(command, AM_PATH * 4,
        L"\"%s\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%s\" -Application \"%s\" -RuntimeBase \"%s\" -ErrorFile \"%s\"",
        powershell, script, host, cache, log_path);
    if (length < 0 || length >= AM_PATH * 4) return 0;
    STARTUPINFOW startup = {sizeof(startup)};
    return CreateProcessW(powershell, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, base, &startup, &installer);
}

static HRESULT CALLBACK install_dialog(HWND window, UINT notification, WPARAM first, LPARAM second, LONG_PTR data) {
    (void)first; (void)second; (void)data;
    if (notification == TDN_CREATED) {
        ShowWindow(GetDlgItem(window, IDOK), SW_HIDE);
        SendMessageW(window, TDM_SET_PROGRESS_BAR_MARQUEE, TRUE, 30);
        if (!start_installer()) { install_status = GetLastError(); if (!install_status) install_status = 1; SendMessageW(window, TDM_CLICK_BUTTON, IDOK, 0); }
    } else if (notification == TDN_TIMER && installer.hProcess && WaitForSingleObject(installer.hProcess, 0) == WAIT_OBJECT_0) {
        GetExitCodeProcess(installer.hProcess, &install_status);
        CloseHandle(installer.hProcess); CloseHandle(installer.hThread);
        installer.hProcess = NULL;
        SendMessageW(window, TDM_CLICK_BUTTON, IDOK, 0);
    }
    return S_OK;
}

static void read_error(wchar_t *result, int capacity) {
    HANDLE file = CreateFileW(log_path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    char bytes[4096]; DWORD count = 0;
    if (ReadFile(file, bytes, sizeof(bytes) - 1, &count, NULL)) {
        bytes[count] = 0;
        MultiByteToWideChar(CP_UTF8, 0, bytes, -1, result, capacity);
    }
    CloseHandle(file);
}

static void unregister_player(void) {
    HKEY classes;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Classes", 0, KEY_READ | KEY_WRITE, &classes) == ERROR_SUCCESS) {
        wchar_t name[256]; DWORD index = 0, size;
        for (;;) {
            size = 256;
            if (RegEnumKeyExW(classes, index++, name, &size, NULL, NULL, NULL, NULL) != ERROR_SUCCESS) break;
            if (name[0] != L'.') continue;
            wchar_t key[512]; _snwprintf(key, 512, L"%s\\OpenWithProgids", name);
            HKEY types;
            if (RegOpenKeyExW(classes, key, 0, KEY_SET_VALUE, &types) == ERROR_SUCCESS) {
                RegDeleteValueW(types, L"AvaMedia.Player.Media"); RegCloseKey(types);
            }
        }
        RegDeleteTreeW(classes, L"AvaMedia.Player.Media");
        RegDeleteTreeW(classes, L"Applications\\AvaMedia.Desktop.exe");
        RegCloseKey(classes);
    }
    RegDeleteTreeW(HKEY_CURRENT_USER, L"Software\\AvaMedia\\Player");
    HKEY registered;
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\RegisteredApplications", 0, KEY_SET_VALUE, &registered) == ERROR_SUCCESS) {
        RegDeleteValueW(registered, L"AvaMedia.Player"); RegCloseKey(registered);
    }
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, NULL, NULL);
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE previous, PWSTR command_line, int show) {
    (void)previous; (void)command_line; (void)show;
    int argc; wchar_t **argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    DWORD length = GetModuleFileNameW(NULL, host, AM_PATH);
    if (!argv || !length || length >= AM_PATH) return 1;
    wcscpy(base, host); wchar_t *separator = wcsrchr(base, L'\\');
    if (!separator) return 1;
    *separator = 0;
    am_join(config, base, L"AvaMedia.Desktop.runtimeconfig.json");
    if (argc == 3 && !wcscmp(argv[1], L"--bootstrap-check-root")) return am_runtime_usable(argv[2], config) ? 0 : 1;
    wchar_t local[AM_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, local)) || !am_join(cache, local, L"AvaMedia/runtimes")) return 1;
    am_join(log_path, cache, L"install-error.txt");
    int available = find_runtime();
    if (argc == 2 && !wcscmp(argv[1], L"--bootstrap-check")) return available ? 0 : 1;
    if (argc == 2 && !wcscmp(argv[1], L"--unregister-player")) { unregister_player(); return 0; }
    // Installer maintenance must never show an installation prompt.
    if (!available && argc == 2 && !wcscmp(argv[1], L"--register-player")) return 0;
    if (!available) {
        INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_STANDARD_CLASSES}; InitCommonControlsEx(&controls);
        wchar_t error[4096] = {0};
        while (!available) {
            TASKDIALOG_BUTTON buttons[] = {{100, error[0] ? L"重试安装" : L"安装运行时"}, {IDCANCEL, L"退出"}};
            TASKDIALOGCONFIG prompt = {sizeof(prompt)};
            prompt.hInstance = instance; prompt.dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_SIZE_TO_CONTENT;
            prompt.pszWindowTitle = L"天池万象转换";
            prompt.pszMainInstruction = error[0] ? L"运行时安装失败" : L"需要安装 .NET 8 运行时";
            prompt.pszContent = L"安装完成后自动进入软件，无需管理员权限。";
            prompt.pszExpandedInformation = error[0] ? error : NULL;
            prompt.cButtons = 2; prompt.pButtons = buttons; prompt.nDefaultButton = 100;
            int button = IDCANCEL;
            if (FAILED(TaskDialogIndirect(&prompt, &button, NULL, NULL)) || button != 100) return 0;
            TASKDIALOGCONFIG progress = {sizeof(progress)};
            progress.hInstance = instance; progress.dwFlags = TDF_SHOW_MARQUEE_PROGRESS_BAR | TDF_CALLBACK_TIMER;
            progress.pszWindowTitle = L"天池万象转换"; progress.pszMainInstruction = L"正在安装运行时…";
            progress.pszContent = L"正在下载并校验 Microsoft 官方运行时。";
            progress.pfCallback = install_dialog;
            // A hidden completion button lets the callback dismiss the progress dialog.
            TASKDIALOG_BUTTON done = {IDOK, L"完成"}; progress.cButtons = 1; progress.pButtons = &done;
            install_status = 1;
            HRESULT status = TaskDialogIndirect(&progress, NULL, NULL, NULL);
            if (installer.hProcess) { WaitForSingleObject(installer.hProcess, INFINITE); GetExitCodeProcess(installer.hProcess, &install_status); CloseHandle(installer.hProcess); CloseHandle(installer.hThread); installer.hProcess = NULL; }
            available = SUCCEEDED(status) && install_status == 0 && find_runtime();
            if (!available) { wcscpy(error, L"下载或安装失败，请检查网络后重试。"); read_error(error, 4096); }
        }
    }
    wchar_t dll[AM_PATH]; am_join(dll, base, L"AvaMedia.Desktop.dll");
    argv[0] = dll;
    int result = am_run_application(root, host, argc, (const am_char **)argv);
    LocalFree(argv);
    return result;
}
