#ifndef AVAMEDIA_HOST_H
#define AVAMEDIA_HOST_H
#include <stddef.h>
#include <stdint.h>
#ifdef _WIN32
#include <wchar.h>
typedef wchar_t am_char;
#define AM_TEXT(s) L##s
#define AM_CALL __cdecl
#else
typedef char am_char;
#define AM_TEXT(s) s
#define AM_CALL
#endif
#define AM_PATH 4096

// Public hostfxr ABI: https://github.com/dotnet/runtime/blob/v8.0.20/src/native/corehost/hostfxr.h
typedef void *am_context;
typedef struct { size_t size; const am_char *host_path; const am_char *dotnet_root; } am_parameters;
typedef int32_t (AM_CALL *am_initialize)(int, const am_char **, const am_parameters *, am_context *);
typedef int32_t (AM_CALL *am_initialize_config)(const am_char *, const am_parameters *, am_context *);
typedef int32_t (AM_CALL *am_run)(am_context);
typedef int32_t (AM_CALL *am_close)(am_context);
typedef void (AM_CALL *am_error_writer)(const am_char *);
typedef am_error_writer (AM_CALL *am_set_error_writer)(am_error_writer);

int am_join(am_char *result, const am_char *root, const am_char *relative);
int am_runtime_usable(const am_char *root, const am_char *config);
int am_find_private_runtime(const am_char *base, const am_char *config, am_char *result);
int am_run_application(const am_char *root, const am_char *host, int argc, const am_char **argv);
#endif
