#include "Host.h"
#include <stdio.h>
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#define am_format _snwprintf
#define am_length wcslen
#define am_scan swscanf
#define am_load(path) LoadLibraryExW(path, NULL, LOAD_WITH_ALTERED_SEARCH_PATH)
#define am_symbol(module, name) GetProcAddress(module, name)
#define am_unload(module) FreeLibrary(module)
typedef HMODULE am_module;
#else
#include <dirent.h>
#include <dlfcn.h>
#define am_format snprintf
#define am_length strlen
#define am_scan sscanf
#define am_load(path) dlopen(path, RTLD_NOW | RTLD_LOCAL)
#define am_symbol(module, name) dlsym(module, name)
#define am_unload(module) dlclose(module)
typedef void *am_module;
#endif

int am_join(am_char *result, const am_char *root, const am_char *relative) {
    int count = am_format(result, AM_PATH, AM_TEXT("%s/%s"), root, relative);
    return count >= 0 && count < AM_PATH;
}

static int am_version(const am_char *name, int *major, int *minor, int *patch) {
    am_char tail;
    return am_scan(name, AM_TEXT("%d.%d.%d%c"), major, minor, patch, &tail) == 3;
}

static int am_fxr_path(const am_char *root, am_char *result) {
    am_char folder[AM_PATH], best[AM_PATH] = {0};
    int highest_major = -1, highest_minor = -1, highest_patch = -1;
    if (!am_join(folder, root, AM_TEXT("host/fxr"))) return 0;
#ifdef _WIN32
    am_char pattern[AM_PATH]; WIN32_FIND_DATAW entry;
    if (!am_join(pattern, folder, L"*")) return 0;
    HANDLE search = FindFirstFileW(pattern, &entry);
    if (search == INVALID_HANDLE_VALUE) return 0;
    do {
        const am_char *name = entry.cFileName;
#else
    DIR *search = opendir(folder);
    if (!search) return 0;
    struct dirent *entry;
    while ((entry = readdir(search))) {
        const am_char *name = entry->d_name;
#endif
        int major, minor, patch;
        if (am_version(name, &major, &minor, &patch) &&
            (major > highest_major || (major == highest_major && minor > highest_minor) ||
             (major == highest_major && minor == highest_minor && patch > highest_patch))) {
            highest_major = major; highest_minor = minor; highest_patch = patch;
            am_format(best, AM_PATH, AM_TEXT("%s"), name);
        }
#ifdef _WIN32
    } while (FindNextFileW(search, &entry));
    FindClose(search);
#else
    }
    closedir(search);
#endif
    if (!best[0] || !am_join(result, folder, best)) return 0;
    am_char version_folder[AM_PATH];
    am_format(version_folder, AM_PATH, AM_TEXT("%s"), result);
#ifdef _WIN32
    return am_join(result, version_folder, L"hostfxr.dll");
#else
    return am_join(result, version_folder, "libhostfxr.dylib");
#endif
}

static void AM_CALL am_ignore_error(const am_char *message) { (void)message; }

int am_runtime_usable(const am_char *root, const am_char *config) {
    if (!root || !root[0]) return 0;
    am_char fxr[AM_PATH];
    if (!am_fxr_path(root, fxr)) return 0;
    am_module module = am_load(fxr);
    if (!module) return 0; // Includes an architecture mismatch.
    am_initialize_config initialize = (am_initialize_config)am_symbol(module, "hostfxr_initialize_for_runtime_config");
    am_close close = (am_close)am_symbol(module, "hostfxr_close");
    am_set_error_writer set_error = (am_set_error_writer)am_symbol(module, "hostfxr_set_error_writer");
    int usable = 0;
    if (initialize && close && set_error) {
        am_error_writer previous = set_error(am_ignore_error);
        am_parameters parameters = {sizeof(am_parameters), NULL, root};
        am_context context = NULL;
        int32_t status = initialize(config, &parameters, &context);
        usable = status >= 0 && context != NULL;
        if (context) close(context);
        set_error(previous);
    }
    am_unload(module);
    return usable;
}

int am_find_private_runtime(const am_char *base, const am_char *config, am_char *result) {
#ifdef _WIN32
    am_char pattern[AM_PATH]; WIN32_FIND_DATAW entry;
    if (!am_join(pattern, base, L"*")) return 0;
    HANDLE search = FindFirstFileW(pattern, &entry);
    if (search == INVALID_HANDLE_VALUE) return 0;
    do {
        const am_char *name = entry.cFileName;
#else
    DIR *search = opendir(base);
    if (!search) return 0;
    struct dirent *entry;
    while ((entry = readdir(search))) {
        const am_char *name = entry->d_name;
#endif
        // Staging folders begin with a dot and must never become launch candidates.
        if (name[0] != (am_char)'.' && am_join(result, base, name) && am_runtime_usable(result, config)) {
#ifdef _WIN32
            FindClose(search);
#else
            closedir(search);
#endif
            return 1;
        }
#ifdef _WIN32
    } while (FindNextFileW(search, &entry));
    FindClose(search);
#else
    }
    closedir(search);
#endif
    return 0;
}

int am_run_application(const am_char *root, const am_char *host, int argc, const am_char **argv) {
    am_char fxr[AM_PATH];
    if (!am_fxr_path(root, fxr)) return 1;
    am_module module = am_load(fxr);
    if (!module) return 1;
    am_initialize initialize = (am_initialize)am_symbol(module, "hostfxr_initialize_for_dotnet_command_line");
    am_run run = (am_run)am_symbol(module, "hostfxr_run_app");
    am_close close = (am_close)am_symbol(module, "hostfxr_close");
    if (!initialize || !run || !close) { am_unload(module); return 1; }
    am_parameters parameters = {sizeof(am_parameters), host, root};
    am_context context = NULL;
    int32_t status = initialize(argc, argv, &parameters, &context);
    if (status >= 0 && context) status = run(context);
    if (context) close(context);
    // Keep the host module loaded until process exit after CoreCLR has run.
    return status;
}
