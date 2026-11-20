#ifndef STUDIO_WINDOWS_FILES_H
#define STUDIO_WINDOWS_FILES_H

#include <stdbool.h>
#include <stdio.h>
#include <wchar.h>
#include <windows.h>

/* A Windows path limited to MAX_PATH UTF-16 units may take more UTF-8 bytes. */
#define UTF8_PATH_CAPACITY (MAX_PATH * 4)

static inline bool utf8_to_wide_path(const char *path, wchar_t *out, int capacity)
{
    return path && MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1,
                                       out, capacity) > 0;
}

static inline bool read_path_environment(const wchar_t *name, char *out, int capacity)
{
    wchar_t value[MAX_PATH];
    DWORD length = GetEnvironmentVariableW(name, value, MAX_PATH);
    if (!length || length >= MAX_PATH) return false;
    return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1,
                               out, capacity, NULL, NULL) > 0;
}

static inline FILE *open_utf8_file(const char *path, const wchar_t *mode)
{
    wchar_t wide[MAX_PATH];
    return utf8_to_wide_path(path, wide, MAX_PATH) ? _wfopen(wide, mode) : NULL;
}

static inline bool utf8_file_exists(const char *path)
{
    wchar_t wide[MAX_PATH];
    if (!utf8_to_wide_path(path, wide, MAX_PATH)) return false;
    DWORD attributes = GetFileAttributesW(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && !(attributes & FILE_ATTRIBUTE_DIRECTORY);
}

static inline bool ensure_parent_directory(const char *path)
{
    wchar_t wide[MAX_PATH];
    if (!path || !*path || !utf8_to_wide_path(path, wide, MAX_PATH)) return false;
    wchar_t *slash = wcsrchr(wide, L'\\');
    wchar_t *forward_slash = wcsrchr(wide, L'/');
    if (!slash || (forward_slash && forward_slash > slash)) slash = forward_slash;
    if (!slash || slash == wide || (slash == wide + 2 && wide[1] == L':')) return true;
    *slash = L'\0';
    if (CreateDirectoryW(wide, NULL)) return true;
    DWORD attributes = GetFileAttributesW(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY);
}

static inline bool build_appdata_path(char *out, int capacity, const char *relative)
{
    char appdata[UTF8_PATH_CAPACITY];
    out[0] = '\0';
    if (!read_path_environment(L"APPDATA", appdata, sizeof(appdata))) return false;
    int length = snprintf(out, (size_t)capacity, "%s%s", appdata, relative);
    if (length > 0 && length < capacity) return true;
    out[0] = '\0';
    return false;
}

#endif
