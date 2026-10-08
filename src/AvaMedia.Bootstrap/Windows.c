#define COBJMACROS
#define UNICODE
#define _UNICODE
#define _WIN32_WINNT 0x0A00
// The native wizard uses the Windows 10 1607 thread DPI APIs.
#define NTDDI_VERSION 0x0A000002
#include <windows.h>
#include <commctrl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <wincodec.h>
#include <stdio.h>
#include <stdlib.h>
#include "Host.h"

static wchar_t host[AM_PATH], base[AM_PATH], config[AM_PATH], cache[AM_PATH];
static wchar_t root[AM_PATH], log_path[AM_PATH], progress_path[AM_PATH];
static PROCESS_INFORMATION installer;
static DWORD install_status;

static int read_recorded_runtime(void) {
    DWORD size = sizeof(root);
    return RegGetValueW(HKEY_CURRENT_USER, L"Software\\AvaMedia\\Runtime", L"win-x64",
        RRF_RT_REG_SZ, NULL, root, &size) == ERROR_SUCCESS && root[0];
}

static int record_runtime(const wchar_t *path) {
    HKEY key;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\AvaMedia\\Runtime", 0, NULL, 0,
        KEY_SET_VALUE, NULL, &key, NULL) != ERROR_SUCCESS) return 0;
    DWORD size = (DWORD)((wcslen(path) + 1) * sizeof(wchar_t));
    LSTATUS status = RegSetValueExW(key, L"win-x64", 0, REG_SZ, (const BYTE *)path, size);
    RegCloseKey(key);
    return status == ERROR_SUCCESS;
}

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
        L"\"%s\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%s\" -Application \"%s\" -RuntimeBase \"%s\" -ErrorFile \"%s\" -ProgressFile \"%s\"",
        powershell, script, host, cache, log_path, progress_path);
    if (length < 0 || length >= AM_PATH * 4) return 0;
    STARTUPINFOW startup = {sizeof(startup)};
    return CreateProcessW(powershell, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, base, &startup, &installer);
}

static void read_text_file(const wchar_t *path, wchar_t *result, int capacity) {
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (file == INVALID_HANDLE_VALUE) return;
    char bytes[4096]; DWORD count = 0;
    if (ReadFile(file, bytes, sizeof(bytes) - 1, &count, NULL)) {
        bytes[count] = 0;
        MultiByteToWideChar(CP_UTF8, 0, bytes, -1, result, capacity);
    }
    CloseHandle(file);
}
static void read_error(wchar_t *result, int capacity) { read_text_file(log_path, result, capacity); }
#include "SetupWindows.h"

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
    am_join(progress_path, cache, L"install-progress.txt");
    am_join(setup_root, local, L"AvaMedia");
    am_join(setup_pending, setup_root, L"setup-pending.json");
    am_join(setup_complete, setup_root, L"setup-complete");
    if (argc == 3 && !wcscmp(argv[1], L"--bootstrap-record-root")) return record_runtime(argv[2]) ? 0 : 1;
    if (argc == 2 && !wcscmp(argv[1], L"--unregister-player")) { unregister_player(); return 0; }
    // A recorded installation goes straight to the normal runtime loader. No preflight probe.
    int available = read_recorded_runtime();
    if (!available) {
        available = find_runtime();
        if (available && !record_runtime(root)) {
            MessageBoxW(NULL, L"无法保存运行时安装信息。", L"天池万象转换", MB_OK | MB_ICONERROR);
            return 1;
        }
    }
    if (argc == 2 && !wcscmp(argv[1], L"--bootstrap-check")) return available ? 0 : 1;
    // Installer maintenance must never show an installation prompt.
    if (!available && argc == 2 && !wcscmp(argv[1], L"--register-player")) return 0;
    int force_setup = 0, maintenance = 0, capture = 0;
    for (int i = 1; i < argc; i++) {
        if (!wcscmp(argv[i], L"--setup")) {
            force_setup = 1;
            for (int j = i; j + 1 < argc; j++) argv[j] = argv[j + 1];
            argc--; i--;
        } else if (!wcscmp(argv[i], L"--register-player")) maintenance = 1;
        else if (!wcscmp(argv[i], L"--capture")) capture = 1;
    }
    if (!maintenance && (!available || force_setup || (!capture && GetFileAttributesW(setup_complete) == INVALID_FILE_ATTRIBUTES))) {
        INITCOMMONCONTROLSEX controls = {sizeof(controls), ICC_STANDARD_CLASSES | ICC_PROGRESS_CLASS}; InitCommonControlsEx(&controls);
        if (!run_setup(instance, available)) { LocalFree(argv); return 0; }
    }
    wchar_t dll[AM_PATH]; am_join(dll, base, L"AvaMedia.Desktop.dll");
    argv[0] = dll;
    int result = am_run_application(root, host, argc, (const am_char **)argv);
    LocalFree(argv);
    return result;
}
